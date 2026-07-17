using BabyMonitor.Core.Json;
using BabyMonitor.Core.Logging;
using BabyMonitor.Core.Xiaomi;

namespace BabyMonitor.Core.Data;

/// <summary>Where non-secret state lives — settings, the camera choice. The app supplies the file.</summary>
public interface IKeyValueStore
{
    string? Get(string key);

    void Put(string key, string value);

    void Remove(string key);
}

/// <summary>
/// Encryption seam for secrets — DPAPI on Windows, the Keychain on the Mac, the Keystore on the
/// phone, a passthrough fake in tests.
///
/// **Failure is a null, never an exception.** A secret store can refuse — a roamed profile, a
/// corrupted blob, a machine key rotated by an OS repair — and the caller must be able to answer
/// that by dropping the session (AUTH-6), not by dying. A value cannot be misused; an exception
/// crossing this seam once took a whole monitor down with it.
/// </summary>
public interface ISecretBox
{
    /// <summary>Null when the secret could not be sealed. The caller must then NOT persist it (AUTH-6).</summary>
    string? Seal(string plain);

    /// <summary>Null when the secret could not be read back — a lost session, never a crash.</summary>
    string? Open(string sealed_);
}

public static class Codecs
{
    public static string SessionToJson(Session s) => new JsonObj()
        .Put("userId", s.UserId)
        .Put("cUserId", s.CUserId)
        .Put("passToken", s.PassToken)
        .Put("serviceToken", s.ServiceToken)
        .Put("ssecurity", s.Ssecurity.ToBase64())
        .Put("region", s.Region)
        .ToString();

    public static Session? SessionFromJson(string json)
    {
        try
        {
            var v = new JsonObj(json);
            return new Session(
                UserId: v.GetString("userId"),
                CUserId: v.OptString("cUserId"),
                PassToken: v.GetString("passToken"),
                ServiceToken: v.GetString("serviceToken"),
                Ssecurity: v.GetString("ssecurity").Base64ToBytes(),
                Region: v.GetString("region"));
        }
        catch (Exception e) when (e is JsonException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    public static string DeviceToJson(Device d) => new JsonObj()
        .Put("did", d.Did)
        .Put("name", d.Name)
        .Put("model", d.Model)
        .Put("mac", d.Mac)
        .Put("ip", d.Ip)
        .ToString();

    public static Device? DeviceFromJson(string json)
    {
        try
        {
            var v = new JsonObj(json);
            return new Device(
                Did: v.GetString("did"),
                Name: v.OptString("name"),
                Model: v.OptString("model"),
                Mac: v.OptString("mac"),
                Ip: v.OptString("ip"));
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// Everything the user can set. One shape, shared with the phone and the Mac — including its JSON,
/// its defaults, its clamps and its migrations (ALRM-6), so no platform can drift into meaning
/// something slightly different by "sensitivity 7".
/// </summary>
public sealed record Settings
{
    public const string ScheduleAlways = "always";
    public const string ScheduleWindow = "window";

    // ALRM-2: the sensitivity scale. 1 needs loud crying, 10 alarms on quiet crying.
    public const int SensitivityMin = 1;
    public const int SensitivityMax = 10;
    public const int SensitivityDefault = 5;

    // ALRM-4: never all the way down — a stored volume of zero would be a silent alarm.
    public const double VolumeMin = 0.2;
    public const double VolumeMax = 1.0;

    // WATCH-1: grace bounds — a zero grace would alarm on every one-second audio hiccup.
    public const int GraceMinSeconds = 5;
    public const int GraceMaxSeconds = 120;

    // ALRM-11: calm → urgent.
    public const string SoundSoftChime = "soft-chime";
    public const string SoundRisingChime = "rising-chime";
    public const string SoundLowPulse = "low-pulse";
    public const string SoundUrgentBeep = "urgent-beep";
    public const string SoundSiren = "siren";

    public static readonly IReadOnlyList<string> AlarmSounds = new[]
    {
        SoundSoftChime,
        SoundRisingChime,
        SoundLowPulse,
        SoundUrgentBeep,
        SoundSiren,
    };

    /// <summary>LIVE-18: the two pictures the camera offers. HD is what it actually has.</summary>
    public const string QualityHd = "hd";
    public const string QualitySd = "sd";

    public bool Muted { get; init; }

    /// <summary>
    /// LIVE-18: which picture this viewer asks the camera for. Unlike night vision (LIVE-10) it lives
    /// here rather than on the camera, because it is what *this* app requests — two people watching the
    /// same cot can choose differently. HD by default: the picture the camera has is the one to show
    /// unless someone says otherwise.
    /// </summary>
    public string VideoQuality { get; init; } = QualityHd;

    public bool AlarmEnabled { get; init; }

    /// <summary>ALRM-2: the one detection control — 1 (needs loud crying) .. 10 (quiet crying).</summary>
    public int AlarmSensitivity { get; init; } = SensitivityDefault;

    /// <summary>ALRM-7: "always" or a daily window (minutes since midnight, may cross midnight).</summary>
    public string AlarmScheduleMode { get; init; } = ScheduleAlways;

    public int AlarmWindowStartMinutes { get; init; } = 19 * 60;

    public int AlarmWindowEndMinutes { get; init; } = 7 * 60;

    /// <summary>WATCH-1: feed watchdog.</summary>
    public bool WatchdogEnabled { get; init; }

    public int WatchdogGraceSeconds { get; init; } = 30;

    // ALRM-11: how each alarm sounds. The default sounds differ so the two alarms are told apart by
    // ear out of the box (WATCH-2). A PC has no vibration motor, so the vibrate flags the phone
    // carries are read and written but never offered here (DESK-23).
    public string CryAlarmSound { get; init; } = SoundRisingChime;

    public string FeedAlarmSound { get; init; } = SoundLowPulse;

    public double CryAlarmVolume { get; init; } = 0.85;

    public double FeedAlarmVolume { get; init; } = 0.85;

    public bool CryAlarmVibrate { get; init; } = true;

    public bool FeedAlarmVibrate { get; init; } = true;

    public string ToJson() => new JsonObj()
        .Put("muted", Muted)
        .Put("videoQuality", VideoQuality)
        .Put("alarmEnabled", AlarmEnabled)
        .Put("alarmSensitivity", AlarmSensitivity)
        .Put("alarmScheduleMode", AlarmScheduleMode)
        .Put("alarmWindowStartMinutes", AlarmWindowStartMinutes)
        .Put("alarmWindowEndMinutes", AlarmWindowEndMinutes)
        .Put("watchdogEnabled", WatchdogEnabled)
        .Put("watchdogGraceSeconds", WatchdogGraceSeconds)
        .Put("cryAlarmSound", CryAlarmSound)
        .Put("feedAlarmSound", FeedAlarmSound)
        .Put("cryAlarmVolume", CryAlarmVolume)
        .Put("feedAlarmVolume", FeedAlarmVolume)
        .Put("cryAlarmVibrate", CryAlarmVibrate)
        .Put("feedAlarmVibrate", FeedAlarmVibrate)
        .ToString();

    public static Settings FromJson(string? json)
    {
        if (json == null)
        {
            return new Settings();
        }

        try
        {
            var v = new JsonObj(json);
            // ALRM-6: settings written before volume/vibrate were per-alarm had one shared
            // "alarmVolume"/"alarmVibrate" — an old value seeds both alarms rather than being lost.
            var legacyVolume = v.OptDouble("alarmVolume", 0.85);
            var legacyVibrate = v.OptBoolean("alarmVibrate", true);
            return new Settings
            {
                Muted = v.OptBoolean("muted", false),
                // Anything unrecognised is HD, not a stored string handed to the camera: a settings
                // file from a newer version, or a hand edit, must not end as a quality the camera does
                // not understand and a feed that never starts.
                VideoQuality = v.OptString("videoQuality", QualityHd) == QualitySd ? QualitySd : QualityHd,
                AlarmEnabled = v.OptBoolean("alarmEnabled", false),
                AlarmSensitivity = Math.Clamp(
                    v.OptInt("alarmSensitivity", LegacySensitivity(v)),
                    SensitivityMin,
                    SensitivityMax),
                AlarmScheduleMode = v.OptString("alarmScheduleMode", ScheduleAlways),
                AlarmWindowStartMinutes = Math.Clamp(v.OptInt("alarmWindowStartMinutes", 19 * 60), 0, (24 * 60) - 1),
                AlarmWindowEndMinutes = Math.Clamp(v.OptInt("alarmWindowEndMinutes", 7 * 60), 0, (24 * 60) - 1),
                WatchdogEnabled = v.OptBoolean("watchdogEnabled", false),
                // Clamped like sensitivity: stored data (old versions, hand edits) must never yield a
                // watchdog that fires on every hiccup or an alarm that plays silently.
                WatchdogGraceSeconds = Math.Clamp(
                    v.OptInt("watchdogGraceSeconds", 30),
                    GraceMinSeconds,
                    GraceMaxSeconds),
                CryAlarmSound = v.OptString("cryAlarmSound", SoundRisingChime),
                FeedAlarmSound = v.OptString("feedAlarmSound", SoundLowPulse),
                CryAlarmVolume = Math.Clamp(v.OptDouble("cryAlarmVolume", legacyVolume), VolumeMin, VolumeMax),
                FeedAlarmVolume = Math.Clamp(v.OptDouble("feedAlarmVolume", legacyVolume), VolumeMin, VolumeMax),
                CryAlarmVibrate = v.OptBoolean("cryAlarmVibrate", legacyVibrate),
                FeedAlarmVibrate = v.OptBoolean("feedAlarmVibrate", legacyVibrate),
            };
        }
        catch (JsonException)
        {
            return new Settings();
        }
    }

    /// <summary>
    /// ALRM-6: settings written before the sensitivity slider carried a dB threshold (3 = most
    /// sensitive .. 24 = least). Map it onto the 1..10 scale rather than lose the user's tuning. The
    /// old scale is frozen history — these constants must never track whatever the live sensitivity
    /// mapping becomes.
    /// </summary>
    private static int LegacySensitivity(JsonObj v)
    {
        if (!v.Has("alarmThresholdDb"))
        {
            return SensitivityDefault;
        }

        var mapped = (int)Math.Round((24.0 - v.OptDouble("alarmThresholdDb", 14.0)) / 2.0, MidpointRounding.AwayFromZero);
        return Math.Clamp(mapped, SensitivityMin, SensitivityMax);
    }
}

public sealed class AppStore
{
    private const string KeySession = "session_v1";
    private const string KeyDevice = "device_v1";
    private const string KeySettings = "settings_v1";
    private const string KeyMonitoring = "monitoring_v1";
    private const string KeyCryCalibration = "cry_calibration_v1"; // JSON object: camera did → steps

    private readonly IKeyValueStore _kv;
    private readonly ISecretBox _box;

    /// <summary>
    /// The decrypted session, held after the first read. Every caller that wants to know whether we
    /// are signed in — the router, the engine, the camera controls — would otherwise pay a full
    /// decrypt, and the UI asks on every level-meter tick.
    /// </summary>
    private volatile Session? _cachedSession;

    private volatile bool _sessionRead;

    public AppStore(IKeyValueStore kv, ISecretBox box)
    {
        _kv = kv;
        _box = box;
    }

    /// <summary>
    /// AUTH-6: tokens are only ever stored encrypted. If the secret store refuses, drop the session
    /// rather than crash or, worse, fall back to plaintext — the user simply signs in again.
    /// </summary>
    public bool SaveSession(Session s)
    {
        string? sealed_;
        try
        {
            sealed_ = _box.Seal(Codecs.SessionToJson(s));
        }
        catch (Exception e)
        {
            Log.W("data", $"could not encrypt session: {e.Message}", e);
            sealed_ = null;
        }

        if (sealed_ == null)
        {
            Log.W("data", "the secret store refused the session — not persisting it");
            _kv.Remove(KeySession);
            _cachedSession = null;
            _sessionRead = true;
            // AUTH-13: report the failure so a shell can say so, never a silent "signed in" that is
            // then quietly dropped back to the login screen.
            return false;
        }

        _kv.Put(KeySession, sealed_);
        _cachedSession = s;
        _sessionRead = true;
        return true;
    }

    public Session? LoadSession()
    {
        if (_sessionRead)
        {
            return _cachedSession;
        }

        var stored = _kv.Get(KeySession);
        var plain = stored == null ? null : _box.Open(stored);
        _cachedSession = plain == null ? null : Codecs.SessionFromJson(plain);
        _sessionRead = true;
        return _cachedSession;
    }

    public void SaveDevice(Device d) => _kv.Put(KeyDevice, Codecs.DeviceToJson(d));

    public Device? LoadDevice()
    {
        var stored = _kv.Get(KeyDevice);
        return stored == null ? null : Codecs.DeviceFromJson(stored);
    }

    public Settings LoadSettings() => Settings.FromJson(_kv.Get(KeySettings));

    public void SaveSettings(Settings s) => _kv.Put(KeySettings, s.ToJson());

    /// <summary>BG-13: was monitoring running when the process last went away (a restart, a crash)?</summary>
    public bool WasMonitoring() => _kv.Get(KeyMonitoring) == "true";

    public void SetMonitoring(bool on) => _kv.Put(KeyMonitoring, on ? "true" : "false");

    /// <summary>
    /// ALRM-17: the learned crying-alarm tuning, per camera. Plain steps; the bounds and their
    /// meaning live in the monitor layer — corrupt or absent data reads as "nothing learned".
    /// </summary>
    public int CryCalibrationSteps(string did)
    {
        try
        {
            var stored = _kv.Get(KeyCryCalibration);
            return stored == null ? 0 : new JsonObj(stored).OptInt(did, 0);
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    public void SaveCryCalibrationSteps(string did, int steps)
    {
        JsonObj all;
        try
        {
            all = new JsonObj(_kv.Get(KeyCryCalibration) ?? "{}");
        }
        catch (JsonException)
        {
            all = new JsonObj(); // corrupt store: better to relearn than to fail to save (ALRM-17)
        }

        _kv.Put(KeyCryCalibration, all.Put(did, steps).ToString());
    }

    /// <summary>
    /// AUTH-10: signing out forgets the session AND the selected camera (not settings — and not the
    /// learned tuning, which belongs to the camera, not the account).
    /// </summary>
    public void SignOut()
    {
        _kv.Remove(KeySession);
        _kv.Remove(KeyDevice);
        _cachedSession = null;
        _sessionRead = true;
    }

    /// <summary>CAM-4: switching camera forgets the choice, not the account — the picker comes back.</summary>
    public void ClearDevice() => _kv.Remove(KeyDevice);
}
