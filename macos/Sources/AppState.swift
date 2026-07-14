import BabyMonitorCore
import Combine
import Foundation
import IOKit.pwr_mgt
import os

/// One `os_log` subsystem, per-subsystem tags in the message — same shape as the phone's logcat,
/// so a night can be reconstructed the same way on either device.
///
/// Read it live:  log stream --predicate 'subsystem == "com.bluzi.babymonitor"'
enum Log {
    private static let logger = Logger(subsystem: "com.bluzi.babymonitor", category: "BabyMonitor")
    private static let started = Date()

    static func debug(_ tag: String, _ message: String) { emit("D", tag, message); logger.debug("[\(tag)] \(message)") }
    static func info(_ tag: String, _ message: String) { emit("I", tag, message); logger.info("[\(tag)] \(message)") }
    static func warn(_ tag: String, _ message: String) { emit("W", tag, message); logger.warning("[\(tag)] \(message)") }
    static func error(_ tag: String, _ message: String) { emit("E", tag, message); logger.error("[\(tag)] \(message)") }

    /// Also to stderr. The unified log is fine for a shipped app but hard to read back from an
    /// ad-hoc-signed binary run out of a terminal — and a log nobody can read is not a log.
    private static func emit(_ level: String, _ tag: String, _ message: String) {
        let t = String(format: "%7.2f", Date().timeIntervalSince(started))
        FileHandle.standardError.write("\(t) \(level) [\(tag)] \(message)\n".data(using: .utf8)!)
    }

    /// The sink core logs through, so the protocol layer's logs land in the same place.
    static func sink(level: String, tag: String, message: String) {
        switch level {
        case "DEBUG": debug(tag, message)
        case "WARN": warn(tag, message)
        case "ERROR": error(tag, message)
        default: info(tag, message)
        }
    }
}

/// BG-12 / MACOS-10: a sleeping Mac runs nothing at all, so while monitoring is on we ask the
/// system not to idle-sleep.
///
/// This cannot stop sleep the user *asks* for — a closed lid, or Apple menu → Sleep. Nothing an
/// app can do will. That gap is real, it is spec'd (MACOS-11), and the app reports the outage on
/// wake rather than pretending the night was covered.
final class SleepInhibitor {
    private var assertionID: IOPMAssertionID = 0
    private var held = false

    /// Returns false if the system refused — the caller must say so rather than appear to monitor.
    @discardableResult
    func hold() -> Bool {
        guard !held else { return true }
        // PreventUserIdleSystemSleep: the machine stays awake. The *display* may still sleep, which
        // is what we want at 3am — LIVE-14 keeps the display awake only while a window shows the feed.
        let result = IOPMAssertionCreateWithName(
            kIOPMAssertionTypePreventUserIdleSystemSleep as CFString,
            IOPMAssertionLevel(kIOPMAssertionLevelOn),
            "Baby Monitor is monitoring" as CFString,
            &assertionID
        )
        held = result == kIOReturnSuccess
        if held {
            Log.info("app", "sleep inhibitor held — the Mac will not idle-sleep while monitoring")
        } else {
            Log.error("app", "could not hold the sleep inhibitor (\(result)) — the Mac may sleep and stop monitoring")
        }
        return held
    }

    func release() {
        guard held else { return }
        IOPMAssertionRelease(assertionID)
        held = false
        Log.info("app", "sleep inhibitor released — the Mac may idle-sleep again")
    }

    var isHeld: Bool { held }
}

/// The shell's view of the monitor. A thin observer: it holds no decisions of its own — whether
/// monitoring can be stopped, what the status says, how loud is loud, all of it comes from core.
@MainActor
final class AppState: ObservableObject {
    @Published private(set) var ui: UiState
    @Published var updateStatus: UpdateStatus = .idle

    /// BG-12: true when monitoring is running but the Mac could still idle-sleep. The UI must say so.
    @Published private(set) var sleepUnprotected = false

    private let inhibitor = SleepInhibitor()
    private let secretBox = KeychainSecretBox()

    init() {
        let store = DefaultsStore()
        BabyMonitor.shared.install(
            keyValueStore: store,
            secretBox: secretBox,
            logSink: { level, tag, message in Log.sink(level: level, tag: tag, message: message) }
        )
        ui = BabyMonitor.shared.state()
        BabyMonitor.shared.onStateChange { [weak self] state in
            Task { @MainActor in self?.apply(state) }
        }
    }

    private func apply(_ state: UiState) {
        let wasRunning = ui.running
        ui = state
        guard state.running != wasRunning else { return }
        if state.running {
            sleepUnprotected = !inhibitor.hold()
        } else {
            inhibitor.release()
            sleepUnprotected = false
        }
    }

    // MARK: - Monitoring

    func start() { BabyMonitor.shared.start() }

    func stop() { BabyMonitor.shared.stop() }

    func acknowledge() { BabyMonitor.shared.acknowledge() }

    func toggleMute() { BabyMonitor.shared.setMuted(muted: !ui.muted) }

    func signOut() {
        BabyMonitor.shared.signOut()
        secretBox.clear()
    }

    func switchCamera() { BabyMonitor.shared.switchCamera() }

    func dismissSleepOutage() { BabyMonitor.shared.clearSleepOutage() }

    // MARK: - Sleep and wake (BG-12, MACOS-11)

    func systemWillSleep() { BabyMonitor.shared.systemWillSleep() }

    func systemDidWake() { BabyMonitor.shared.systemDidWake() }

    // MARK: - Settings

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

    func updateSetting(_ key: String, _ value: Any) {
        var current = settings
        current[key] = value
        settings = current
    }

    var alarmSounds: [AlarmSoundInfo] { BabyMonitor.shared.alarmSounds() }

    func previewAlarm(sound: String, volume: Double) {
        BabyMonitor.shared.previewAlarm(sound: sound, volume: volume)
    }
}

enum UpdateStatus: Equatable {
    case idle
    case checking
    /// UPD-7: downloaded and verified. It waits — it does not restart anything (UPD-5).
    case readyToInstall(version: String)
    /// UPD-4: repeatedly could not check. The app says so rather than going quiet.
    case failing(reason: String)
}
