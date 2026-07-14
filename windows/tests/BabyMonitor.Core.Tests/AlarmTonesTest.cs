using BabyMonitor.Core.Data;
using BabyMonitor.Core.Dsp;
using BabyMonitor.Core.Monitor;
using Xunit;

namespace BabyMonitor.Core.Tests;

public class AlarmTonesTest
{
    private static double PeakOf(short[] pcm) => pcm.Max(s => Math.Abs((int)s)) / 32768.0;

    /// <summary>The loudest frequency in a window — how the tone actually reads to an ear.</summary>
    private static double DominantHz(short[] pcm)
    {
        const int n = 4096;
        var re = new double[n];
        var im = new double[n];
        for (var i = 0; i < n; i++)
        {
            re[i] = i < pcm.Length ? pcm[i] / 32768.0 : 0.0;
        }

        Analysis.Fft(re, im);

        var bestBin = 1;
        var bestEnergy = 0.0;
        for (var bin = 1; bin < n / 2; bin++)
        {
            var e = (re[bin] * re[bin]) + (im[bin] * im[bin]);
            if (e > bestEnergy)
            {
                bestEnergy = e;
                bestBin = bin;
            }
        }

        return bestBin * (double)AlarmTones.SampleRate / n;
    }

    [Fact(DisplayName = "ALRM-11 every alarm sound actually makes a sound — and none of them clips")]
    public void EverySoundIsAudible()
    {
        foreach (var sound in Settings.AlarmSounds)
        {
            var pcm = AlarmTones.Pcm(sound);
            var seconds = (double)pcm.Length / AlarmTones.SampleRate;
            Assert.True(PeakOf(pcm) > 0.3, $"{sound} is silent");
            Assert.True(PeakOf(pcm) <= 1.0, $"{sound} would clip");
            // Long enough to be heard, short enough to repeat: repetition is what wakes people.
            Assert.InRange(seconds, 1.0, 4.0);
        }
    }

    [Fact(DisplayName = "ALRM-11 every alarm sound ends in a gap — so it repeats instead of droning")]
    public void EverySoundEndsInAGap()
    {
        foreach (var sound in Settings.AlarmSounds)
        {
            var pcm = AlarmTones.Pcm(sound);
            var tailStart = (int)(pcm.Length * 0.92);
            var tailPeak = PeakOf(pcm[tailStart..]);
            Assert.True(tailPeak < 0.1, $"{sound} does not fall quiet before it loops (tail peak {tailPeak})");
        }
    }

    [Fact(DisplayName = "ALRM-11 the alarms are told apart by ear — calm and urgent do not converge on one pitch")]
    public void TheAlarmsAreToldApartByEar()
    {
        // A parent decides what to do by ear, before they can read the screen.
        var lowPulse = DominantHz(AlarmTones.Pcm(Settings.SoundLowPulse));
        var urgentBeep = DominantHz(AlarmTones.Pcm(Settings.SoundUrgentBeep));
        var softChime = DominantHz(AlarmTones.Pcm(Settings.SoundSoftChime));
        Assert.True(lowPulse < 350, $"the low pulse must actually be low ({lowPulse} Hz)");
        Assert.True(urgentBeep > 700, $"the urgent beep must be high ({urgentBeep} Hz)");
        Assert.InRange(softChime, 350.0, 1500.0);
    }

    [Fact(DisplayName = "ALRM-14 the alarm starts gentler and reaches full volume within a few seconds")]
    public void TheRampWakesWithoutStartling()
    {
        Assert.True(AlarmTones.RampGain(0) >= 0.3, "it must never start silent");
        Assert.True(AlarmTones.RampGain(0) < AlarmTones.RampGain(2_000));
        Assert.True(AlarmTones.RampGain(2_000) < AlarmTones.RampGain(5_000));
        Assert.Equal(1.0, AlarmTones.RampGain(5_000), 9); // full volume by then
        Assert.Equal(1.0, AlarmTones.RampGain(60_000), 9); // and it stays there
    }
}
