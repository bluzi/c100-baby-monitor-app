using BabyMonitor.Core.Monitor;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Security.Cryptography;
using Log = BabyMonitor.App.Services.Logging.Log;

namespace BabyMonitor.App.Services;

/// <summary>
/// The picture, on Windows: the camera's H.265 access units go into a <see cref="MediaStreamSource"/>
/// and out of Media Foundation's own decoder, hardware-accelerated where the machine has it.
///
/// Two things here are Windows-shaped and neither the phone nor the Mac has to care about them:
///
///  1. **A media type must carry a frame size** before Media Foundation will decode a byte. That size
///     comes from the SPS (the core parses it — see HevcSps), and it is also what gives the window
///     the camera's shape (WIN-19).
///  2. **Windows may not own an H.265 decoder at all.** The HEVC Video Extensions are a separate
///     (free) Store download on many machines. When that is the case, MediaFoundation says so through
///     <see cref="MediaPlayer.MediaFailed"/> — and the app tells the parent in plain words and keeps
///     monitoring (WIN-20). Sound is what monitoring means; the picture is a convenience.
///
/// Everything in here is best-effort by contract (LIVE-7): nothing it does may throw into the monitor.
/// </summary>
public sealed class MediaFoundationVideoRenderer : IVideoRenderer, IDisposable
{
    private readonly object _lock = new();
    private readonly Queue<Sample> _queue = new();

    private MediaStreamSource? _source;
    private MediaStreamSourceSampleRequest? _pendingRequest;
    private MediaStreamSourceSampleRequestDeferral? _pendingDeferral;

    private byte[]? _parameterSets; // Annex-B VPS+SPS+PPS, prepended to every keyframe
    private long _firstPtsMs = -1;
    private bool _torn;

    /// <summary>WIN-20: Windows could not decode this stream. The shell says so; monitoring carries on.</summary>
    public event Action<string>? DecoderFailed;

    /// <summary>The player the window binds its MediaPlayerElement to.</summary>
    public MediaPlayer Player { get; } = new()
    {
        // A monitor is a live feed, not a film: never buffer ahead, never fall behind.
        RealTimePlayback = true,
        AutoPlay = true,
        IsMuted = true, // the feed's audio is ours; this path carries the picture only
    };

    public MediaFoundationVideoRenderer()
    {
        Player.MediaFailed += OnMediaFailed;
    }

    /// <summary>
    /// Build a decoder for a stream of this size. Returns false if Windows would not even accept the
    /// media type — in which case the core stops feeding it and the shell tells the parent (WIN-20).
    /// </summary>
    public bool Configure(byte[] vps, byte[] sps, byte[] pps, int width, int height)
    {
        try
        {
            lock (_lock)
            {
                _queue.Clear();
                _firstPtsMs = -1;

                // The camera sends its parameter sets in their own access units, so every keyframe we
                // hand to Media Foundation gets them prepended — an in-band stream is what its H.265
                // decoder expects, and it is what lets a decoder that started late catch up.
                var startCode = new byte[] { 0, 0, 0, 1 };
                _parameterSets = startCode
                    .Concat(vps).Concat(startCode)
                    .Concat(sps).Concat(startCode)
                    .Concat(pps)
                    .ToArray();

                var properties = new VideoEncodingProperties
                {
                    Subtype = "HEVC",
                    Width = (uint)width,
                    Height = (uint)height,
                };
                properties.SetFormatUserData(_parameterSets);

                var descriptor = new VideoStreamDescriptor(properties);
                var source = new MediaStreamSource(descriptor)
                {
                    BufferTime = TimeSpan.Zero, // LIVE-8: the feed tracks real time; delay never accumulates
                };
                source.SampleRequested += OnSampleRequested;
                source.Starting += OnStarting;

                _source = source;
            }

            Player.Source = MediaSource.CreateFromMediaStreamSource(_source);
            Log.Info("video", $"Media Foundation H.265 pipeline built ({width}x{height})");
            return true;
        }
        catch (Exception e)
        {
            // A media type Windows will not take is the same answer as a decoder it does not have.
            Log.Error("video", $"could not build the H.265 decoder: {e.Message}", e);
            DecoderFailed?.Invoke(e.Message);
            return false;
        }
    }

    /// <summary>One Annex-B access unit. Never throws — LIVE-7.</summary>
    public void Decode(byte[] annexB, long ptsMs)
    {
        try
        {
            var data = annexB;
            if (Hevc.IsKeyframe(annexB) && _parameterSets != null)
            {
                data = _parameterSets.Concat(annexB).ToArray();
            }

            lock (_lock)
            {
                if (_torn)
                {
                    return;
                }

                if (_firstPtsMs < 0)
                {
                    _firstPtsMs = ptsMs;
                }

                var sample = new Sample(data, TimeSpan.FromMilliseconds(Math.Max(0, ptsMs - _firstPtsMs)));

                // A request is waiting: hand it the frame and let the decoder go.
                if (_pendingRequest != null && _pendingDeferral != null)
                {
                    var request = _pendingRequest;
                    var deferral = _pendingDeferral;
                    _pendingRequest = null;
                    _pendingDeferral = null;
                    request.Sample = ToMediaSample(sample);
                    deferral.Complete();
                    return;
                }

                // Nothing is asking yet. Keep a short queue — a monitor that shows a picture from ten
                // seconds ago is worse than one that drops a frame (LIVE-8).
                _queue.Enqueue(sample);
                while (_queue.Count > 30)
                {
                    _queue.Dequeue();
                }
            }
        }
        catch (Exception e)
        {
            Log.Warn("video", $"could not queue a frame: {e.Message}");
        }
    }

    public void TearDown()
    {
        lock (_lock)
        {
            _torn = true;
            _queue.Clear();
            _pendingRequest = null;

            try
            {
                _pendingDeferral?.Complete();
            }
            catch (Exception)
            {
                // A deferral on a source that is already gone.
            }

            _pendingDeferral = null;
        }

        try
        {
            Player.Source = null;
        }
        catch (Exception e)
        {
            Log.Warn("video", $"could not release the video source: {e.Message}");
        }

        _source = null;
        Log.Info("video", "video pipeline torn down");
    }

    public void Dispose()
    {
        TearDown();
        Player.MediaFailed -= OnMediaFailed;
        Player.Dispose();
    }

    private void OnStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
    {
        // A live stream starts where it starts: there is no seeking, and no history to seek into.
        args.Request.SetActualStartPosition(TimeSpan.Zero);
    }

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        lock (_lock)
        {
            if (_torn)
            {
                return; // a null sample ends the stream, which is what a torn-down renderer wants
            }

            if (_queue.Count > 0)
            {
                args.Request.Sample = ToMediaSample(_queue.Dequeue());
                return;
            }

            // Nothing decoded yet. Hold the request open rather than ending the stream — the camera's
            // next frame will complete it.
            _pendingRequest = args.Request;
            _pendingDeferral = args.Request.GetDeferral();
        }
    }

    private static MediaStreamSample ToMediaSample(Sample sample)
    {
        var buffer = CryptographicBuffer.CreateFromByteArray(sample.Data);
        return MediaStreamSample.CreateFromBuffer(buffer, sample.Timestamp);
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        // WIN-20: on a PC without the HEVC Video Extensions this is where Windows finally says so.
        Log.Error("video", $"Media Foundation could not play the stream: {args.Error} — {args.ErrorMessage}");
        DecoderFailed?.Invoke(args.ErrorMessage ?? args.Error.ToString());
    }

    private sealed record Sample(byte[] Data, TimeSpan Timestamp);
}
