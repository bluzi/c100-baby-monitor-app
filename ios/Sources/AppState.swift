import BabyMonitorCore
import Combine
import Network
import SwiftUI
import UIKit

/// The shell's view of the monitor. A thin observer: it holds no decisions of its own — whether
/// monitoring can be stopped, what the status says, how loud is loud — all of it comes from core
/// through the shared `BabyMonitor` facade, exactly as the Mac shell does. What lives here that does
/// not live there is the iOS lifecycle: the audio session that keeps the monitor alive in the
/// background (BG-9i), the network watch (LIVE-13), keeping the screen awake (LIVE-14), and the
/// honesty about a reboot (BG-10i).
@MainActor
final class AppState: ObservableObject {
    @Published private(set) var ui: UiState

    /// LIVE-13: the camera is only reachable on its own network. A phone with no path to it must say
    /// so rather than sit on "Connecting…" looking busy.
    @Published private(set) var networkDown = false

    /// IOS-5: notification permission was refused. The alarm still sounds and vibrates, but no alert
    /// appears — and the viewer says so, so a parent knows what they gave up (never a silent gap).
    @Published private(set) var notificationsDenied = false

    /// BG-10i: monitoring was running when the process last ended (a reboot, a force-quit), and iOS
    /// could neither keep it alive nor say so. The viewer reports the outage once, then resumes.
    @Published var bootOutage = false

    private let secretBox = KeychainSecretBox()
    private let audio = MonitorAudio()
    private let network = NWPathMonitor()
    private let liveActivity = LiveActivityController()
    private var lastActivityKey = ""

    init() {
        if Preview.active {
            ui = Preview.state()
            Preview.install()
            observeNetwork()
            if Preview.liveActivity { updateLiveActivity(ui) } // pose the Live Activity for a look
            return
        }
        let store = DefaultsStore()
        BabyMonitor.shared.install(
            keyValueStore: store,
            secretBox: secretBox,
            logSink: { level, tag, message in Log.sink(level: level, tag: tag, message: message) }
        )
        AlarmNotifications.shared.configure()
        // BG-10i: read this *before* anything starts monitoring, or start() would flip the flag first.
        bootOutage = BabyMonitor.shared.wasMonitoring()
        ui = BabyMonitor.shared.state()
        lastAlarm = ui.activeAlarm
        BabyMonitor.shared.onStateChange { [weak self] state in
            Task { @MainActor [weak self] in self?.apply(state) }
        }
        observeNetwork()
        observeStopFromLiveActivity() // BG-3i
        // IOS-5: the parent may grant or revoke notifications in Settings; re-check on every return so
        // the disclosure below is never stale.
        NotificationCenter.default.addObserver(
            forName: UIApplication.didBecomeActiveNotification, object: nil, queue: .main
        ) { [weak self] _ in MainActor.assumeIsolated { self?.refreshNotificationStatus() } }
    }

    /// IOS-5: asked once, in context — when the parent first reaches the live feed.
    func requestNotifications() {
        AlarmNotifications.shared.requestAuthorization { [weak self] granted in
            self?.notificationsDenied = !granted
        }
    }

    private func refreshNotificationStatus() {
        guard !Preview.active else { return }
        AlarmNotifications.shared.refreshAuthorization { [weak self] ok in self?.notificationsDenied = !ok }
    }

    private var lastAlarm: String?

    private func apply(_ state: UiState) {
        let wasRunning = ui.running
        ui = state
        // LIVE-14: keep the screen awake only while there is a live feed on screen to watch.
        UIApplication.shared.isIdleTimerDisabled = state.screen == "viewer" && state.status == "live"

        // ALRM-4 / IOS-5: an alarm starting posts the notification and (if that alarm vibrates) the
        // buzz; acknowledging clears both. The tone itself is core's — this is the phone's shell of it.
        if state.activeAlarm != lastAlarm {
            if let alarm = state.activeAlarm {
                AlarmNotifications.shared.postAlarm(kind: alarm, camera: state.cameraName)
                if alarmVibrates(alarm) { Haptics.startAlarm() }
            } else {
                AlarmNotifications.shared.clearAlarm()
                Haptics.stopAlarm()
            }
            lastAlarm = state.activeAlarm
        }

        updateLiveActivity(state)

        guard state.running != wasRunning else { return }
        // BG-9i: the audio session is the whole reason the monitor survives backgrounding. Bring it up
        // the moment monitoring starts, take it down when monitoring stops.
        if state.running {
            audio.begin { [weak self] lost in
                Task { @MainActor in
                    if lost { Log.warn("audio", "the audio session was lost and could not be reclaimed") }
                    self?.audioSessionLost = lost
                }
            }
        } else {
            audio.end()
            audioSessionLost = false // a deliberate stop clears the "interrupted" warning
        }
    }

    /// BG-9i / WATCH-11: the audio session could not be reclaimed. The monitor has effectively lost its
    /// ears; the viewer says so, and — crucially, because at night the app is backgrounded — the Live
    /// Activity is pushed to a stopped state too, never left reading "live" over a deaf monitor.
    @Published private(set) var audioSessionLost = false {
        didSet {
            guard oldValue != audioSessionLost else { return }
            updateLiveActivity(ui, force: true)
        }
    }

    // MARK: - The network (LIVE-13)

    private func observeNetwork() {
        network.pathUpdateHandler = { [weak self] path in
            let down = path.status != .satisfied
            Task { @MainActor [weak self] in
                guard let self, self.networkDown != down else { return }
                self.networkDown = down
                Log.info("app", "network is \(down ? "down — the camera cannot be reached" : "back")")
            }
        }
        network.start(queue: DispatchQueue(label: "com.bluzi.babymonitor.network"))
    }

    /// LIVE-13: the shortest route to fixing it, which on a phone is Settings.
    func openSettings() {
        guard let url = URL(string: UIApplication.openSettingsURLString) else { return }
        UIApplication.shared.open(url)
    }

    // MARK: - The Live Activity (BG-2i / BG-3i / IOS-3)

    private var activityRefresh: Timer?

    /// Throttled to meaningful changes — the camera, the feed state, muted, alarm — never the level
    /// tick, so iOS's update budget is spent only when a parent's lock screen would actually change.
    /// `force` bypasses the throttle for the periodic staleness refresh and for a lost session.
    private func updateLiveActivity(_ state: UiState, force: Bool = false) {
        if state.running {
            // BG-9i / IOS-3: a lost audio session means the monitor is deaf even while core still reads
            // "live" (the watchdog watches network frames, which keep arriving). On the lock screen —
            // the only surface while the app is backgrounded — show it stopped, never green.
            let failed = audioSessionLost
            let status = failed ? "monitor-failed" : state.status
            let statusText = failed ? "Monitoring stopped" : state.statusText
            let key = "\(state.cameraName)|\(status)|\(state.muted)|\(state.activeAlarm != nil)"
            guard force || key != lastActivityKey else { return }
            lastActivityKey = key
            liveActivity.startOrUpdate(
                camera: state.cameraName,
                state: MonitorActivityAttributes.ContentState(
                    status: status, statusText: statusText,
                    muted: state.muted, alarming: state.activeAlarm != nil
                )
            )
            startActivityRefresh()
        } else if !lastActivityKey.isEmpty {
            lastActivityKey = ""
            stopActivityRefresh()
            liveActivity.end()
        }
    }

    /// BG-10i backstop: push the activity's staleDate forward every couple of minutes while the app is
    /// alive, so a healthy monitor stays fresh — and a suspended or killed app, which can no longer
    /// refresh, lets iOS mark the card stale: the honest "this may be old" cue on the lock screen.
    private func startActivityRefresh() {
        guard activityRefresh == nil, !Preview.active else { return }
        activityRefresh = Timer.scheduledTimer(withTimeInterval: 120, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.updateLiveActivity(self.ui, force: true)
            }
        }
    }

    private func stopActivityRefresh() {
        activityRefresh?.invalidate()
        activityRefresh = nil
    }

    /// BG-3i: the Live Activity's Stop fires a Darwin notification (the widget stays decoupled from the
    /// monitor); the running app — alive because it is playing audio — receives it here and stops.
    private func observeStopFromLiveActivity() {
        let observer = Unmanaged.passUnretained(self).toOpaque()
        CFNotificationCenterAddObserver(
            CFNotificationCenterGetDarwinNotifyCenter(), observer,
            { _, observer, _, _, _ in
                guard let observer else { return }
                let appState = Unmanaged<AppState>.fromOpaque(observer).takeUnretainedValue()
                Task { @MainActor in appState.stop() }
            },
            stopMonitoringDarwinName as CFString, nil, .deliverImmediately
        )
    }

    // MARK: - Monitoring

    func start() {
        guard !Preview.active else { return }
        // Activate the session before the engine builds its AVAudioEngine, so the first audio it
        // decodes actually reaches the speaker and keeps the app alive in the background.
        audio.begin { [weak self] lost in
            Task { @MainActor in self?.audioSessionLost = lost }
        }
        BabyMonitor.shared.start()
        bootOutage = false
    }

    func stop() {
        guard !Preview.active else { return }
        BabyMonitor.shared.stop()
    }

    func acknowledge() {
        guard !Preview.active else { return }
        BabyMonitor.shared.acknowledge()
    }

    func toggleMute() {
        guard !Preview.active else { return }
        BabyMonitor.shared.setMuted(muted: !ui.muted)
    }

    func signOut() {
        guard !Preview.active else { return }
        BabyMonitor.shared.signOut()
        secretBox.clear()
        audio.end()
    }

    /// CAM-4: go back to the picker and choose again.
    func switchCamera() {
        guard !Preview.active else { return }
        BabyMonitor.shared.switchCamera()
    }

    /// Switch straight to a named camera without walking back through the picker. The engine reads the
    /// selected camera when it connects, so stop, change, start is all it takes.
    func selectCamera(_ camera: CameraInfo) {
        guard !Preview.active else { return }
        guard camera.did != BabyMonitor.shared.selectedCamera()?.did else { return }
        Log.info("ui", "switching camera to \(camera.name) did=\(camera.did)")
        BabyMonitor.shared.stop()
        BabyMonitor.shared.selectCamera(camera: camera)
        BabyMonitor.shared.start()
    }

    func loadCameras(_ done: @escaping ([CameraInfo]) -> Void) {
        BabyMonitor.shared.loadCameras { list, _ in done(list ?? []) }
    }

    func submitCryFeedback(_ wasCry: Bool) {
        guard !Preview.active else { return }
        BabyMonitor.shared.submitCryFeedback(wasCry: wasCry)
    }

    func dismissCryFeedback() {
        guard !Preview.active else { return }
        BabyMonitor.shared.dismissCryFeedback()
    }

    func dismissBootOutage() { bootOutage = false }

    // MARK: - Settings (read/written as JSON via core — no second settings model to drift)

    var settings: [String: Any] {
        get {
            let json = BabyMonitor.shared.settingsJson()
            guard let data = json.data(using: .utf8),
                  let dict = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
            else { return [:] }
            return dict
        }
        set {
            guard let data = try? JSONSerialization.data(withJSONObject: newValue),
                  let json = String(data: data, encoding: .utf8)
            else { return }
            BabyMonitor.shared.saveSettingsJson(json: json)
        }
    }

    /// ALRM-11: does the alarm that is ringing have vibrate on? Read from the same settings the tone's
    /// volume comes from, so the phone buzzes exactly when the parent asked it to.
    private func alarmVibrates(_ kind: String) -> Bool {
        let key = kind == "BABY_NOISE" ? "cryAlarmVibrate" : "feedAlarmVibrate"
        return settings[key] as? Bool ?? true
    }

    var alarmSounds: [AlarmSoundInfo] { BabyMonitor.shared.alarmSounds() }

    func previewAlarm(sound: String, volume: Double, vibrate: Bool, kind: String) {
        BabyMonitor.shared.previewAlarm(sound: sound, volume: volume)
        if vibrate { Haptics.previewAlarm() }
    }

    func resetCalibration() {
        guard !Preview.active else { return }
        BabyMonitor.shared.resetCalibration()
    }
}
