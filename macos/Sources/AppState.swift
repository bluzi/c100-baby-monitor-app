import AppKit
import BabyMonitorCore
import Combine
import Foundation
import IOKit.pwr_mgt
import Network
import ServiceManagement
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
        // is what we want at 3am — DisplayWake keeps it on only while a window shows the feed.
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

/// LIVE-14 / MACOS-10: while a window is showing a live feed, the screen stays on — watching the
/// baby must never end in a sleeping display.
///
/// Deliberately narrower than the sleep inhibitor above: it is held only while there is *something
/// to look at*, and let go the moment the window closes or the feed stops being live. A monitor
/// nobody is looking at has no business burning a Mac's screen all night, and BG-12 keeps the
/// machine itself awake regardless.
final class DisplayWake {
    private var assertionID: IOPMAssertionID = 0
    private var held = false

    func set(_ wanted: Bool) {
        guard wanted != held else { return }
        if wanted {
            let result = IOPMAssertionCreateWithName(
                kIOPMAssertionTypePreventUserIdleDisplaySleep as CFString,
                IOPMAssertionLevel(kIOPMAssertionLevelOn),
                "Baby Monitor is showing the live feed" as CFString,
                &assertionID
            )
            held = result == kIOReturnSuccess
            if !held { Log.warn("app", "could not keep the display awake (\(result))") }
        } else {
            IOPMAssertionRelease(assertionID)
            held = false
        }
        Log.debug("app", "display-wake assertion \(held ? "held" : "released")")
    }
}

/// MACOS-5/14: the two shapes of the one monitor window.
enum WindowShape: String {
    case full
    case mini

    var other: WindowShape { self == .full ? .mini : .full }
}

/// MACOS-8/16 and LIVE-11m: the shell's own preferences. They are *not* monitor settings — nothing
/// here changes what the monitor does — so they live in UserDefaults rather than in core's shared
/// `Settings`, which the phone reads too. The *rules* about them (how faint is too faint, when a
/// fade is forbidden) are core's, and are tested there: see `MacShell`.
enum Prefs {
    private static let defaults = UserDefaults.standard

    /// The visual harness never writes to a real installation's preferences (see `Preview`).
    private static func write(_ value: Any, _ key: String) {
        guard !Preview.active else { return }
        defaults.set(value, forKey: key)
    }

    static var shape: WindowShape {
        get { WindowShape(rawValue: defaults.string(forKey: "shell.shape") ?? "") ?? .full }
        set { write(newValue.rawValue, "shell.shape") }
    }

    static var miniFadeEnabled: Bool {
        get { defaults.object(forKey: "shell.miniFade") as? Bool ?? true }
        set { write(newValue, "shell.miniFade") }
    }

    static var miniIdleOpacity: Double {
        get {
            let stored = defaults.object(forKey: "shell.miniOpacity") as? Double
                ?? MacShell.shared.MINI_OPACITY_DEFAULT
            return MacShell.shared.clampMiniOpacity(value: stored)
        }
        // Clamped on the way in as well as on the way out: a value that could hide the monitor must
        // not even be storable (MACOS-16).
        set { write(MacShell.shared.clampMiniOpacity(value: newValue), "shell.miniOpacity") }
    }

    /// MACOS-8: the offer is made once. Declining it is an answer, and an app that keeps asking is
    /// an app that gets its dialogs dismissed without being read.
    static var loginOfferMade: Bool {
        get { defaults.bool(forKey: "shell.loginOfferMade") }
        set { write(newValue, "shell.loginOfferMade") }
    }

    static func frame(_ shape: WindowShape) -> NSRect? {
        guard let string = defaults.string(forKey: "shell.frame.\(shape.rawValue)") else { return nil }
        let rect = NSRectFromString(string)
        return rect.width > 0 && rect.height > 0 ? rect : nil
    }

    static func setFrame(_ rect: NSRect, for shape: WindowShape) {
        write(NSStringFromRect(rect), "shell.frame.\(shape.rawValue)")
    }
}

/// MACOS-8: open at login. `SMAppService` is the only supported way since macOS 13 — and it is the
/// *system's* switch, so System Settings and this app can never disagree about it. We only ever
/// read it and, when the user asks, flip it. Never on our own (a monitor that installs itself into
/// a login sequence unasked is a monitor nobody trusts).
enum LoginItem {
    static var isEnabled: Bool { SMAppService.mainApp.status == .enabled }

    /// Returns a readable reason on failure — never throws into the UI, never silently no-ops.
    static func set(_ enabled: Bool) -> String? {
        do {
            if enabled {
                try SMAppService.mainApp.register()
            } else {
                try SMAppService.mainApp.unregister()
            }
            Log.info("app", "open at login → \(enabled)")
            return nil
        } catch {
            Log.warn("app", "could not change the login item: \(error.localizedDescription)")
            return error.localizedDescription
        }
    }
}

/// The shell's view of the monitor. A thin observer: it holds no decisions of its own — whether
/// monitoring can be stopped, what the status says, how loud is loud, whether the mini window may
/// fade — all of it comes from core.
@MainActor
final class AppState: ObservableObject {
    @Published private(set) var ui: UiState
    @Published var updateStatus: UpdateStatus = .idle

    /// BG-12: true when monitoring is running but the Mac could still idle-sleep. The UI must say so.
    @Published private(set) var sleepUnprotected = false

    // MARK: Shell state (MACOS-14/16, LIVE-11m)

    /// The shape the user asked for. What is actually on screen is `shape` — sign-in and the camera
    /// picker are never shown in a tile (MACOS-14), and that is core's rule, not ours.
    @Published private(set) var preferredShape: WindowShape = Preview.active ? Preview.shape : Prefs.shape
    @Published private(set) var pointerInside = false
    @Published private(set) var chromeVisible = true
    @Published private(set) var miniAlpha: Double = 1
    @Published private(set) var reduceTransparency = false
    @Published private(set) var reduceMotion = false
    @Published var openAtLogin = LoginItem.isEnabled
    @Published private(set) var loginOfferPending = false
    @Published private(set) var loginItemError: String?

    @Published var miniFadeEnabled = Prefs.miniFadeEnabled {
        didSet {
            Prefs.miniFadeEnabled = miniFadeEnabled
            recomputeMiniAlpha()
        }
    }

    @Published var miniIdleOpacity = Prefs.miniIdleOpacity {
        didSet {
            Prefs.miniIdleOpacity = miniIdleOpacity
            recomputeMiniAlpha()
        }
    }

    var shape: WindowShape {
        WindowShape(
            rawValue: MacShell.shared.windowShape(screen: ui.screen, preferred: preferredShape.rawValue)
        ) ?? .full
    }

    /// LIVE-13: the camera is only reachable on its own network. A Mac with no network at all
    /// cannot reach it, and must say so rather than sit on "Connecting…" looking busy.
    @Published private(set) var networkDown = false

    /// MACOS-19: the shape of the picture the camera is sending (width ÷ height), or 0 while there
    /// is no picture yet. The window takes this shape, which is why there are no black bars around
    /// the feed: they were never the camera's, they were the window's.
    @Published private(set) var videoAspect: CGFloat = 0

    private let inhibitor = SleepInhibitor()
    private let secretBox = KeychainSecretBox()
    private let network = NWPathMonitor()
    private var chromeHide: DispatchWorkItem?
    private var chromePinned = false

    init() {
        if Preview.active {
            // A visual harness only: an in-memory store, no Keychain, no camera, no monitor.
            ui = Preview.state()
            Preview.install()
            pointerInside = Preview.hovering
            videoAspect = Preview.aspect
            observeAccessibility()
            return
        }
        let store = DefaultsStore()
        BabyMonitor.shared.install(
            keyValueStore: store,
            secretBox: secretBox,
            logSink: { level, tag, message in Log.sink(level: level, tag: tag, message: message) }
        )
        ui = BabyMonitor.shared.state()
        BabyMonitor.shared.onStateChange { [weak self] state in
            Task { @MainActor [weak self] in self?.apply(state) }
        }
        observeAccessibility()
        observeNetwork()

        // MACOS-8: offer it once, and only when it is not already on. Never turn it on ourselves.
        loginOfferPending = !Prefs.loginOfferMade && !openAtLogin
    }

    // MARK: - The network (LIVE-13)

    private func observeNetwork() {
        // The `[weak self]` is captured again by the Task, not read out of the enclosing closure's
        // capture. Reaching into the outer one from inside a concurrently-executing body is a
        // warning at -Onone and an **error** at -O, so a build that is fine on this machine is a
        // release that does not compile at all. (It was. That is why this comment exists.)
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

    /// LIVE-13: the shortest route to fixing it, which on a Mac is the Network pane. (On the phone
    /// the same criterion opens the Wi-Fi panel — same hazard, each platform's own front door.)
    func openNetworkSettings() {
        guard let url = URL(string: "x-apple.systempreferences:com.apple.Network-Settings.extension") else { return }
        NSWorkspace.shared.open(url)
    }

    private func apply(_ state: UiState) {
        let wasRunning = ui.running
        ui = state
        recomputeMiniAlpha()
        guard state.running != wasRunning else { return }
        if state.running {
            sleepUnprotected = !inhibitor.hold()
        } else {
            inhibitor.release()
            sleepUnprotected = false
        }
    }

    // MARK: - Monitoring

    func start() {
        guard !Preview.active else { return }
        BabyMonitor.shared.start()
    }

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

    // MARK: - The window's shape (MACOS-14)

    func setShape(_ shape: WindowShape) {
        preferredShape = shape
        Prefs.shape = shape
        recomputeMiniAlpha()
    }

    func toggleShape() { setShape(shape.other) }

    // MARK: - The picture's shape (MACOS-19)

    func videoSizeChanged(_ size: CGSize) {
        guard size.width > 0, size.height > 0 else { return }
        let aspect = size.width / size.height
        guard abs(aspect - videoAspect) > 0.001 else { return }
        videoAspect = aspect
    }

    // MARK: - The pointer (LIVE-11m, MACOS-15/16)

    /// Any movement brings the controls back — no click, ever. A parent who has to click to find
    /// out what the monitor is doing is a parent who stops checking.
    func pointerMoved() {
        pointerInside = true
        chromeVisible = true
        recomputeMiniAlpha()
        scheduleChromeHide()
    }

    func pointerExited() {
        pointerInside = false
        recomputeMiniAlpha()
        scheduleChromeHide()
    }

    /// While the pointer rests *on* the controls they stay, whatever the timer thinks. Nothing may
    /// vanish from under the pointer that is reaching for it.
    func pinChrome(_ pinned: Bool) {
        chromePinned = pinned
        if pinned {
            chromeHide?.cancel()
            chromeVisible = true
        } else {
            scheduleChromeHide()
        }
    }

    private func scheduleChromeHide() {
        chromeHide?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let self, !self.chromePinned else { return }
            self.chromeVisible = false
        }
        chromeHide = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 3, execute: work)
    }

    private func recomputeMiniAlpha() {
        miniAlpha = MacShell.shared.miniOpacity(
            health: ui.health,
            hovering: pointerInside,
            fadeEnabled: miniFadeEnabled,
            reduceTransparency: reduceTransparency,
            idleOpacity: miniIdleOpacity
        )
    }

    // MARK: - Accessibility (MACOS-18)

    private func observeAccessibility() {
        readAccessibility()
        NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.accessibilityDisplayOptionsDidChangeNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.readAccessibility() }
        }
    }

    private func readAccessibility() {
        reduceTransparency = NSWorkspace.shared.accessibilityDisplayShouldReduceTransparency
        reduceMotion = NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
        recomputeMiniAlpha()
    }

    // MARK: - Open at login (MACOS-8)

    func setOpenAtLogin(_ enabled: Bool) {
        loginItemError = LoginItem.set(enabled)
        openAtLogin = LoginItem.isEnabled
        answerLoginOffer()
    }

    /// The offer has been answered, either way. It is not asked again.
    func answerLoginOffer() {
        Prefs.loginOfferMade = true
        loginOfferPending = false
    }

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
