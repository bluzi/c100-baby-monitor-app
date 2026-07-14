using BabyMonitor.Core.Dsp;
using Xunit;

namespace BabyMonitor.Core.Tests;

// The measurements the crying alarm reasons about (ALRM-3): loudness, pitch, tonality, brightness.

public class DspTest
{
    [Fact(DisplayName = "analyzeWindow reports loudness")]
    public void Loudness()
    {
        var metrics = Analysis.AnalyzeWindow(Sounds.Sine(1000.0, 48000, 2048), 48000);
        Assert.Equal(0.5 / Math.Sqrt(2.0), metrics.Rms, 0.02);
        Assert.Equal(0.5, metrics.Peak, 0.02);
    }

    [Fact(DisplayName = "ALRM-3 pitch is measured — and a baby's pitch is told apart from an adult's")]
    public void PitchTellsBabyFromAdult()
    {
        var baby = Analysis.AnalyzeWindow(Sounds.Sine(450.0, 48000, 4096), 48000);
        Assert.Equal(450.0, baby.PitchHz, 25.0);
        Assert.InRange(baby.PitchHz, Analysis.CryPitchMinHz, Analysis.CryPitchMaxHz);

        var adult = Analysis.AnalyzeWindow(Sounds.Sine(120.0, 48000, 4096), 48000);
        Assert.Equal(120.0, adult.PitchHz, 12.0);
        Assert.True(adult.PitchHz < Analysis.CryPitchMinHz, "an adult's pitch is below the cry range");
    }

    [Fact(DisplayName = "ALRM-3 pitch does not drop an octave and mistake a baby for an adult")]
    public void NoOctaveError()
    {
        // A periodic signal also correlates at multiples of its period. Picking the wrong one would
        // report a crying baby at half her pitch — squarely in the adult range — and drop the cry.
        var cry = Analysis.AnalyzeWindow(Sounds.BabyCry(4096, f0: 500.0), 48000);
        Assert.InRange(cry.PitchHz, Analysis.CryPitchMinHz, Analysis.CryPitchMaxHz);
    }

    [Fact(DisplayName = "ALRM-3 a clear tone is tonal — noise is not")]
    public void Tonality()
    {
        Assert.True(Analysis.AnalyzeWindow(Sounds.Sine(450.0, 48000, 4096), 48000).Tonality > 0.8);
        Assert.True(Analysis.AnalyzeWindow(Sounds.WhiteNoise(4096), 48000).Tonality < 0.3);
    }

    [Fact(DisplayName = "ALRM-13 a wall takes the brightness away — that is how muffled speech is spotted")]
    public void Brightness()
    {
        var inRoom = Analysis.AnalyzeWindow(Sounds.BabyCry(4096), 48000);
        var throughWall = Analysis.AnalyzeWindow(Sounds.MuffledSpeechThroughWall(4096), 48000);
        Assert.True(inRoom.Brightness > 0.12, $"in-room cry is bright ({inRoom.Brightness})");
        Assert.True(throughWall.Brightness < 0.12, $"muffled speech is dark ({throughWall.Brightness})");
    }

    [Fact(DisplayName = "ALRM-13 rumble piles its energy at the bottom — a cry does not")]
    public void LowRatio()
    {
        Assert.True(Analysis.AnalyzeWindow(Sounds.Rumble(4096), 48000).LowRatio > 0.55);
        Assert.True(Analysis.AnalyzeWindow(Sounds.BabyCry(4096), 48000).LowRatio < 0.55);
    }

    [Fact(DisplayName = "silence has no loudness and no pitch")]
    public void Silence()
    {
        var metrics = Analysis.AnalyzeWindow(new short[2048], 48000);
        Assert.Equal(0.0, metrics.Rms, 1e-9);
        Assert.Equal(0.0, metrics.PitchHz, 1e-9);
    }
}
