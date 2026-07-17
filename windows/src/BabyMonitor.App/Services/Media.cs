using BabyMonitor.Core.Monitor;
using NAudio.Wave;
using Log = BabyMonitor.App.Services.Logging.Log;

namespace BabyMonitor.App.Services;

/// <summary>
/// The feed's speaker: WASAPI, in shared mode, fed by the shared core's Opus decoder.
///
/// LIVE-3 lives at the top of this file and nowhere else: **mute is a volume, never a pause.** The
/// decode loop and the analysis tap are in the core and keep running regardless — which is what keeps
/// the level meter and the crying alarm alive behind a muted feed.
/// </summary>
public sealed class WasapiPcmSink : IPcmSink
{
    private readonly object _lock = new();

    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;
    private float _volume = 1f;

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            lock (_lock)
            {
                if (_output != null)
                {
                    _output.Volume = _volume; // the speaker, and only the speaker
                }
            }
        }
    }

    public void Start(int sampleRate)
    {
        lock (_lock)
        {
            // A latency of 50 ms: this is a live monitor, not a music player. Anything larger is delay
            // a parent hears as "the baby cried a second ago".
            var output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, useEventSync: true, latency: 50);
            var buffer = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, 1))
            {
                // LIVE-8: audio never accumulates delay. If playback falls behind, the backlog is
                // dropped — a brief glitch, not a growing lag.
                BufferDuration = TimeSpan.FromMilliseconds(600),
                DiscardOnBufferOverflow = true,
            };

            output.Init(buffer);
            output.Volume = _volume;
            output.Play();

            _output = output;
            _buffer = buffer;
            Log.Info("audio", $"WASAPI output up ({sampleRate}Hz mono, shared mode)");
        }
    }

    public void Write(short[] pcm)
    {
        BufferedWaveProvider? buffer;
        WasapiOut? output;
        lock (_lock)
        {
            buffer = _buffer;
            output = _output;
        }

        if (buffer == null || output == null)
        {
            return;
        }

        // A dead audio path must fail loudly rather than play silence: the parent would hear nothing
        // and read it as a quiet room. The engine treats the throw as a dead connection and rebuilds.
        if (output.PlaybackState == PlaybackState.Stopped)
        {
            throw new InvalidOperationException("audio: the output device stopped");
        }

        // LIVE-2/3: the volume is applied to the samples, here, rather than handed to WasapiOut.Volume.
        //
        // That property routes through the session volume, and on this path it does not silence the
        // feed — mute looked engaged, the control latched, and the room played on. A mute that does not
        // mute is worse than no mute at all: a parent who believes the app is quiet learns otherwise at
        // 3am, and the one thing they asked it to do is the thing it did not do. Scaling the samples is
        // ours end to end, and it cannot be quietly ignored by anything downstream.
        //
        // It is still a volume and never a pause: the decoder and the analysis tap upstream never see
        // this, so the level meter and the crying alarm carry on behind a silent feed (LIVE-3).
        var volume = _volume;
        var bytes = new byte[pcm.Length * 2];

        if (volume <= 0f)
        {
            buffer.AddSamples(bytes, 0, bytes.Length); // already zeroed: silence, at the same rate
            return;
        }

        if (volume < 1f)
        {
            var scaled = new short[pcm.Length];
            for (var i = 0; i < pcm.Length; i++)
            {
                scaled[i] = (short)(pcm[i] * volume);
            }

            Buffer.BlockCopy(scaled, 0, bytes, 0, bytes.Length);
        }
        else
        {
            Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        }

        buffer.AddSamples(bytes, 0, bytes.Length);
    }

    public void Stop()
    {
        lock (_lock)
        {
            try
            {
                _output?.Stop();
            }
            catch (Exception e)
            {
                Log.Warn("audio", $"could not stop the audio output: {e.Message}");
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try
            {
                _output?.Dispose();
            }
            catch (Exception e)
            {
                Log.Warn("audio", $"could not dispose the audio output: {e.Message}");
            }

            _output = null;
            _buffer = null;
        }
    }
}

/// <summary>
/// One alarm tone, on its own WASAPI client — so nothing about the feed's audio, muted or broken, can
/// silence it (ALRM-4). The tone itself is the shared one from the core: the phone, the Mac and the PC
/// make literally the same sound.
/// </summary>
public sealed class WasapiAlarmVoice : IAlarmVoice
{
    private readonly object _lock = new();

    private WasapiOut? _output;
    private float _volume;

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            lock (_lock)
            {
                if (_output != null)
                {
                    _output.Volume = _volume;
                }
            }
        }
    }

    public void Start(short[] pcm, int sampleRate, bool loop, float volume)
    {
        lock (_lock)
        {
            var bytes = new byte[pcm.Length * 2];
            Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);

            var provider = new LoopingPcmProvider(bytes, new WaveFormat(sampleRate, 16, 1), loop);
            var output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, useEventSync: true, latency: 100);
            output.Init(provider);
            _volume = Math.Clamp(volume, 0f, 1f);
            output.Volume = _volume;
            output.Play();
            _output = output;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            try
            {
                _output?.Stop();
            }
            catch (Exception e)
            {
                Log.Warn("service", $"could not stop the alarm: {e.Message}");
            }
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_lock)
        {
            try
            {
                _output?.Dispose();
            }
            catch (Exception e)
            {
                Log.Warn("service", $"could not dispose the alarm output: {e.Message}");
            }

            _output = null;
        }
    }

    /// <summary>The alarm's one cycle, on repeat until it is acknowledged (ALRM-4).</summary>
    private sealed class LoopingPcmProvider : IWaveProvider
    {
        private readonly byte[] _pcm;
        private readonly bool _loop;
        private int _position;

        public LoopingPcmProvider(byte[] pcm, WaveFormat format, bool loop)
        {
            _pcm = pcm;
            _loop = loop;
            WaveFormat = format;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(byte[] buffer, int offset, int count)
        {
            var written = 0;
            while (written < count)
            {
                if (_position >= _pcm.Length)
                {
                    if (!_loop)
                    {
                        break;
                    }

                    _position = 0;
                }

                var n = Math.Min(count - written, _pcm.Length - _position);
                Buffer.BlockCopy(_pcm, _position, buffer, offset + written, n);
                _position += n;
                written += n;
            }

            if (written >= count || _loop)
            {
                return written;
            }

            // A preview that has finished: silence rather than a click.
            Array.Clear(buffer, offset + written, count - written);
            return count;
        }
    }
}

/// <summary>The PC's media stack, handed to the engine.</summary>
public sealed class WindowsMedia : IMediaFactory
{
    private readonly Action<int, int>? _onVideoSize;

    public WindowsMedia(Action<int, int>? onVideoSize = null) => _onVideoSize = onVideoSize;

    /// <summary>DESK-22: set once the video output has found that this PC cannot decode H.265.</summary>
    public HevcVideoOutput? LastVideo { get; private set; }

    public IAudioOutput Audio(Action<short[], int> onPcmWindow) =>
        new OpusAudioOutput(new WasapiPcmSink(), onPcmWindow);

    public IVideoOutput Video()
    {
        var video = new HevcVideoOutput(_onVideoSize);
        LastVideo = video;
        return video;
    }
}
