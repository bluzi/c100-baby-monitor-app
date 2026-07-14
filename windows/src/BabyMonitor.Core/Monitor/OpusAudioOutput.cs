using BabyMonitor.Core.Logging;
using Concentus.Structs;

namespace BabyMonitor.Core.Monitor;

/// <summary>
/// Where decoded PCM goes on its way to a speaker. The shell implements it with WASAPI; the decode,
/// the analysis tap and the mute rule stay here, in the shared monitor, where they can be tested.
///
/// <see cref="Volume"/> is what mute moves — never the decoder (LIVE-3).
/// </summary>
public interface IPcmSink : IDisposable
{
    /// <summary>0 = silent, 1 = as sent. Mute sets this to 0 and nothing else.</summary>
    float Volume { get; set; }

    void Start(int sampleRate);

    /// <summary>
    /// Hand one decoded window to the speaker. Must throw if the audio path is dead: a parent must
    /// never mistake a broken speaker for a quiet room.
    /// </summary>
    void Write(short[] pcm);

    void Stop();
}

/// <summary>
/// The <see cref="IAudioOutput"/> every desktop shares: Opus in, PCM out, with the analysis tap that
/// feeds the level meter and the crying alarm.
///
/// The decode path is deliberately in the shared core rather than the shell. It is the crying alarm's
/// path — every decoded window feeds the detector — and putting platform code in the middle of it
/// would put platform code between a crying baby and a sleeping parent.
///
/// LIVE-3: muting sets the sink's volume to zero. **Decoding and the analysis callback carry on
/// regardless** — that is what keeps the level meter and the alarm alive behind a muted feed, and an
/// implementation that muted by pausing the decoder would have silently disabled the alarm.
/// </summary>
public sealed class OpusAudioOutput : IAudioOutput
{
    /// <summary>120 ms at 48 kHz — the largest frame Opus can carry, so a packet never overruns.</summary>
    private const int MaxFrameSamples = 5760;

    private readonly int _sampleRate;
    private readonly Action<short[], int> _onPcmWindow;
    private readonly IPcmSink _sink;
    private readonly short[] _decoded = new short[MaxFrameSamples];
    private readonly short[] _analysisBuf = new short[2048];

    private OpusDecoder? _decoder;
    private int _analysisLen;
    private bool _muted;

    public OpusAudioOutput(IPcmSink sink, Action<short[], int> onPcmWindow, int sampleRate = 48000)
    {
        _sink = sink;
        _onPcmWindow = onPcmWindow;
        _sampleRate = sampleRate;
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            _muted = value;
            _sink.Volume = value ? 0f : 1f; // LIVE-3: the speaker, and only the speaker
        }
    }

    /// <summary>Never leaves a half-built decoder or sink behind: any failure releases what it created.</summary>
    public void Start()
    {
        try
        {
            _decoder = new OpusDecoder(_sampleRate, 1);
            _sink.Start(_sampleRate);
            _sink.Volume = _muted ? 0f : 1f;
            Log.I("audio", $"opus decoder + audio output up ({_sampleRate}Hz mono)");
        }
        catch (Exception)
        {
            Release();
            throw;
        }
    }

    public void Push(byte[] packet, long ptsMs)
    {
        var decoder = _decoder;
        if (decoder == null)
        {
            return;
        }

        int samples;
        try
        {
            samples = decoder.Decode(packet, 0, packet.Length, _decoded, 0, MaxFrameSamples, false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A corrupt packet is a blip, not a failure — the next one usually decodes. Losing the whole
            // connection over one bad packet would be worse than a click in the audio.
            Log.W("audio", $"opus: dropped a packet ({e.Message})");
            return;
        }

        if (samples <= 0)
        {
            return;
        }

        var pcm = _decoded[..samples];
        _sink.Write(pcm); // throws if the audio path is dead — see IPcmSink.Write
        Analyze(pcm);
    }

    public void Release()
    {
        try
        {
            _sink.Stop();
        }
        catch (Exception e)
        {
            Log.W("audio", $"could not stop the audio sink: {e.Message}");
        }

        try
        {
            _sink.Dispose();
        }
        catch (Exception e)
        {
            Log.W("audio", $"could not dispose the audio sink: {e.Message}");
        }

        _decoder = null;
        _analysisLen = 0;
    }

    /// <summary>LIVE-3: runs muted or not — the level meter and the alarm feed off this.</summary>
    private void Analyze(short[] samples)
    {
        var off = 0;
        while (off < samples.Length)
        {
            var n = Math.Min(_analysisBuf.Length - _analysisLen, samples.Length - off);
            Array.Copy(samples, off, _analysisBuf, _analysisLen, n);
            _analysisLen += n;
            off += n;
            if (_analysisLen != _analysisBuf.Length)
            {
                continue;
            }

            _onPcmWindow((short[])_analysisBuf.Clone(), _sampleRate);
            _analysisLen = 0;
        }
    }
}
