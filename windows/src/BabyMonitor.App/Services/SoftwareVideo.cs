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
    private readonly object _lock = new();
    private readonly DispatcherQueue _ui;
    private readonly Action<WriteableBitmap>? _onSurfaceReady;

    private IntPtr _ctx;
    private byte[]? _parameterSets; // Annex-B VPS+SPS+PPS, prepended to every keyframe
    private WriteableBitmap? _bitmap;
    private byte[]? _ready;         // the newest frame, waiting for the UI thread
    private bool _posted;           // a UI update is already queued; do not pile more on
    private bool _torn;
    private int _width;
    private int _height;

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
                _width = width;
                _height = height;

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

            // The surface belongs to the UI thread, so it is made there and handed to the window.
            _ui.TryEnqueue(() =>
            {
                try
                {
                    _bitmap = new WriteableBitmap(width, height);
                    _onSurfaceReady?.Invoke(_bitmap);
                }
                catch (Exception e)
                {
                    Log.Warn("video", $"could not create the picture surface: {e.Message}");
                }
            });

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
            lock (_lock)
            {
                if (_torn || _ctx == IntPtr.Zero)
                {
                    return;
                }

                ctx = _ctx;
                sets = _parameterSets;
            }

            var data = Hevc.IsKeyframe(annexB) && sets != null
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
            for (var pass = 0; pass < 64; pass++)
            {
                var err = Libde265.de265_decode(ctx, out var more);
                DrainPictures(ctx);

                if (err == Libde265.DE265_ERROR_WAITING_FOR_INPUT_DATA)
                {
                    return; // it has had everything this access unit carries
                }

                if (err == Libde265.DE265_ERROR_IMAGE_BUFFER_FULL)
                {
                    continue; // we just emptied the queue; let it carry on
                }

                if (err != Libde265.DE265_OK)
                {
                    // A broken packet is a dropped frame, never a dropped session: the stream recovers at
                    // the next keyframe, and PROTO-23's rule (a bad packet is skipped) holds here too.
                    Log.Warn("video", $"libde265 could not decode a frame: {Libde265.ErrorText(err)}");
                    return;
                }

                if (more == 0)
                {
                    return;
                }
            }
        }
        catch (Exception e)
        {
            Log.Warn("video", $"could not decode a frame: {e.Message}");
        }
    }

    public void TearDown()
    {
        ReleaseDecoder();
        Log.Info("video", "video pipeline torn down");
    }

    public void Dispose() => ReleaseDecoder();

    /// <summary>Take every finished picture out, and give each one back — a picture not released is a stall.</summary>
    private void DrainPictures(IntPtr ctx)
    {
        while (true)
        {
            var image = Libde265.de265_get_next_picture(ctx);
            if (image == IntPtr.Zero)
            {
                return;
            }

            try
            {
                var frame = ToBgra(image);
                if (frame != null)
                {
                    Present(frame);
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

        // The decoder is the authority on size: an SPS we misread would otherwise tear the picture.
        if (width != _width || height != _height)
        {
            Log.Info("video", $"picture is {width}x{height}; rebuilding the surface");
            _width = width;
            _height = height;
            _ui.TryEnqueue(() =>
            {
                try
                {
                    _bitmap = new WriteableBitmap(width, height);
                    _onSurfaceReady?.Invoke(_bitmap);
                }
                catch (Exception e)
                {
                    Log.Warn("video", $"could not resize the picture surface: {e.Message}");
                }
            });
        }

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
    /// </summary>
    private void Present(byte[] bgra)
    {
        lock (_lock)
        {
            if (_torn)
            {
                return;
            }

            _ready = bgra;
            if (_posted)
            {
                return;
            }

            _posted = true;
        }

        _ui.TryEnqueue(() =>
        {
            byte[]? frame;
            lock (_lock)
            {
                frame = _ready;
                _ready = null;
                _posted = false;
            }

            var bitmap = _bitmap;
            if (frame == null || bitmap == null || _torn)
            {
                return;
            }

            try
            {
                if (bitmap.PixelWidth * bitmap.PixelHeight * 4 != frame.Length)
                {
                    return; // a resize is in flight; the next frame lands on the new surface
                }

                using var stream = bitmap.PixelBuffer.AsStream();
                stream.Write(frame, 0, frame.Length);
                bitmap.Invalidate();
            }
            catch (Exception e)
            {
                Log.Warn("video", $"could not draw a frame: {e.Message}");
            }
        });
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
