using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using BabyMonitor.Core.Monitor;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Log = BabyMonitor.App.Services.Logging.Log;

namespace BabyMonitor.App.Services;

/// <summary>
/// The picture, on Windows: the camera's H.265 goes into **our own decoder**, which travels in the
/// app's folder.
///
/// **Why the app carries a decoder.** Windows ships no H.265 decoder; Media Foundation only gets one
/// from the HEVC Video Extensions, a separate Store download. So the picture used to depend on a parent
/// finding a Store page — and on a PC with no Store, or no account signed into it, the picture was never
/// coming at all. The camera speaks H.265 and nothing else (measured: every quality it offers, at every
/// resolution), so there was nothing to fall back to. Now there is: libde265, 772 KB, built from source
/// and statically linked, needing nothing installed (DESK-22).
///
/// Everything here is best-effort by contract (LIVE-7): nothing it does may throw into the monitor. A
/// black picture is a disappointment; a dead alarm is the thing this project exists to prevent. So every
/// failure below ends as "no picture, say so, keep monitoring" — never as an exception going upwards.
/// </summary>
public sealed class SoftwareHevcVideoRenderer : IVideoRenderer, IDisposable
{
    /// <summary>
    /// DESK-27: how many decodes may say "buffer full" and hand back nothing before the decoder is
    /// declared wedged. At the camera's ~20 frames a second this is well under a second — long enough
    /// that a moment's contention is not mistaken for a wedge, short enough that nobody is looking at a
    /// photograph while we make up our mind.
    /// </summary>
    private const int WedgeStreakLimit = 10;

    private readonly object _lock = new();
    private readonly DispatcherQueue _ui;
    private readonly Action<WriteableBitmap>? _onSurfaceReady;

    private IntPtr _ctx;
    private byte[]? _parameterSets; // Annex-B VPS+SPS+PPS, prepended to every keyframe
    private WriteableBitmap? _bitmap;

    // DESK-27: what the decoder was built from, kept so it can be built again from scratch. See
    // RebuildIfWedged — a decoder that has wedged cannot be talked round, only replaced.
    private byte[]? _cfgVps;
    private byte[]? _cfgSps;
    private byte[]? _cfgPps;
    private int _cfgWidth;
    private int _cfgHeight;

    /// <summary>DESK-27: decodes that said "buffer full" while handing back no picture at all.</summary>
    private int _wedgeStreak;

    private byte[]? _ready;         // the newest frame, waiting for the UI thread
    private int _readyWidth;        // its size — carried with it, so the surface can follow the frame
    private int _readyHeight;
    private bool _posted;           // a UI update is already queued; do not pile more on
    private bool _torn;

    public SoftwareHevcVideoRenderer(DispatcherQueue ui, Action<WriteableBitmap>? onSurfaceReady = null)
    {
        _ui = ui;
        _onSurfaceReady = onSurfaceReady;
    }

    /// <summary>DESK-22: the decoder could not be used at all. The shell says so; monitoring carries on.</summary>
    public event Action<string>? DecoderFailed;

    /// <summary>Build a decoder for a stream of this size. False means no picture this session.</summary>
    public bool Configure(byte[] vps, byte[] sps, byte[] pps, int width, int height)
    {
        try
        {
            ReleaseDecoder();

            lock (_lock)
            {
                _torn = false;

                var ctx = Libde265.de265_new_decoder();
                if (ctx == IntPtr.Zero)
                {
                    Fail("libde265 would not create a decoder");
                    return false;
                }

                // Worker threads make the difference between a picture and a slideshow at the camera's
                // full resolution. Capped: a baby monitor runs all night beside someone trying to sleep,
                // and it has no business taking the machine over to draw a cot.
                // Joining a live stream means the first frames arrive without the references they were
                // coded against. Left to itself the decoder hands those over anyway, and they land on
                // screen as a grey, blocky half-picture of the cot — which reads as "the baby is fine"
                // just as convincingly as the real thing does. Wait for a frame worth showing instead.
                Libde265.de265_set_parameter_bool(ctx, Libde265.DE265_DECODER_PARAM_SUPPRESS_FAULTY_PICTURES, 1);

                var workers = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
                var started = Libde265.de265_start_worker_threads(ctx, workers);
                if (started != Libde265.DE265_OK)
                {
                    // Not fatal: without workers it decodes on this thread, which is slower but correct.
                    Log.Warn("video", $"libde265 worker threads unavailable ({Libde265.ErrorText(started)}); decoding single-threaded");
                }

                _ctx = ctx;
                _wedgeStreak = 0;

                // DESK-27: kept so the decoder can be built again without the camera's help — a wedged
                // one is replaced, and waiting for a fresh Configure would mean waiting for a reconnect.
                _cfgVps = vps;
                _cfgSps = sps;
                _cfgPps = pps;
                _cfgWidth = width;
                _cfgHeight = height;

                // The camera sends VPS/SPS/PPS in their own access units, so the decoder never sees them
                // in the stream we forward — every keyframe gets them prepended instead. A decoder that
                // started late has nothing to start from otherwise.
                var startCode = new byte[] { 0, 0, 0, 1 };
                _parameterSets = startCode
                    .Concat(vps).Concat(startCode)
                    .Concat(sps).Concat(startCode)
                    .Concat(pps)
                    .ToArray();
            }

            // The surface is not made here. It is made by the first frame that needs it, at the size the
            // decoder actually produced (see Draw) — so nothing set up now can be lost, and the picture
            // cannot end up waiting for a surface that never arrived.
            Log.Info("video", $"libde265 H.265 decoder built ({width}x{height}, {Math.Clamp(Environment.ProcessorCount / 2, 1, 4)} threads)");
            return true;
        }
        catch (DllNotFoundException e)
        {
            // The decoder ships beside the exe; if it is missing the build was wrong, and the honest
            // answer is the same one DESK-22 always gave: no picture, said out loud, monitoring intact.
            Fail($"the bundled H.265 decoder is missing: {e.Message}");
            return false;
        }
        catch (Exception e)
        {
            Fail($"could not build the H.265 decoder: {e.Message}");
            return false;
        }
    }

    /// <summary>One Annex-B access unit. Never throws (LIVE-7).</summary>
    public void Decode(byte[] annexB, long ptsMs)
    {
        try
        {
            IntPtr ctx;
            byte[]? sets;
            var isKeyframe = Hevc.IsKeyframe(annexB);
            lock (_lock)
            {
                if (_torn || _ctx == IntPtr.Zero)
                {
                    return;
                }

                ctx = _ctx;
                sets = _parameterSets;
            }

            var data = isKeyframe && sets != null
                ? sets.Concat(annexB).ToArray()
                : annexB;

            var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                var pushed = Libde265.de265_push_data(ctx, pinned.AddrOfPinnedObject(), data.Length, ptsMs, IntPtr.Zero);
                if (pushed != Libde265.DE265_OK)
                {
                    Log.Warn("video", $"libde265 rejected an access unit: {Libde265.ErrorText(pushed)}");
                    return;
                }
            }
            finally
            {
                pinned.Free();
            }

            // Decode until it stops asking, taking finished pictures out on EVERY pass.
            //
            // Draining only at the end is not a tidier way to write the same loop: the decoder's output
            // queue is small, and a picture left sitting in it is a slot it cannot reuse. Fill the queue
            // and it stops decoding entirely ("DPB/output queue full") — the picture then stutters or
            // stops for a reason that is nothing to do with the camera and entirely our own doing.
            //
            // Bounded, because a decoder that somehow never finishes must not take the feed's thread with
            // it: video is best-effort (LIVE-7), and audio is what monitoring means.
            var drewSomething = false;
            var sawBufferFull = false;
            for (var pass = 0; pass < 64; pass++)
            {
                var err = Libde265.de265_decode(ctx, out var more);
                drewSomething |= DrainPictures(ctx);

                if (err == Libde265.DE265_ERROR_WAITING_FOR_INPUT_DATA)
                {
                    break; // it has had everything this access unit carries
                }

                if (err == Libde265.DE265_ERROR_IMAGE_BUFFER_FULL)
                {
                    // "DPB full, extract some images before continuing" — which we just tried to do. If
                    // nothing came out, that instruction cannot be followed, and see RebuildIfWedged.
                    sawBufferFull = true;
                    if (!drewSomething)
                    {
                        break;
                    }

                    continue; // we did empty the queue; let it carry on
                }

                if (err != Libde265.DE265_OK)
                {
                    // A broken packet is a dropped frame, never a dropped session: the stream recovers at
                    // the next keyframe, and PROTO-23's rule (a bad packet is skipped) holds here too.
                    Log.Warn("video", $"libde265 could not decode a frame: {Libde265.ErrorText(err)}");
                    break;
                }

                if (more == 0)
                {
                    break;
                }
            }

            RebuildIfWedged(drewSomething, sawBufferFull);
        }
        catch (Exception e)
        {
            Log.Warn("video", $"could not decode a frame: {e.Message}");
        }
    }

    /// <summary>
    /// DESK-27: **a decoder that has wedged is replaced, because it cannot be talked round.**
    ///
    /// libde265 refuses to decode once its picture buffer is full ("extract some images before
    /// continuing") — and it will not give the images back. `param_suppress_faulty_pictures`, which
    /// DESK-26 turns on so a half-decoded cot never reaches the screen, drops faulty pictures on the
    /// floor *instead of* queueing them for output (decctx.cc, push_picture_to_output_queue): they are
    /// never handed over, so they are never released, so their buffer slots are never freed. Lose enough
    /// reference frames — a dropped backlog (LIVE-8) will do it — and the buffer fills with pictures
    /// nobody can extract. Full, so it will not decode; silent, so it will not drain. It never recovers.
    ///
    /// Measured, and it is not an edge case: the picture froze about 25 seconds into every session and
    /// stayed frozen, while frames arrived at 20/s, keyframes among them, and nothing was logged. Audio
    /// played on and the feed said Live — a photograph of a sleeping baby, held up for as long as anyone
    /// cared to look at it. That is the exact lie this project exists to prevent.
    ///
    /// So the signature is watched for directly — "buffer full" while handing back nothing — and the
    /// answer is a new decoder. The next keyframe carries the parameter sets and the picture returns.
    /// The streak is what keeps this honest: a decoder still waiting for its first keyframe says
    /// WAITING_FOR_INPUT_DATA, never this, so a healthy start cannot be mistaken for a wedge.
    /// </summary>
    private void RebuildIfWedged(bool drewSomething, bool sawBufferFull)
    {
        if (drewSomething || !sawBufferFull)
        {
            _wedgeStreak = 0;
            return;
        }

        if (++_wedgeStreak < WedgeStreakLimit)
        {
            return;
        }

        byte[]? vps, sps, pps;
        int w, h;
        lock (_lock)
        {
            vps = _cfgVps;
            sps = _cfgSps;
            pps = _cfgPps;
            w = _cfgWidth;
            h = _cfgHeight;
        }

        if (vps == null || sps == null || pps == null)
        {
            return; // nothing to rebuild from; the next Configure will sort it out
        }

        Log.Warn(
            "video",
            $"the decoder wedged — its picture buffer is full and it will hand nothing back " +
            $"({_wedgeStreak} decodes). Building a new one; the picture returns at the next keyframe.");
        _wedgeStreak = 0;
        Configure(vps, sps, pps, w, h);
    }

    public void TearDown()
    {
        ReleaseDecoder();
        Log.Info("video", "video pipeline torn down");
    }

    public void Dispose() => ReleaseDecoder();

    /// <summary>
    /// Take every finished picture out, and give each one back — a picture not released is a stall.
    /// Returns whether anything came out at all, which is how a wedged decoder is recognised (DESK-27).
    /// </summary>
    private bool DrainPictures(IntPtr ctx)
    {
        var any = false;
        while (true)
        {
            var image = Libde265.de265_get_next_picture(ctx);
            if (image == IntPtr.Zero)
            {
                return any;
            }

            any = true;
            try
            {
                var width = Libde265.de265_get_image_width(image, 0);
                var height = Libde265.de265_get_image_height(image, 0);
                var frame = ToBgra(image);
                if (frame != null)
                {
                    Present(frame, width, height);
                }
            }
            finally
            {
                Libde265.de265_release_next_picture(ctx);
            }
        }
    }

    /// <summary>
    /// YUV 4:2:0 to BGRA, which is the only thing a WriteableBitmap will take.
    ///
    /// Limited-range video, and the matrix follows the picture's size the way every player picks it:
    /// BT.709 for HD and above, BT.601 below. Getting this wrong is not a crash — it is a baby who looks
    /// slightly green at 3am, which is its own small cruelty.
    /// </summary>
    private byte[]? ToBgra(IntPtr image)
    {
        if (Libde265.de265_get_chroma_format(image) != Libde265.DE265_CHROMA_420 ||
            Libde265.de265_get_bits_per_pixel(image, 0) != 8)
        {
            // The C100 sends 8-bit 4:2:0 and nothing else. Anything else is a stream we did not plan
            // for: say so once rather than paint a mess.
            Fail("this camera's video is in a format the decoder cannot draw");
            return null;
        }

        var width = Libde265.de265_get_image_width(image, 0);
        var height = Libde265.de265_get_image_height(image, 0);
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        // The decoder is the authority on size — an SPS we misread would otherwise tear the picture —
        // and the frame carries that size to the surface itself (see Draw). Nothing is arranged here
        // for a later frame to depend on.
        var y = Libde265.de265_get_image_plane(image, 0, out var yStride);
        var u = Libde265.de265_get_image_plane(image, 1, out var uStride);
        var v = Libde265.de265_get_image_plane(image, 2, out var vStride);
        if (y == IntPtr.Zero || u == IntPtr.Zero || v == IntPtr.Zero)
        {
            return null;
        }

        var bgra = new byte[width * height * 4];
        var hd = height >= 720;

        // BT.709 / BT.601, limited range, in fixed point.
        var rv = hd ? 459 : 409;
        var gu = hd ? -55 : -100;
        var gv = hd ? -136 : -208;
        var bu = hd ? 541 : 516;

        unsafe
        {
            var yp = (byte*)y;
            var up = (byte*)u;
            var vp = (byte*)v;

            for (var row = 0; row < height; row++)
            {
                var yRow = yp + (row * yStride);
                var uRow = up + ((row >> 1) * uStride);
                var vRow = vp + ((row >> 1) * vStride);
                var outIndex = row * width * 4;

                for (var col = 0; col < width; col++)
                {
                    var c = yRow[col] - 16;
                    var d = uRow[col >> 1] - 128;
                    var e = vRow[col >> 1] - 128;
                    var yy = 298 * c;

                    bgra[outIndex + 0] = Clamp8((yy + (bu * d) + 128) >> 8);
                    bgra[outIndex + 1] = Clamp8((yy + (gu * d) + (gv * e) + 128) >> 8);
                    bgra[outIndex + 2] = Clamp8((yy + (rv * e) + 128) >> 8);
                    bgra[outIndex + 3] = 255;
                    outIndex += 4;
                }
            }
        }

        return bgra;
    }

    private static byte Clamp8(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);

    /// <summary>
    /// Hand the newest frame to the UI thread — and only the newest.
    ///
    /// A monitor that shows a picture from ten seconds ago is worse than one that drops a frame
    /// (LIVE-8), so a frame that arrives while another is still waiting replaces it rather than queueing
    /// behind it. Under load the picture loses frames; it never falls behind.
    ///
    /// **Nothing here may latch.** This coalescing needs a "a frame is already on its way" flag, and a
    /// flag that is cleared by a callback is a flag that stays set for ever if the callback never runs —
    /// which is a picture frozen for the rest of the night while the audio plays on, and a parent
    /// looking at a room as it was an hour ago. So the flag is cleared on the failure path too, and the
    /// surface follows the frame rather than being arranged separately: every state here has to be
    /// recoverable by the next frame.
    /// </summary>
    private void Present(byte[] bgra, int width, int height)
    {
        lock (_lock)
        {
            if (_torn)
            {
                return;
            }

            _ready = bgra;
            _readyWidth = width;
            _readyHeight = height;
            if (_posted)
            {
                return;
            }

            _posted = true;
        }

        if (!_ui.TryEnqueue(() => Draw()))
        {
            // The queue would not take it (the window is going away, or the dispatcher is shutting
            // down). Clearing the flag is what lets the picture come back if it does not: leaving it set
            // would freeze the feed permanently, and silently.
            lock (_lock)
            {
                _posted = false;
            }
        }
    }

    /// <summary>Draw the newest frame. UI thread only.</summary>
    private void Draw()
    {
        byte[]? frame;
        int width, height;
        lock (_lock)
        {
            frame = _ready;
            width = _readyWidth;
            height = _readyHeight;
            _ready = null;
            _posted = false;
        }

        if (frame == null || _torn || width <= 0 || height <= 0)
        {
            return;
        }

        try
        {
            // The surface follows the frame. Deciding the size somewhere else and hoping the two agree
            // is how a mismatch becomes permanent: one dropped resize and every frame afterwards fails
            // the comparison for ever.
            var bitmap = _bitmap;
            if (bitmap == null || bitmap.PixelWidth != width || bitmap.PixelHeight != height)
            {
                bitmap = new WriteableBitmap(width, height);
                _bitmap = bitmap;
                _onSurfaceReady?.Invoke(bitmap);
                Log.Info("video", $"picture surface is {width}x{height}");
            }

            using var stream = bitmap.PixelBuffer.AsStream();
            stream.Write(frame, 0, frame.Length);
            bitmap.Invalidate();
        }
        catch (Exception e)
        {
            Log.Warn("video", $"could not draw a frame: {e.Message}");
        }
    }

    private void ReleaseDecoder()
    {
        IntPtr ctx;
        lock (_lock)
        {
            _torn = true;
            _ready = null;
            ctx = _ctx;
            _ctx = IntPtr.Zero;
        }

        if (ctx != IntPtr.Zero)
        {
            try
            {
                Libde265.de265_free_decoder(ctx);
            }
            catch (Exception e)
            {
                Log.Warn("video", $"could not free the decoder: {e.Message}");
            }
        }
    }

    private void Fail(string why)
    {
        Log.Error("video", why);
        DecoderFailed?.Invoke(why);
    }
}
