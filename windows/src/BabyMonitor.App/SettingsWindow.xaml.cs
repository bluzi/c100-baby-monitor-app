using System.ComponentModel;
using System.Runtime.InteropServices;
using BabyMonitor.App.Services;
using BabyMonitor.Core.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinRT.Interop;

namespace BabyMonitor.App;

public sealed partial class SettingsWindow : Window
{
    private readonly AppState _state;
    private readonly List<string> _sounds;
    private readonly PropertyChangedEventHandler _onStateChanged;

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

        // Physical pixels, so scale by the window's DPI — 640×860 as-is is only 427×573 effective at
        // 150%, which crops the dialog on the common laptop.
        var hwnd = WindowNative.GetWindowHandle(this);
        var scale = Math.Max(1.0, GetDpiForWindow(hwnd) / 96.0);
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(640 * scale), (int)(860 * scale)));

        CrySound.ItemsSource = state.AlarmSounds.Select(s => s.Label).ToList();
        FeedSound.ItemsSource = state.AlarmSounds.Select(s => s.Label).ToList();
        VersionText.Text = $"Baby Monitor {state.Version}"; // LIVE-15 / UPD-6

        // AppState raises PropertyChanged ~20 times a second while the feed is live (the level meter).
        // The subscription MUST be dropped when this window closes — otherwise Load() keeps firing on a
        // window whose XAML tree is gone, throwing 20 times a second forever, and leaking the window.
        _onStateChanged = (_, _) => DispatcherQueue.TryEnqueue(Load);
        _state.PropertyChanged += _onStateChanged;
        Closed += (_, _) => _state.PropertyChanged -= _onStateChanged;
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

            SelectCorner(_state.MiniCorner);
            FadeToggle.IsOn = _state.MiniFadeEnabled;
            OpacitySlider.IsEnabled = _state.MiniFadeEnabled;
            OpacitySlider.Value = _state.MiniIdleOpacity;

            StartupToggle.IsOn = _state.StartWithWindows;
            StartupError.IsOpen = _state.StartupError != null;
            StartupError.Message = _state.StartupError ?? string.Empty;

            AutoUpdateToggle.IsOn = _state.AutoUpdateEnabled;
            UpdateStatusText.Text = _state.UpdateStatus.State switch
            {
                UpdateState.Checking => "Checking…",
                // UPD-6/7: a parent who declined the restart can still tell what will run next time,
                // and what is running now.
                UpdateState.Installed =>
                    $"{_state.UpdateStatus.Version} is installed — it runs at the next launch " +
                    $"(you are running {_state.Version})",
                UpdateState.Failing => _state.UpdateStatus.Reason ?? "Update checks are failing",
                // UPD-11: with the automatic check off, we did not check at launch — do not claim to be
                // up to date. The version is on the About line either way (UPD-6).
                _ => _state.AutoUpdateEnabled ? $"Up to date ({_state.Version})" : "Automatic updates are off",
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

    private void SelectCorner(string corner)
    {
        foreach (var item in CornerCombo.Items)
        {
            if (item is ComboBoxItem box && box.Tag as string == corner)
            {
                CornerCombo.SelectedItem = box;
                return;
            }
        }

        CornerCombo.SelectedIndex = 0; // an unknown stored value: bottom-right, like the core's fallback
    }

    private void OnMiniCornerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        if (CornerCombo.SelectedItem is ComboBoxItem box && box.Tag is string corner)
        {
            _state.MiniCorner = corner; // DESK-8: the window snaps the tile to it
        }
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

    private void OnAutoUpdateToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            _state.AutoUpdateEnabled = AutoUpdateToggle.IsOn;
            Load(); // reflect the change in the status line straight away
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
}
