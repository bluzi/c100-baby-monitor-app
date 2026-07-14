using System.ComponentModel;
using System.Runtime.CompilerServices;
using BabyMonitor.App.Services;
using BabyMonitor.Core.Data;
using BabyMonitor.Core.Monitor;
using BabyMonitor.Core.Platform;
using BabyMonitor.Core.Shell;
using BabyMonitor.Core.Ui;
using BabyMonitor.Core.Xiaomi;
using Microsoft.UI.Dispatching;
using Log = BabyMonitor.App.Services.Logging.Log;

namespace BabyMonitor.App;

/// <summary>
/// The shell's view of the monitor. A thin observer: it holds no decisions of its own — whether
/// monitoring can be stopped, what the status says, how loud is loud, whether the mini window may fade
/// — all of it comes from the shared core, which is the same logic the phone and the Mac run.
/// </summary>
public sealed class AppState : INotifyPropertyChanged
{
    private readonly DispatcherQueue _ui;
    private readonly AppStore _store;
    private readonly ToneRinger _ringer;
    private readonly WindowsMedia _media;
    private readonly NetworkWatcher _network;

    private MonitorEngine? _engine;
    private LoginResult? _pendingLogin;

    private bool _hasSession;
    private bool _hasDevice;
    private bool _sleepUnprotected;
    private bool _networkDown;
    private bool _videoUnavailable;
    private string? _sleepOutage;
    private long _sleptAtMs;
    private double _videoAspect;
    private UpdateStatus _updateStatus = UpdateStatus.Idle;

    public AppState(DispatcherQueue ui)
    {
        _ui = ui;
        _store = new AppStore(new JsonFileStore(), new DpapiSecretBox());
        _ringer = new ToneRinger(() => new WasapiAlarmVoice());
        _media = new WindowsMedia(OnVideoSize);
        _network = new NetworkWatcher(down => Post(() =>
        {
            NetworkDown = down;
        }));

        MonitorHub.ApplySettings(_store.LoadSettings());
        RefreshRouting();

        _networkDown = NetworkWatcher.IsDown;

        // One listener over everything the UI can show. Every one of these fires on a background
        // thread; the UI never touches them directly.
        MonitorHub.Running.Changed += _ => OnMonitorChanged();
        MonitorHub.Status.Changed += _ => OnMonitorChanged();
        MonitorHub.Level.Changed += _ => Post(Emit);
        MonitorHub.CameraName.Changed += _ => Post(Emit);
        MonitorHub.Settings.Changed += _ => Post(Emit);
        MonitorHub.ActiveAlarm.Changed += _ => Post(Emit);
        MonitorHub.SessionExpired.Changed += expired => Post(() => OnSessionExpired(expired));
        MonitorHub.PendingCryFeedback.Changed += _ => Post(Emit);
        MonitorHub.CalibrationSteps.Changed += _ => Post(Emit);

        SystemPreferences.Changed += () => Post(Emit);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the monitor's state changed enough that the window may need to react.</summary>
    public event Action? StateChanged;

    // --- what the UI reads ---------------------------------------------------

    public string Screen => Router.Route(_hasSession, _hasDevice) switch
    {
        Core.Ui.Screen.Login => "login",
        Core.Ui.Screen.Devices => "devices",
        _ => "viewer",
    };

    public bool Running => MonitorHub.Running.Value;

    public string Status => MonitorHub.Status.Value;

    public string StatusText => Statuses.FriendlyStatus(Status);

    public string StatusLine => Statuses.StatusLine(CameraName, Status, Muted);

    public string CameraName => MonitorHub.CameraName.Value;

    /// <summary>LIVE-6: what the level bar shows — the squelched value, decided in the core.</summary>
    public double Level => LevelMeter.DisplayLevelDb(MonitorHub.Level.Value);

    public double LevelMax => LevelMeter.LevelMax;

    public double ThresholdDb => CryCalibration.EffectiveThresholdDb(
        MonitorHub.Settings.Value.AlarmSensitivity,
        MonitorHub.CalibrationSteps.Value);

    public bool Muted => MonitorHub.Settings.Value.Muted;

    public bool AlarmEnabled => MonitorHub.Settings.Value.AlarmEnabled;

    public AlarmKind? ActiveAlarm => MonitorHub.ActiveAlarm.Value;

    public bool Alarming => ActiveAlarm != null;

    public string AlarmText => ActiveAlarm == AlarmKind.BabyNoise
        ? "The baby is crying"
        : "The feed is down";

    public bool SessionExpired => MonitorHub.SessionExpired.Value;

    /// <summary>AUTH-8: set when an expiry has just sent the parent back to sign-in, shown there once.</summary>
    public string? SessionExpiredMessage { get; private set; }

    public bool AskingCryFeedback => MonitorHub.PendingCryFeedback.Value != null;

    public int CalibrationSteps => MonitorHub.CalibrationSteps.Value;

    /// <summary>
    /// BG-14 / WATCH-11: the shared decision, so the tray menu and the window cannot disagree — and
    /// so nobody can quietly hand a PC a Stop button back. There is deliberately no `CanStop`: a PC
    /// stops by exiting (DESK-3), and Start exists only for a monitor that failed on its own.
    /// </summary>
    public bool CanResume =>
        DesktopShell.ViewerActions(Running, Status).Contains(ViewerActionKind.Resume);

    public MonitorHealth Health => new(
        Running: Running,
        Status: Status,
        ActiveAlarm: ActiveAlarm?.ToString(),
        SessionExpired: SessionExpired,
        SleepOutage: SleepOutage);

    /// <summary>DESK-11: may the mini window fade right now, or is there something to be read?</summary>
    public bool NeedsAttention => DesktopShell.NeedsAttention(Health);

    /// <summary>DESK-21: the PC slept, so the monitor was down. Never a quiet reconnect.</summary>
    public string? SleepOutage
    {
        get => _sleepOutage;
        private set => Set(ref _sleepOutage, value);
    }

    /// <summary>BG-12: monitoring is running but Windows would not promise to stay awake. Say so.</summary>
    public bool SleepUnprotected
    {
        get => _sleepUnprotected;
        private set => Set(ref _sleepUnprotected, value);
    }

    /// <summary>LIVE-13: the camera lives on your network, and this PC is not on one.</summary>
    public bool NetworkDown
    {
        get => _networkDown;
        private set => Set(ref _networkDown, value);
    }

    /// <summary>DESK-22: Windows cannot decode this camera's video. Audio monitoring carries on.</summary>
    public bool VideoUnavailable
    {
        get => _videoUnavailable;
        private set => Set(ref _videoUnavailable, value);
    }

    /// <summary>DESK-12: the shape of the picture the camera sends (width ÷ height), or 0 before there is one.</summary>
    public double VideoAspect
    {
        get => _videoAspect;
        private set => Set(ref _videoAspect, value);
    }

    public UpdateStatus UpdateStatus
    {
        get => _updateStatus;
        set => Set(ref _updateStatus, value);
    }

    public bool MiniFadeEnabled
    {
        get => Prefs.MiniFadeEnabled;
        set
        {
            Prefs.MiniFadeEnabled = value;
            Emit();
        }
    }

    public double MiniIdleOpacity
    {
        get => Prefs.MiniIdleOpacity;
        set
        {
            Prefs.MiniIdleOpacity = value;
            Emit();
        }
    }

    public bool StartWithWindows => StartupRegistry.IsEnabled;

    public string? StartupError { get; private set; }

    /// <summary>DESK-19: offered once, plainly, and never turned on for you.</summary>
    public bool StartupOfferPending => !Prefs.StartupOfferMade && !StartupRegistry.IsEnabled;

    public string Version => Updater.CurrentVersion;

    public IReadOnlyList<string> Regions => Mi.Regions;

    public IReadOnlyList<(string Id, string Label, string Description)> AlarmSounds =>
        Settings.AlarmSounds
            .Select(s => (s, AlarmTones.Label(s), AlarmTones.Description(s)))
            .ToList();

    public Settings Settings => MonitorHub.Settings.Value;

    /// <summary>DESK-11 / DESK-18: how solid the mini window is drawn right now. The core decides.</summary>
    public double MiniOpacity(bool hovering) => DesktopShell.MiniOpacity(
        Health,
        hovering,
        Prefs.MiniFadeEnabled,
        SystemPreferences.TransparencyDisabled,
        Prefs.MiniIdleOpacity);

    /// <summary>DESK-9: which shape the one window may take. Sign-in is never a tile.</summary>
    public string Shape => DesktopShell.WindowShape(Screen, Prefs.Shape);

    // --- monitoring ----------------------------------------------------------

    public void Start()
    {
        var engine = _engine ??= new MonitorEngine(_store, _ringer, _media);
        engine.Start();
        Emit();
    }

    /// <summary>
    /// BG-14: **not a control.** There is no Stop on a PC — the app watches until it is exited. This
    /// is the machinery behind the things that *do* end a watch: exiting, signing out, and switching
    /// camera (which stops one stream to start another).
    /// </summary>
    public void Stop()
    {
        _engine?.Stop();
        _store.SetMonitoring(false); // deliberate: a restart must not resume by itself (BG-13)
        Emit();
    }

    public void Acknowledge()
    {
        MonitorHub.Acknowledge();
        Emit();
    }

    public void ToggleMute() => SaveSettings(Settings with { Muted = !Settings.Muted });

    public void SubmitCryFeedback(bool wasCry)
    {
        MonitorHub.SubmitCryFeedback(wasCry);
        Emit();
    }

    public void DismissCryFeedback()
    {
        MonitorHub.DismissCryFeedback();
        Emit();
    }

    public void ResetCalibration()
    {
        MonitorHub.ResetCalibration();
        Emit();
    }

    /// <summary>BG-13: was monitoring running when the app last went away (a restart, a crash)?</summary>
    public bool WasMonitoringBeforeRestart() => _store.WasMonitoring();

    // --- sign-in -------------------------------------------------------------

    public async Task<SignInStep> SignInAsync(string username, string password, string region)
    {
        var cloud = new MiCloud(Core.Net.SystemMiHttp.Shared, region: region);
        return await FinishSignInAsync(() => cloud.LoginAsync(username, password)).ConfigureAwait(true);
    }

    public async Task<SignInStep> SubmitCaptchaAsync(string code)
    {
        if (_pendingLogin is not LoginResult.Captcha captcha)
        {
            return new SignInStep("error", Message: "There is no captcha to answer.");
        }

        return await FinishSignInAsync(() => captcha.Submit(code)).ConfigureAwait(true);
    }

    public async Task<SignInStep> SubmitCodeAsync(string ticket)
    {
        if (_pendingLogin is not LoginResult.TwoFactor twoFactor)
        {
            return new SignInStep("error", Message: "There is no code to submit.");
        }

        return await FinishSignInAsync(() => twoFactor.Submit(ticket)).ConfigureAwait(true);
    }

    /// <summary>AUTH-10: signing out forgets the session AND the selected camera.</summary>
    public void SignOut()
    {
        Stop();
        _store.SignOut();
        _store.SetMonitoring(false);
        _pendingLogin = null;
        MonitorHub.SessionExpired.Value = false;
        RefreshRouting();
        Emit();
    }

    /// <summary>
    /// AUTH-8: the server refused the stored token while monitoring. The parent must land back on
    /// sign-in — not sit on a live-looking viewer that is quietly dead, with the only way out three
    /// levels into a menu. This mirrors the phone exactly (ViewerScreen's session-expired effect):
    /// forget the session, route to login, and carry the reason so the login screen can state it.
    /// </summary>
    private void OnSessionExpired(bool expired)
    {
        if (!expired)
        {
            Emit();
            return;
        }

        Log.Warn("ui", "the session expired — returning to sign-in (AUTH-8)");
        SessionExpiredMessage = "Your session expired — please sign in again.";
        Stop();
        _store.SignOut();
        _store.SetMonitoring(false);
        _pendingLogin = null;
        MonitorHub.SessionExpired.Value = false; // consumed: the routing below is the response to it
        RefreshRouting();
        Emit();
    }

    /// <summary>The login screen shows the expiry message once, then clears it.</summary>
    public string? TakeSessionExpiredMessage()
    {
        var message = SessionExpiredMessage;
        SessionExpiredMessage = null;
        return message;
    }

    // --- cameras -------------------------------------------------------------

    public async Task<(IReadOnlyList<CameraInfo>? Cameras, string? Error)> LoadCamerasAsync()
    {
        try
        {
            var session = _store.LoadSession();
            if (session == null)
            {
                return (null, "You are not signed in.");
            }

            var cloud = new MiCloud(Core.Net.SystemMiHttp.Shared, session: session)
            {
                OnSessionRefreshed = s =>
                {
                    _store.SaveSession(s); // AUTH-7
                    return Task.CompletedTask;
                },
            };

            var devices = await cloud.DeviceListAsync().ConfigureAwait(true);
            var cameras = devices
                .Where(d => Mi.IsCamera(d.Model)) // PROTO-11
                .Select(d => new CameraInfo(d.Did, d.Name, d.Model, d.Mac, d.Ip))
                .ToList();
            return (cameras, null);
        }
        catch (Exception e)
        {
            // CAM-5 / APP-3: a readable message with a way to retry — never a dead end.
            Log.Warn("ui", $"could not load the cameras: {e.Message}");
            return (null, e.Message);
        }
    }

    public void SelectCamera(CameraInfo camera)
    {
        _store.SaveDevice(new Device(camera.Did, camera.Name, camera.Model, camera.Mac, camera.Ip));
        RefreshRouting();
        Emit();
    }

    /// <summary>The camera being watched, for the tray's camera submenu (DESK-2).</summary>
    public CameraInfo? SelectedCamera => _store.LoadDevice() is { } d
        ? new CameraInfo(d.Did, d.Name, d.Model, d.Mac, d.Ip)
        : null;

    /// <summary>
    /// CAM-4 / DESK-2: switch straight to a named camera, without going through the picker.
    ///
    /// The engine reads the selected camera when it connects, so stopping it, changing the choice and
    /// starting it again is all it takes — and the app is watching the new room within seconds, which
    /// is the point: a parent with two children should not have to visit a settings screen to look at
    /// the other one.
    /// </summary>
    public void SwitchToCamera(CameraInfo camera)
    {
        if (camera.Did == SelectedCamera?.Did)
        {
            return; // already watching it; restarting the stream would only cost them a few seconds
        }

        Log.Info("ui", $"switching camera to {camera.Name} did={camera.Did}");
        Stop();
        SelectCamera(camera);
        Start();
    }

    /// <summary>CAM-4: switching camera stops the old stream and sends the user back to the picker.</summary>
    public void SwitchCamera()
    {
        Stop();
        _store.ClearDevice();
        RefreshRouting();
        Emit();
    }

    // --- settings ------------------------------------------------------------

    public void SaveSettings(Settings settings)
    {
        _store.SaveSettings(settings);
        MonitorHub.ApplySettings(settings); // ALRM-2: effective immediately
        Emit();
    }

    public void PreviewAlarm(string sound, double volume) => _ = _ringer.PreviewAsync(sound, volume);

    public void SetStartWithWindows(bool enabled)
    {
        StartupError = StartupRegistry.Set(enabled);

        // DESK-19: only count the offer as answered if it actually took. If the registry refused (group
        // policy, a locked-down Run key), marking it answered would hide the banner as though it worked
        // — and the PC would restart overnight to no monitor (BG-13), silently. A failure keeps the
        // offer up and shows why.
        if (StartupError == null)
        {
            Prefs.StartupOfferMade = true;
        }

        Emit();
    }

    /// <summary>The offer has been answered, either way. It is not asked again.</summary>
    public void AnswerStartupOffer()
    {
        Prefs.StartupOfferMade = true;
        Emit();
    }

    // --- camera controls -----------------------------------------------------

    public async Task<(NightVisionMode? Mode, string? Error)> GetNightVisionAsync()
    {
        try
        {
            return (await CameraControl.GetNightVisionAsync(_store).ConfigureAwait(true), null);
        }
        catch (Exception e)
        {
            return (null, e.Message);
        }
    }

    public async Task<string?> SetNightVisionAsync(NightVisionMode mode)
    {
        try
        {
            await CameraControl.SetNightVisionAsync(_store, mode).ConfigureAwait(true);
            return null;
        }
        catch (Exception e)
        {
            // LIVE-10: a readable error, and the shown mode is left as it was.
            return e.Message;
        }
    }

    // --- sleep and wake (BG-12 / DESK-21) ------------------------------------

    /// <summary>
    /// The PC is about to sleep, and nothing we can do will stop it — this is the one thing a phone can
    /// survive and a PC cannot. Record when, so the outage can be reported honestly.
    /// </summary>
    public void SystemWillSleep()
    {
        if (!Running)
        {
            return;
        }

        _sleptAtMs = Clock.WallClockMs();
        Log.Warn("app", "the PC is going to sleep — monitoring stops until it wakes");
    }

    /// <summary>
    /// DESK-21. Three things must happen, and the middle one is the one that would otherwise be a silent
    /// lie:
    ///
    ///  1. the outage is reported, with its duration — never a quiet reconnect;
    ///  2. the feed is marked dead. The monotonic clock does NOT advance while a PC sleeps, so the last
    ///     audio frame still looks like it arrived moments ago. Left alone, the watchdog would conclude
    ///     the feed had been alive all night. It was not.
    ///  3. the connection is dropped so the normal reconnect path runs (LIVE-5).
    /// </summary>
    public void SystemDidWake()
    {
        var sleptAt = _sleptAtMs;
        _sleptAtMs = 0;
        if (sleptAt == 0 || !Running)
        {
            return;
        }

        var outageMs = Clock.WallClockMs() - sleptAt;
        var minutes = Math.Max(1, outageMs / 60_000);
        SleepOutage = "The PC slept, so the monitor was down for about " + (minutes < 60
            ? $"{minutes} minute{(minutes == 1 ? string.Empty : "s")}."
            : $"{minutes / 60}h {minutes % 60}m.");
        Log.Warn("app", $"the PC woke after {outageMs}ms asleep — monitoring was down for that whole time");

        MonitorHub.LastAudioAtMs = 0; // the feed is NOT live, whatever the monotonic clock thinks
        _engine?.Start(); // the sockets died with the machine; reconnect
        Emit();
    }

    public void DismissSleepOutage()
    {
        SleepOutage = null;
        Emit();
    }

    // --- video ---------------------------------------------------------------

    /// <summary>Called by the window when it builds (or drops) the picture's decoder.</summary>
    public void OnVideoRendererChanged(MediaFoundationVideoRenderer? renderer)
    {
        VideoSink.Renderer = renderer;
        if (renderer == null)
        {
            return;
        }

        renderer.DecoderFailed += _ => Post(() =>
        {
            // DESK-22: say so, in the window, and keep monitoring.
            VideoUnavailable = true;
            Emit();
        });
    }

    private void OnVideoSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        Post(() => VideoAspect = (double)width / height);
    }

    // --- plumbing ------------------------------------------------------------

    private void OnMonitorChanged()
    {
        Post(() =>
        {
            // BG-12: hold the machine awake for exactly as long as monitoring runs, and say so if
            // Windows refuses rather than appearing to monitor.
            SleepUnprotected = Running && !PowerRequests.HoldSystem(true);
            if (!Running)
            {
                PowerRequests.HoldSystem(false);
            }

            if (Running && MonitorHub.Status.Value == Statuses.Connecting)
            {
                VideoUnavailable = false; // a new connection deserves a fresh verdict on the picture
            }

            Emit();
        });
    }

    private async Task<SignInStep> FinishSignInAsync(Func<Task<LoginResult>> step)
    {
        LoginResult result;
        try
        {
            result = await step().ConfigureAwait(true);
        }
        catch (Exception e)
        {
            // AUTH-9: a failed sign-in reads as words, never as the gateway's raw JSON.
            Log.Warn("ui", $"sign-in failed: {e.Message}");
            return new SignInStep("error", Message: e.Message);
        }

        _pendingLogin = result;
        switch (result)
        {
            case LoginResult.Ok ok:
                _store.SaveSession(ok.Session);
                _pendingLogin = null;
                RefreshRouting();
                Emit();
                return new SignInStep("ok");

            case LoginResult.Captcha captcha:
                return new SignInStep("captcha", CaptchaImage: captcha.Image);

            case LoginResult.TwoFactor twoFactor:
                return new SignInStep(
                    "code",
                    Channel: twoFactor.Channel,
                    MaskedTarget: twoFactor.MaskedTarget);

            default:
                return new SignInStep("error", Message: "Sign-in failed.");
        }
    }

    /// <summary>
    /// APP-1's inputs, cached. Reading them from the store on every UI update would mean a DPAPI
    /// round-trip twenty times a second — the level meter alone ticks that often.
    /// </summary>
    private void RefreshRouting()
    {
        _hasSession = _store.LoadSession() != null;
        _hasDevice = _store.LoadDevice() != null;
    }

    private void Post(Action action)
    {
        if (_ui.HasThreadAccess)
        {
            action();
            return;
        }

        _ui.TryEnqueue(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Log.Error("ui", $"a UI update threw: {e.Message}", e);
            }
        });
    }

    private void Emit()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty)); // everything may have moved
        StateChanged?.Invoke();
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        StateChanged?.Invoke();
    }
}

public sealed record CameraInfo(string Did, string Name, string Model, string Mac, string Ip)
{
    public string Title => Name.Length == 0 ? Did : Name;
}

/// <summary>What one step of signing in asks the window to show next.</summary>
public sealed record SignInStep(
    string Kind, // "ok" | "captcha" | "code" | "error"
    string? Message = null,
    byte[]? CaptchaImage = null,
    string? Channel = null,
    string? MaskedTarget = null);

/// <summary>
/// DESK-19/11: the shell's own preferences. They are *not* monitor settings — nothing here changes what
/// the monitor does — so they live beside the app rather than in the core's shared Settings, which the
/// phone reads too. The *rules* about them (how faint is too faint, when a fade is forbidden) are the
/// core's, and are tested there: see DesktopShell.
/// </summary>
public static class Prefs
{
    private static readonly JsonFileStore Store = new(Path.Combine(AppPaths.Root, "shell.json"));

    public static string Shape
    {
        get => Store.Get("shape") ?? DesktopShell.ShapeFull;
        set => Store.Put("shape", value);
    }

    public static bool MiniFadeEnabled
    {
        get => Store.Get("miniFade") != "false";
        set => Store.Put("miniFade", value ? "true" : "false");
    }

    public static double MiniIdleOpacity
    {
        // Clamped on the way in as well as on the way out: a value that could hide the monitor must not
        // even be storable (DESK-11).
        get => DesktopShell.ClampMiniOpacity(
            double.TryParse(Store.Get("miniOpacity"), out var v) ? v : DesktopShell.MiniOpacityDefault);
        set => Store.Put(
            "miniOpacity",
            DesktopShell.ClampMiniOpacity(value).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public static bool StartupOfferMade
    {
        get => Store.Get("startupOfferMade") == "true";
        set => Store.Put("startupOfferMade", value ? "true" : "false");
    }

    /// <summary>DESK-9: each shape remembers its own size and position, separately, across relaunches.</summary>
    public static (int X, int Y, int Width, int Height)? Frame(string shape)
    {
        var stored = Store.Get($"frame.{shape}");
        if (stored == null)
        {
            return null;
        }

        var parts = stored.Split(',');
        if (parts.Length != 4 ||
            !int.TryParse(parts[0], out var x) ||
            !int.TryParse(parts[1], out var y) ||
            !int.TryParse(parts[2], out var w) ||
            !int.TryParse(parts[3], out var h) ||
            w <= 0 ||
            h <= 0)
        {
            return null;
        }

        return (x, y, w, h);
    }

    public static void SetFrame(string shape, int x, int y, int width, int height) =>
        Store.Put($"frame.{shape}", $"{x},{y},{width},{height}");
}
