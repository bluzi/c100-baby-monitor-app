using BabyMonitor.Core.Data;
using BabyMonitor.Core.Xiaomi;
using Xunit;

namespace BabyMonitor.Core.Tests;

internal sealed class MemoryKv : IKeyValueStore
{
    public Dictionary<string, string> Map { get; } = new(StringComparer.Ordinal);

    public string? Get(string key) => Map.GetValueOrDefault(key);

    public void Put(string key, string value) => Map[key] = value;

    public void Remove(string key) => Map.Remove(key);
}

/// <summary>Reversible, visibly-marking fake so tests can prove sealing happened.</summary>
internal sealed class MarkingSecretBox : ISecretBox
{
    public string? Seal(string plain) => "SEALED:" + new string(plain.Reverse().ToArray());

    public string? Open(string sealed_) => sealed_.StartsWith("SEALED:", StringComparison.Ordinal)
        ? new string(sealed_["SEALED:".Length..].Reverse().ToArray())
        : null;
}

/// <summary>A secret store that says no — a DPAPI blob from another user, a rotated machine key.</summary>
internal sealed class RefusingSecretBox : ISecretBox
{
    public string? Seal(string plain) => null;

    public string? Open(string sealed_) => null;
}

/// <summary>A secret store that fails the other way: by throwing.</summary>
internal sealed class ThrowingSecretBox : ISecretBox
{
    public string? Seal(string plain) => throw new InvalidOperationException("the secret store is gone");

    public string? Open(string sealed_) => null;
}

public class StoreTest
{
    private readonly MemoryKv _kv = new();
    private readonly AppStore _store;

    public StoreTest() => _store = new AppStore(_kv, new MarkingSecretBox());

    private static Session SampleSession() => new(
        UserId: "100001",
        CUserId: "CU1",
        PassToken: "PT-secret",
        ServiceToken: "ST-secret",
        Ssecurity: new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
        Region: "de");

    private static Device SampleDevice() =>
        new("did-1", "Nursery", "chuangmi.camera.077ac1", "AA:BB", "192.168.1.50");

    private string StoredText() => string.Join(",", _kv.Map.Values);

    [Fact(DisplayName = "AUTH-5 a saved session round-trips including the ssecurity bytes")]
    public void SessionRoundTrips()
    {
        _store.SaveSession(SampleSession());
        Assert.Equal(SampleSession(), _store.LoadSession());
    }

    [Fact(DisplayName = "AUTH-6 what hits storage is the sealed form — never plaintext tokens")]
    public void OnlyTheSealedFormIsStored()
    {
        _store.SaveSession(SampleSession());
        var stored = StoredText();
        Assert.Contains("SEALED:", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("PT-secret", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("ST-secret", stored, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "AUTH-12 a secret store that refuses drops the session — never a crash, never plaintext")]
    public void ARefusingStoreDropsTheSession()
    {
        var kv = new MemoryKv();
        var store = new AppStore(kv, new RefusingSecretBox());
        store.SaveSession(SampleSession());

        Assert.Null(store.LoadSession());
        var stored = string.Join(",", kv.Map.Values);
        Assert.DoesNotContain("PT-secret", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("ST-secret", stored, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "AUTH-12 a secret store that throws drops the session rather than taking the monitor down")]
    public void AThrowingStoreDropsTheSession()
    {
        var kv = new MemoryKv();
        var store = new AppStore(kv, new ThrowingSecretBox());
        store.SaveSession(SampleSession()); // must not propagate

        Assert.Null(store.LoadSession());
        Assert.DoesNotContain("PT-secret", string.Join(",", kv.Map.Values), StringComparison.Ordinal);
        Assert.False(kv.Map.ContainsKey("session_v1")); // and must not fall back to plaintext
    }

    [Fact(DisplayName = "AUTH-13 SaveSession reports whether the session actually reached the store")]
    public void SaveSessionReportsWhetherItPersisted()
    {
        // The honesty signal every shell relies on: true means signed in and persisted, false means
        // authenticated but not stored — and a shell that ignores it reports a phantom sign-in and
        // drops the user back to login with nothing said.
        Assert.True(new AppStore(new MemoryKv(), new MarkingSecretBox()).SaveSession(SampleSession()));
        Assert.False(new AppStore(new MemoryKv(), new RefusingSecretBox()).SaveSession(SampleSession()));
        Assert.False(new AppStore(new MemoryKv(), new ThrowingSecretBox()).SaveSession(SampleSession()));
    }

    [Fact(DisplayName = "AUTH-10 signing out forgets session and camera but keeps settings and learned tuning")]
    public void SignOutForgetsTheRightThings()
    {
        _store.SaveSession(SampleSession());
        _store.SaveDevice(SampleDevice());
        _store.SaveSettings(new Settings { Muted = true, AlarmEnabled = true, AlarmSensitivity = 7 });
        _store.SaveCryCalibrationSteps(SampleDevice().Did, 2);

        _store.SignOut();

        Assert.Null(_store.LoadSession());
        Assert.Null(_store.LoadDevice());
        Assert.True(_store.LoadSettings().Muted);
        // ALRM-17: the tuning belongs to the camera, not the session — re-signing-in keeps it.
        Assert.Equal(2, _store.CryCalibrationSteps(SampleDevice().Did));
    }

    [Fact(DisplayName = "CAM-2+3 the chosen camera persists and loads back")]
    public void CameraPersists()
    {
        Assert.Null(_store.LoadDevice());
        _store.SaveDevice(SampleDevice());
        Assert.Equal(SampleDevice(), _store.LoadDevice());
    }

    [Fact(DisplayName = "CAM-4 choosing another camera replaces the stored one")]
    public void CameraCanBeReplaced()
    {
        _store.SaveDevice(SampleDevice());
        var other = new Device("did-2", "Bedroom", "chuangmi.camera.077ac1", "EE:FF", "192.168.1.51");
        _store.SaveDevice(other);
        Assert.Equal(other, _store.LoadDevice());
    }

    [Fact(DisplayName = "LIVE-2 the mute state persists across restarts")]
    public void MutePersists()
    {
        Assert.False(_store.LoadSettings().Muted); // default: unmuted
        _store.SaveSettings(_store.LoadSettings() with { Muted = true });
        Assert.True(new AppStore(_kv, new MarkingSecretBox()).LoadSettings().Muted);
    }

    [Fact(DisplayName = "ALRM-1+6 the alarm defaults off and its settings persist")]
    public void AlarmSettingsPersist()
    {
        var defaults = _store.LoadSettings();
        Assert.False(defaults.AlarmEnabled); // ALRM-1: off by default
        _store.SaveSettings(defaults with { AlarmEnabled = true, AlarmSensitivity = 8 });
        var reloaded = new AppStore(_kv, new MarkingSecretBox()).LoadSettings();
        Assert.True(reloaded.AlarmEnabled);
        Assert.Equal(8, reloaded.AlarmSensitivity);
    }

    [Fact(DisplayName = "ALRM-6 a threshold saved by an older version maps onto the sensitivity scale")]
    public void LegacyThresholdMigrates()
    {
        // Settings written before the sensitivity slider existed carry a dB threshold instead.
        _kv.Put("settings_v1", """{"alarmEnabled":true,"alarmThresholdDb":12.0}""");
        Assert.Equal(6, _store.LoadSettings().AlarmSensitivity); // the old default lands mid-scale
        _kv.Put("settings_v1", """{"alarmThresholdDb":3.0}"""); // old "most sensitive"
        Assert.Equal(Settings.SensitivityMax, _store.LoadSettings().AlarmSensitivity);
        _kv.Put("settings_v1", """{"alarmThresholdDb":24.0}"""); // old "least sensitive"
        Assert.Equal(Settings.SensitivityMin, _store.LoadSettings().AlarmSensitivity);
    }

    [Fact(DisplayName = "ALRM-17 learned tuning persists per camera and survives a restart")]
    public void LearnedTuningPersists()
    {
        Assert.Equal(0, _store.CryCalibrationSteps("did-1")); // nothing learned yet
        _store.SaveCryCalibrationSteps("did-1", 2);
        _store.SaveCryCalibrationSteps("did-2", 1);
        var reloaded = new AppStore(_kv, new MarkingSecretBox());
        Assert.Equal(2, reloaded.CryCalibrationSteps("did-1"));
        Assert.Equal(1, reloaded.CryCalibrationSteps("did-2")); // each camera keeps its own
        reloaded.SaveCryCalibrationSteps("did-1", 0); // reset touches only its camera
        Assert.Equal(0, reloaded.CryCalibrationSteps("did-1"));
        Assert.Equal(1, reloaded.CryCalibrationSteps("did-2"));
    }

    [Fact(DisplayName = "ALRM-17 corrupt learned tuning degrades to nothing learned")]
    public void CorruptTuningDegrades()
    {
        _kv.Put("cry_calibration_v1", "{broken");
        Assert.Equal(0, _store.CryCalibrationSteps("did-1"));
        _store.SaveCryCalibrationSteps("did-1", 1); // and can be written over
        Assert.Equal(1, _store.CryCalibrationSteps("did-1"));
    }

    [Fact(DisplayName = "ALRM-6+7 the alarm schedule persists")]
    public void SchedulePersists()
    {
        Assert.Equal(Settings.ScheduleAlways, _store.LoadSettings().AlarmScheduleMode); // default
        _store.SaveSettings(_store.LoadSettings() with
        {
            AlarmScheduleMode = Settings.ScheduleWindow,
            AlarmWindowStartMinutes = 20 * 60,
            AlarmWindowEndMinutes = (6 * 60) + 30,
        });
        var reloaded = new AppStore(_kv, new MarkingSecretBox()).LoadSettings();
        Assert.Equal(Settings.ScheduleWindow, reloaded.AlarmScheduleMode);
        Assert.Equal(20 * 60, reloaded.AlarmWindowStartMinutes);
        Assert.Equal((6 * 60) + 30, reloaded.AlarmWindowEndMinutes);
    }

    [Fact(DisplayName = "WATCH-1+5 the watchdog defaults off and its settings persist")]
    public void WatchdogSettingsPersist()
    {
        var defaults = _store.LoadSettings();
        Assert.False(defaults.WatchdogEnabled); // WATCH-1: off by default
        Assert.Equal(30, defaults.WatchdogGraceSeconds); // WATCH-1: default grace
        _store.SaveSettings(defaults with { WatchdogEnabled = true, WatchdogGraceSeconds = 15 });
        var reloaded = new AppStore(_kv, new MarkingSecretBox()).LoadSettings();
        Assert.True(reloaded.WatchdogEnabled);
        Assert.Equal(15, reloaded.WatchdogGraceSeconds);
    }

    [Fact(DisplayName = "BG-19 picture-in-picture defaults on and the switch persists")]
    public void PipEnabledDefaultsOnAndPersists()
    {
        Assert.True(_store.LoadSettings().PipEnabled); // on by default
        _store.SaveSettings(_store.LoadSettings() with { PipEnabled = false });
        Assert.False(new AppStore(_kv, new MarkingSecretBox()).LoadSettings().PipEnabled);
    }

    [Fact(DisplayName = "ALRM-4+6 a stored alarm volume of zero is floored — a silent alarm is not an alarm")]
    public void VolumeIsFloored()
    {
        _kv.Put("settings_v1", """{"cryAlarmVolume":0.0,"feedAlarmVolume":0.0}""");
        Assert.Equal(Settings.VolumeMin, _store.LoadSettings().CryAlarmVolume, 9);
        Assert.Equal(Settings.VolumeMin, _store.LoadSettings().FeedAlarmVolume, 9);
        _kv.Put("settings_v1", """{"cryAlarmVolume":7.5,"feedAlarmVolume":7.5}""");
        Assert.Equal(Settings.VolumeMax, _store.LoadSettings().CryAlarmVolume, 9);
        Assert.Equal(Settings.VolumeMax, _store.LoadSettings().FeedAlarmVolume, 9);
        // A zero volume from an older version must be floored too, on both alarms.
        _kv.Put("settings_v1", """{"alarmVolume":0.0}""");
        Assert.Equal(Settings.VolumeMin, _store.LoadSettings().CryAlarmVolume, 9);
        Assert.Equal(Settings.VolumeMin, _store.LoadSettings().FeedAlarmVolume, 9);
    }

    [Fact(DisplayName = "WATCH-1+5 a stored grace of zero cannot make the watchdog fire on every hiccup")]
    public void GraceIsClamped()
    {
        _kv.Put("settings_v1", """{"watchdogGraceSeconds":0}""");
        Assert.Equal(Settings.GraceMinSeconds, _store.LoadSettings().WatchdogGraceSeconds);
        _kv.Put("settings_v1", """{"watchdogGraceSeconds":-3}""");
        Assert.Equal(Settings.GraceMinSeconds, _store.LoadSettings().WatchdogGraceSeconds);
        _kv.Put("settings_v1", """{"watchdogGraceSeconds":99999}""");
        Assert.Equal(Settings.GraceMaxSeconds, _store.LoadSettings().WatchdogGraceSeconds);
    }

    [Fact(DisplayName = "ALRM-6+7 stored schedule minutes outside a day are clamped into one")]
    public void ScheduleMinutesAreClamped()
    {
        _kv.Put("settings_v1", """{"alarmWindowStartMinutes":99999,"alarmWindowEndMinutes":-5}""");
        var s = _store.LoadSettings();
        Assert.Equal((24 * 60) - 1, s.AlarmWindowStartMinutes);
        Assert.Equal(0, s.AlarmWindowEndMinutes);
    }

    [Fact(DisplayName = "corrupt stored data degrades to defaults instead of crashing")]
    public void CorruptDataDegrades()
    {
        _kv.Put("session_v1", "SEALED:not-json");
        _kv.Put("device_v1", "{broken");
        _kv.Put("settings_v1", "{broken");
        Assert.Null(_store.LoadSession());
        Assert.Null(_store.LoadDevice());
        Assert.Equal(new Settings(), _store.LoadSettings());
    }

    [Fact(DisplayName = "BG-13 the store remembers whether monitoring was running — so a restart can be reported")]
    public void MonitoringFlagSurvivesARestart()
    {
        Assert.False(_store.WasMonitoring()); // nothing claimed before the first run
        _store.SetMonitoring(true);
        Assert.True(new AppStore(_kv, new MarkingSecretBox()).WasMonitoring());
        _store.SetMonitoring(false);
        Assert.False(new AppStore(_kv, new MarkingSecretBox()).WasMonitoring());
    }

    [Fact(DisplayName = "ALRM-11+WATCH-2 the alarms differ by default but may be given the same sound")]
    public void AlarmSoundsMayMatch()
    {
        Assert.NotEqual(new Settings().CryAlarmSound, new Settings().FeedAlarmSound); // distinct by default

        var same = new Settings
        {
            CryAlarmSound = Settings.SoundSiren,
            FeedAlarmSound = Settings.SoundSiren,
        };
        var reloaded = Settings.FromJson(same.ToJson()); // the choice survives storage un-"repaired"
        Assert.Equal(Settings.SoundSiren, reloaded.CryAlarmSound);
        Assert.Equal(Settings.SoundSiren, reloaded.FeedAlarmSound);
    }

    [Fact(DisplayName = "ALRM-6 each alarm's sound, volume and vibrate persists on its own")]
    public void EachAlarmKeepsItsOwnSettings()
    {
        var settings = new Settings
        {
            CryAlarmSound = Settings.SoundSiren,
            FeedAlarmSound = Settings.SoundSoftChime,
            CryAlarmVolume = 0.6,
            FeedAlarmVolume = 1.0,
            CryAlarmVibrate = true,
            FeedAlarmVibrate = false,
        };
        var reloaded = Settings.FromJson(settings.ToJson());
        Assert.Equal(Settings.SoundSiren, reloaded.CryAlarmSound);
        Assert.Equal(Settings.SoundSoftChime, reloaded.FeedAlarmSound);
        Assert.Equal(0.6, reloaded.CryAlarmVolume, 9);
        Assert.Equal(1.0, reloaded.FeedAlarmVolume, 9);

        // The flags are dead on a PC (DESK-23) but ALRM-6 is universal, so the port round-trips them
        // like the phone: the same spec suite, criterion for criterion.
        Assert.True(reloaded.CryAlarmVibrate);
        Assert.False(reloaded.FeedAlarmVibrate);
    }

    [Fact(DisplayName = "ALRM-6 a single volume and vibrate saved by an older version applies to both alarms")]
    public void LegacyVolumeSeedsBoth()
    {
        var loaded = Settings.FromJson("""{"alarmVolume":0.6,"alarmVibrate":false}""");
        Assert.Equal(0.6, loaded.CryAlarmVolume, 9);
        Assert.Equal(0.6, loaded.FeedAlarmVolume, 9);
        Assert.False(loaded.CryAlarmVibrate);
        Assert.False(loaded.FeedAlarmVibrate);
    }
}
