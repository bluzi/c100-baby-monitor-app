using BabyMonitor.Core.Logging;

namespace BabyMonitor.Core.Monitor;

/// <summary>
/// One alarm tone on its own output, so nothing about the feed's audio can silence it. The shell
/// implements it (WASAPI on Windows); the tones themselves are the shared ones from
/// <see cref="AlarmTones"/>, so the phone, the Mac and the PC make literally the same sound.
/// </summary>
public interface IAlarmVoice : IDisposable
{
    /// <summary>Volume, 0..1. Set while playing — that is how the ramp (ALRM-14) is done.</summary>
    float Volume { get; set; }

    void Start(short[] pcm, int sampleRate, bool loop, float volume);

    void Stop();
}

/// <summary>
/// ALRM-4 / ALRM-11 / ALRM-14 / WATCH-3: rings until acknowledged, on its own audio path so a muted
/// feed cannot silence it.
///
/// This is the last link between a crying baby and a sleeping parent, so it is paranoid: it never
/// throws, and it reports whether it actually started so a swallowed alarm can be retried (WATCH-6).
///
/// A PC has no vibration motor and no separate alarm-volume channel (DESK-23). That is a real
/// difference from the phone and it is not papered over: the alarm plays at the volume the user chose
/// (ALRM-11) on the system's default output, and the tray icon makes a ringing alarm unmistakable
/// (DESK-1) so a PC with its speakers down still *shows* the alarm.
/// </summary>
public sealed class ToneRinger : IRinger, IDisposable
{
    private const int RampStepMs = 250;

    private readonly Func<IAlarmVoice> _voices;
    private readonly object _lock = new();

    private CancellationTokenSource? _cts;
    private Task? _job;

    public ToneRinger(Func<IAlarmVoice> voices) => _voices = voices;

    public bool Ring(AlarmKind kind, string cameraName)
    {
        lock (_lock)
        {
            if (MonitorHub.ActiveAlarm.Value != null || _job is { IsCompleted: false })
            {
                return false; // WATCH-6: an alarm the parent cannot hear is not an alarm — retry later
            }

            var settings = MonitorHub.Settings.Value;
            var sound = kind == AlarmKind.BabyNoise ? settings.CryAlarmSound : settings.FeedAlarmSound;
            var volume = kind == AlarmKind.BabyNoise ? settings.CryAlarmVolume : settings.FeedAlarmVolume;
            Log.W("service", $"alarm ringing: {kind} sound={sound} volume={volume} camera={cameraName}");

            // The sound IS the alarm. Start it first, and let nothing here throw: a throw after marking
            // the alarm active would leave it "ringing" with no sound and no way to acknowledge — and
            // would block every later alarm, all night.
            var cts = new CancellationTokenSource();
            _cts = cts;
            _job = Task.Run(
                async () =>
                {
                    try
                    {
                        await PlayLoopAsync(sound, volume, cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Acknowledging cancels the sound. That is not a failure.
                    }
                    catch (Exception e)
                    {
                        Log.E("service", $"alarm sound failed: {e.Message}", e);
                    }
                },
                CancellationToken.None);

            MonitorHub.ActiveAlarm.Value = kind;
            return true;
        }
    }

    public void Acknowledge()
    {
        lock (_lock)
        {
            var was = MonitorHub.ActiveAlarm.Value;
            if (was != null)
            {
                Log.W("service", $"alarm acknowledged (was {was})");
            }

            _cts?.Cancel();
            _cts = null;
            _job = null;
            MonitorHub.ActiveAlarm.Value = null;
        }
    }

    /// <summary>ALRM-11: preview a sound exactly as a real alarm of this kind would play it.</summary>
    public Task PreviewAsync(string sound, double volume)
    {
        if (MonitorHub.ActiveAlarm.Value != null)
        {
            return Task.CompletedTask; // never talk over a real alarm
        }

        Log.I("service", $"previewing alarm sound: {sound} at volume {volume}");
        return Task.Run(async () =>
        {
            try
            {
                await PlayOnceAsync(sound, volume).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.W("service", $"could not preview {sound}: {e.Message}", e);
            }
        });
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    private async Task PlayLoopAsync(string sound, double volume, CancellationToken ct)
    {
        var pcm = AlarmTones.Pcm(sound);
        using var voice = _voices();

        // ALRM-14: it starts gentle and climbs — but it never starts *silent*, not even for the
        // instant before the first ramp step. A first cycle nobody hears is a first cycle wasted.
        long elapsed = 0;
        voice.Start(pcm, AlarmTones.SampleRate, loop: true, volume: RampVolume(volume, elapsed));

        while (!ct.IsCancellationRequested)
        {
            voice.Volume = RampVolume(volume, elapsed);
            await Task.Delay(RampStepMs, ct).ConfigureAwait(false);
            elapsed += RampStepMs;
        }
    }

    private static float RampVolume(double volume, long elapsedMs) =>
        (float)Math.Clamp(volume * AlarmTones.RampGain(elapsedMs), 0.0, 1.0);

    private async Task PlayOnceAsync(string sound, double volume)
    {
        var pcm = AlarmTones.Pcm(sound);
        using var voice = _voices();
        voice.Start(pcm, AlarmTones.SampleRate, loop: false, volume: (float)Math.Clamp(volume, 0.0, 1.0));
        await Task.Delay((int)(pcm.Length * 1000L / AlarmTones.SampleRate)).ConfigureAwait(false);
    }
}
