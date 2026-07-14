using BabyMonitor.App.Services;
using BabyMonitor.Core.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Log = BabyMonitor.App.Services.Logging.Log;

namespace BabyMonitor.App;

public sealed partial class SettingsWindow : Window
{
    private readonly AppState _state;
    private readonly List<string> _sounds;

    /// <summary>
    /// True while the controls are being filled in, so echoing a control's own value straight back at
    /// the store does not "save" it — and, during construction, so a slider coercing its value up to
    /// its Minimum cannot save anything at all.
    /// </summary>
    private bool _loading;

    public SettingsWindow(AppState state)
    {
        // Before InitializeComponent, and this is not style. Every slider here has a Minimum above
        // zero, so applying it coerces the value up — which raises ValueChanged **while the markup is
        // still being loaded**, before any field this class needs would otherwise exist. The handler
        // would then reach through a null AppState and take the settings window down with it.
        _state = state;
        _sounds = state.AlarmSounds.Select(s => s.Id).ToList();
        _loading = true;

        InitializeComponent();

        Title = "Baby Monitor — Settings";
        ExtendsContentIntoTitleBar = false;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(640, 860));

        CrySound.ItemsSource = state.AlarmSounds.Select(s => s.Label).ToList();
        FeedSound.ItemsSource = state.AlarmSounds.Select(s => s.Label).ToList();
        VersionText.Text = $"Baby Monitor {state.Version}"; // LIVE-15 / UPD-6

        _state.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(Load);
        Load();
    }

    private Settings Settings => _state.Settings;

    private void Load()
    {
        _loading = true;
        try
        {
            var s = Settings;

            AlarmToggle.IsOn = s.AlarmEnabled;
            SensitivitySlider.Value = s.AlarmSensitivity;
            AlarmGroup.IsEnabled = s.AlarmEnabled;
            CrySound.SelectedIndex = Math.Max(0, _sounds.IndexOf(s.CryAlarmSound));
            CryVolume.Value = s.CryAlarmVolume;

            // ALRM-16/17: what the app has learned from the parent's answers, and how to undo it.
            CalibrationRow.Visibility = _state.CalibrationSteps > 0 ? Visibility.Visible : Visibility.Collapsed;
            CalibrationText.Text =
                $"Tuned {_state.CalibrationSteps} step{(_state.CalibrationSteps == 1 ? string.Empty : "s")} stricter from your answers";

            WatchdogToggle.IsOn = s.WatchdogEnabled;
            WatchdogGroup.IsEnabled = s.WatchdogEnabled;
            GraceSlider.Value = s.WatchdogGraceSeconds;
            FeedSound.SelectedIndex = Math.Max(0, _sounds.IndexOf(s.FeedAlarmSound));
            FeedVolume.Value = s.FeedAlarmVolume;

            // WATCH-10: the dependency, made visible.
            WatchdogInactive.IsOpen = s.WatchdogEnabled && !s.AlarmEnabled;

            FadeToggle.IsOn = _state.MiniFadeEnabled;
            OpacitySlider.IsEnabled = _state.MiniFadeEnabled;
            OpacitySlider.Value = _state.MiniIdleOpacity;

            StartupToggle.IsOn = _state.StartWithWindows;
            StartupError.IsOpen = _state.StartupError != null;
            StartupError.Message = _state.StartupError ?? string.Empty;

            var hasToken = UpdaterToken.Load() != null;
            TokenGroup.Visibility = hasToken ? Visibility.Collapsed : Visibility.Visible;
            TokenStored.Visibility = hasToken ? Visibility.Visible : Visibility.Collapsed;
            UpdateStatusText.Text = _state.UpdateStatus.State switch
            {
                UpdateState.Checking => "Checking…",
                UpdateState.ReadyToInstall =>
                    $"{_state.UpdateStatus.Version} is ready — it installs when monitoring stops",
                UpdateState.Failing => _state.UpdateStatus.Reason ?? "Update checks are failing",
                _ => hasToken ? "Up to date" : "Not set up",
            };
        }
        finally
        {
            _loading = false;
        }
    }

    private void Save(Settings settings)
    {
        if (_loading)
        {
            return;
        }

        _state.SaveSettings(settings); // ALRM-2: effective immediately
    }

    // --- the crying alarm ------------------------------------------------------

    private void OnAlarmToggled(object sender, RoutedEventArgs e) =>
        Save(Settings with { AlarmEnabled = AlarmToggle.IsOn });

    private void OnSensitivityChanged(object sender, RangeBaseValueChangedEventArgs e) =>
        Save(Settings with { AlarmSensitivity = (int)Math.Round(e.NewValue) });

    private void OnCrySoundChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CrySound.SelectedIndex >= 0)
        {
            Save(Settings with { CryAlarmSound = _sounds[CrySound.SelectedIndex] });
        }
    }

    private void OnCryVolumeChanged(object sender, RangeBaseValueChangedEventArgs e) =>
        Save(Settings with { CryAlarmVolume = e.NewValue });

    private void OnPreviewCry(object sender, RoutedEventArgs e) =>
        _state.PreviewAlarm(Settings.CryAlarmSound, Settings.CryAlarmVolume);

    private void OnResetCalibration(object sender, RoutedEventArgs e)
    {
        _state.ResetCalibration();
        Load();
    }

    // --- the watchdog ---------------------------------------------------------

    private void OnWatchdogToggled(object sender, RoutedEventArgs e) =>
        Save(Settings with { WatchdogEnabled = WatchdogToggle.IsOn });

    private void OnGraceChanged(object sender, RangeBaseValueChangedEventArgs e) =>
        Save(Settings with { WatchdogGraceSeconds = (int)Math.Round(e.NewValue) });

    private void OnFeedSoundChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FeedSound.SelectedIndex >= 0)
        {
            Save(Settings with { FeedAlarmSound = _sounds[FeedSound.SelectedIndex] });
        }
    }

    private void OnFeedVolumeChanged(object sender, RangeBaseValueChangedEventArgs e) =>
        Save(Settings with { FeedAlarmVolume = e.NewValue });

    private void OnPreviewFeed(object sender, RoutedEventArgs e) =>
        _state.PreviewAlarm(Settings.FeedAlarmSound, Settings.FeedAlarmVolume);

    // --- the shell's own settings ---------------------------------------------

    private void OnFadeToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _state.MiniFadeEnabled = FadeToggle.IsOn;
        OpacitySlider.IsEnabled = FadeToggle.IsOn;
    }

    private void OnOpacityChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_loading)
        {
            _state.MiniIdleOpacity = e.NewValue;
        }
    }

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            _state.SetStartWithWindows(StartupToggle.IsOn);
        }
    }

    // --- updates ---------------------------------------------------------------

    private void OnSaveToken(object sender, RoutedEventArgs e)
    {
        var token = TokenBox.Password.Trim();
        if (token.Length == 0)
        {
            return;
        }

        UpdaterToken.Save(token);
        TokenBox.Password = string.Empty;
        Log.Info("update", "update token stored");
        Load();
    }

    private void OnRemoveToken(object sender, RoutedEventArgs e)
    {
        UpdaterToken.Clear();
        Log.Warn("update", "update token removed — the app will no longer update itself");
        Load();
    }
}
