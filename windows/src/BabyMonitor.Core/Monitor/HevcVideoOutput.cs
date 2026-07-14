using BabyMonitor.Core.Logging;

namespace BabyMonitor.Core.Monitor;

/// <summary>
/// What the window plugs in to draw the picture. Implemented by the shell (Media Foundation on
/// Windows), because a decoder and a swap chain are the one thing that is purely a platform's
/// business — and because video is best-effort (LIVE-7), so the seam costs nothing that matters. The
/// audio path has no such seam, and that asymmetry is deliberate.
///
/// The bitstream work stays here: the camera sends VPS/SPS/PPS in their own access units, separate
/// from keyframes, so they must be cached across frames and the decoder configured at the next
/// keyframe. Every platform's decoder needs exactly that.
/// </summary>
public interface IVideoRenderer
{
    /// <summary>
    /// Configure (or reconfigure) the decoder for a stream of this size. Called before any frame, and
    /// again on a new stream. Returning false means this machine cannot decode H.265 at all (WIN-20) —
    /// the picture is then given up on, and audio monitoring carries on regardless.
    /// </summary>
    bool Configure(byte[] vps, byte[] sps, byte[] pps, int width, int height);

    /// <summary>One Annex-B access unit. Must not throw — see <see cref="HevcVideoOutput.Push"/>.</summary>
    void Decode(byte[] annexB, long ptsMs);

    void TearDown();
}

/// <summary>Where the picture goes, when there is anywhere for it to go. Set by whichever window is showing.</summary>
public static class VideoSink
{
    private static volatile IVideoRenderer? _renderer;

    public static IVideoRenderer? Renderer
    {
        get => _renderer;
        set => _renderer = value;
    }
}

/// <summary>
/// LIVE-7 made concrete: this never throws. Video trouble must never take audio monitoring down with
/// it — a black picture is a disappointment; a dead alarm is the thing this project exists to prevent.
/// </summary>
public sealed class HevcVideoOutput : IVideoOutput
{
    private const int MaxFailures = 8;

    private readonly HevcParamCache _params = new();
    private readonly Action<int, int>? _onVideoSize;

    private IVideoRenderer? _configuredFor;
    private int _failures;
    private bool _loggedWaiting;
    private int _width;
    private int _height;

    /// <summary>
    /// WIN-20: the shell asks this to decide whether to tell the parent that Windows has no H.265
    /// decoder. It is set once the decoder has refused, and never unset by anything but a new stream.
    /// </summary>
    public bool DecoderUnavailable { get; private set; }

    public HevcVideoOutput(Action<int, int>? onVideoSize = null) => _onVideoSize = onVideoSize;

    public void Push(byte[] annexB, long ptsMs)
    {
        try
        {
            _params.Observe(annexB);
            ReadSizeIfNeeded();

            if (!Hevc.HasVclNal(annexB))
            {
                return; // config-only access unit
            }

            var renderer = VideoSink.Renderer;
            if (renderer == null)
            {
                // No window is showing. Audio is unaffected (LIVE-7), and the next window to open
                // reconfigures at the next keyframe.
                if (_configuredFor != null)
                {
                    _configuredFor = null;
                    _loggedWaiting = false;
                }

                return;
            }

            if (_failures >= MaxFailures || DecoderUnavailable)
            {
                return; // this session's video is a lost cause; audio continues
            }

            if (!ReferenceEquals(_configuredFor, renderer))
            {
                if (!Hevc.IsKeyframe(annexB))
                {
                    return; // wait for a clean entry point
                }

                var sets = _params.ParameterSets();
                if (sets == null || _width == 0)
                {
                    if (!_loggedWaiting)
                    {
                        _loggedWaiting = true;
                        Log.I("video", "keyframe seen but VPS/SPS/PPS not yet cached — waiting");
                    }

                    return;
                }

                if (!renderer.Configure(sets[0], sets[1], sets[2], _width, _height))
                {
                    // WIN-20: Windows may simply not have an HEVC decoder. That is not a crash and not a
                    // silent black rectangle — the shell says so, and monitoring carries on.
                    DecoderUnavailable = true;
                    Log.W("video", "this machine cannot decode H.265 — no picture; audio monitoring continues");
                    return;
                }

                _configuredFor = renderer;
                Log.I(
                    "video",
                    $"H265 decoder configured ({_width}x{_height}; vps {sets[0].Length}B sps {sets[1].Length}B pps {sets[2].Length}B)");
            }

            renderer.Decode(annexB, ptsMs);
        }
        catch (Exception e)
        {
            _failures++;
            Log.W("video", $"decode failed (attempt {_failures}/{MaxFailures}), retrying at next keyframe", e);
            _configuredFor = null;
        }
    }

    public void Release()
    {
        try
        {
            VideoSink.Renderer?.TearDown();
        }
        catch (Exception e)
        {
            Log.W("video", $"could not tear the renderer down: {e.Message}");
        }

        _configuredFor = null;
    }

    /// <summary>WIN-19: the window takes the camera's shape, and this is where that shape comes from.</summary>
    private void ReadSizeIfNeeded()
    {
        if (_width != 0)
        {
            return;
        }

        var sets = _params.ParameterSets();
        if (sets == null)
        {
            return;
        }

        var size = HevcSps.Dimensions(sets[1]);
        if (size == null)
        {
            // An SPS we cannot read is not a reason to have no picture: assume the camera's usual shape
            // and let the decoder correct us. A wrong guess costs a stretched frame; refusing to decode
            // costs the picture entirely.
            Log.W("video", "could not read the picture size from the SPS — assuming 1920x1080");
            _width = 1920;
            _height = 1080;
        }
        else
        {
            (_width, _height) = size.Value;
            Log.I("video", $"camera picture is {_width}x{_height}");
        }

        _onVideoSize?.Invoke(_width, _height);
    }
}
