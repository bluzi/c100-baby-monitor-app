import AppKit
import BabyMonitorCore
import Combine
import SwiftUI

/// MACOS-1/2/9: the app lives in the menu bar. Windows come and go; the menu bar item and the
/// monitor behind it do not. **Quit is the only thing that ends the app** — closing a window never
/// does, because closing a window must never stop a monitor (BG-5).
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate, NSMenuItemValidation {
    /// Built in `applicationDidFinishLaunching`, NOT at construction. Reading the stored session
    /// touches the Keychain, and macOS may want to put an access prompt on screen to allow it —
    /// which it cannot do before `NSApplication.run()` has a run loop to show it on. Doing this
    /// eagerly deadlocks the app on launch, silently, with a menu bar icon and no window.
    private var state: AppState!
    private var statusItem: NSStatusItem!
    private var cancellables = Set<AnyCancellable>()

    private var monitor: MonitorWindowController!
    private var settingsWindow: NSWindow?

    private lazy var updater = Updater(currentVersion: Self.version)
    private var updateTimer: Timer?

    static var version: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.0.0"
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        // UPD-5, the "or at the next launch" half. This must happen BEFORE anything else: the app
        // starts monitoring almost immediately, and once it does, an update may not be applied. If
        // a previous run staged one and then found the monitor running, this is its moment — the
        // only one where installing costs nothing, because nothing is being watched yet.
        //
        // The visual harness does none of it: it must not install anything, and — the part that
        // bites — it must not touch a Keychain item, because a throwaway build is not the binary
        // that wrote one, and macOS answers that with a password prompt.
        if !Preview.active, installStagedFromEarlierRun() { return } // relaunching; do not set up

        state = AppState()
        Log.info("app", "Baby Monitor \(Self.version) starting — screen=\(state.ui.screen)")

        MainMenu.install() // MACOS-13: and with it, ⌘V

        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem.menu = NSMenu()

        monitor = MonitorWindowController(state: state)
        monitor.onVisibilityChanged = { [weak self] in self?.updateActivationPolicy() }

        state.$ui
            .receive(on: RunLoop.main)
            .sink { [weak self] ui in
                self?.refreshStatusItem(ui)
                self?.rebuildMenu(ui)
            }
            .store(in: &cancellables)

        state.$updateStatus
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in self?.rebuildMenu(self?.state.ui ?? BabyMonitor.shared.state()) }
            .store(in: &cancellables)

        observeSleepAndWake()
        startUpdateChecks()

        // BG-13: a Mac that restarted overnight comes back with the monitor stopped. Say so, and
        // let one click fix it — the window opens on the viewer with Start right there. Sign-in and
        // the camera picker live in the same window (MACOS-14).
        monitor.show()

        if Preview.active { logMenus() }
        if let path = Preview.snapshotPath { takePreviewSnapshot(to: path) }
    }

    /// Says out loud what the menu bar actually contains — because MACOS-13's whole failure mode was
    /// a menu that silently was not there, and "the code looks right" is what let that ship.
    private func logMenus() {
        NSApp.mainMenu?.update() // what the menu bar would show if it were pulled down right now
        for menu in NSApp.mainMenu?.items.compactMap(\.submenu) ?? [] {
            menu.update()
            // Alternates (Close/Close All and the like) are in the list but never on screen at the
            // same time as the item they replace — so they are marked, not miscounted as duplicates.
            let items = menu.items
                .filter { !$0.isSeparatorItem && !$0.isAlternate && !$0.isHidden }
                .map { $0.keyEquivalent.isEmpty ? $0.title : "\($0.title) [⌘\($0.keyEquivalent)]" }
            Log.info("ui", "menu \(menu.title): \(items.joined(separator: " · "))")
        }
    }

    /// The visual harness (see `Preview`): draw the window, photograph it, quit. Never reached in a
    /// real run — `BM_UI_SNAPSHOT` is not set in one.
    private func takePreviewSnapshot(to path: String) {
        let wantsSettings = ProcessInfo.processInfo.environment["BM_UI_PREVIEW"] == "settings"
        if wantsSettings { showSettings(nil) }

        // BM_UI_TOGGLE: change shape and change back before the picture is taken. The log then says
        // whether the video surface survived it (MACOS-14) — the one promise of the one-window
        // design that a still picture cannot show.
        if ProcessInfo.processInfo.environment["BM_UI_TOGGLE"] != nil {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) { self.state.toggleShape() }
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.9) { self.state.toggleShape() }
        }

        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { [weak self] in
            guard let self, let window = wantsSettings ? self.settingsWindow : self.monitor.nsWindow else {
                return NSApp.terminate(nil)
            }
            Preview.snapshot(window, to: path) { NSApp.terminate(nil) }
        }
    }

    /// MACOS-9: closing the last window is not a reason to quit — the monitor is still running.
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    /// Clicking the Dock icon (which only exists while a window is open) brings the monitor back.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows: Bool) -> Bool {
        if !hasVisibleWindows { monitor.show() }
        return true
    }

    /// MACOS-12: an app with no windows recedes into the menu bar and stops cluttering the switcher;
    /// an app with one is reachable by Cmd-Tab and Mission Control like any other.
    private func updateActivationPolicy() {
        let hasWindow = monitor.isVisible || settingsWindow?.isVisible == true
        NSApp.setActivationPolicy(hasWindow ? .regular : .accessory)
        rebuildMenu(state.ui)
    }

    // MARK: - Menu bar (MACOS-1, MACOS-2)

    private func refreshStatusItem(_ ui: UiState) {
        guard let button = statusItem.button else { return }
        let (symbol, description, tint): (String, String, NSColor?) = {
            if ui.activeAlarm != nil {
                // MACOS-1: a ringing alarm is unmistakable, even on a Mac with its volume down.
                return ("bell.badge.fill", "Alarm ringing", .systemRed)
            }
            if !ui.running { return ("moon.zzz", "Monitoring stopped", .secondaryLabelColor) }
            switch ui.status {
            case "live": return ("waveform", "Live", nil)
            case "session-expired", "unsupported-camera", "monitor-failed":
                return ("exclamationmark.triangle.fill", ui.statusText, .systemOrange)
            default: return ("arrow.triangle.2.circlepath", ui.statusText, .systemYellow)
            }
        }()
        // Always a template image: that is what makes macOS draw it at the menu bar's own size and
        // adapt it to a light or dark bar. A non-template image renders as a black blob at the
        // wrong size — which is exactly what it did. Colour comes from the tint, not the image.
        let image = NSImage(systemSymbolName: symbol, accessibilityDescription: description)?
            .withSymbolConfiguration(.init(pointSize: 15, weight: .regular))
        image?.isTemplate = true
        button.image = image
        button.contentTintColor = tint // nil = follow the menu bar; red only while an alarm rings
        button.toolTip = ui.statusLine
    }

    private func rebuildMenu(_ ui: UiState) {
        let menu = NSMenu()

        // MACOS-2: what is happening, in words, before anything you can click.
        let header = NSMenuItem(title: ui.statusLine, action: nil, keyEquivalent: "")
        header.isEnabled = false
        menu.addItem(header)

        if let outage = ui.sleepOutage {
            // MACOS-11: the Mac slept and the monitor was down. Never a quiet reconnect.
            let item = NSMenuItem(title: "⚠︎ \(outage)", action: #selector(dismissOutage), keyEquivalent: "")
            item.target = self
            menu.addItem(item)
        }
        if state.sleepUnprotected {
            let item = NSMenuItem(
                title: "⚠︎ The Mac may sleep and stop monitoring",
                action: nil,
                keyEquivalent: ""
            )
            item.isEnabled = false
            menu.addItem(item)
        }
        if case let .failing(reason) = state.updateStatus {
            // UPD-4: an app that has silently stopped updating looks exactly like one that is current.
            let item = NSMenuItem(title: "⚠︎ Can't check for updates: \(reason)", action: nil, keyEquivalent: "")
            item.isEnabled = false
            menu.addItem(item)
        }
        if case let .readyToInstall(version) = state.updateStatus {
            // UPD-7: it is ready and it is waiting. Not a nag, not a surprise.
            let item = NSMenuItem(
                title: ui.running
                    ? "Update \(version) ready — installs when monitoring stops"
                    : "Install update \(version)",
                action: ui.running ? nil : #selector(installUpdate),
                keyEquivalent: ""
            )
            item.target = self
            item.isEnabled = !ui.running
            menu.addItem(item)
        }

        menu.addItem(.separator())

        if ui.activeAlarm != nil {
            let ack = NSMenuItem(title: "Acknowledge alarm", action: #selector(acknowledge), keyEquivalent: "")
            ack.target = self
            menu.addItem(ack)
            menu.addItem(.separator())
        }

        // MACOS-4 / LIVE-2: says which state it is IN, never what clicking would do.
        let mute = NSMenuItem(title: ui.muted ? "Muted (sound off)" : "Sound on", action: #selector(toggleMute), keyEquivalent: "")
        mute.target = self
        mute.state = ui.muted ? .on : .off
        menu.addItem(mute)

        // MACOS-2/14: one window, two shapes — so one item shows it and one changes its shape.
        let show = NSMenuItem(title: "Show Camera", action: #selector(showMonitorWindow(_:)), keyEquivalent: "")
        show.target = self
        menu.addItem(show)

        let mini = NSMenuItem(title: "Mini Window", action: #selector(toggleShape(_:)), keyEquivalent: "")
        mini.target = self
        mini.state = state.shape == .mini ? .on : .off
        menu.addItem(mini)

        menu.addItem(.separator())

        // BG-11 / WATCH-11 / MACOS-3: which of these exist is the SHARED decision (core's
        // viewerActionKinds), so this menu and the phone's button row cannot disagree.
        if ui.canResume {
            let resume = NSMenuItem(title: "Start Monitoring", action: #selector(startMonitoring), keyEquivalent: "")
            resume.target = self
            menu.addItem(resume)
        }
        if ui.canStop {
            let stop = NSMenuItem(title: "Stop Monitoring…", action: #selector(stopMonitoring), keyEquivalent: "")
            stop.target = self
            menu.addItem(stop)
        }

        let settings = NSMenuItem(title: "Settings…", action: #selector(showSettings(_:)), keyEquivalent: "")
        settings.target = self
        menu.addItem(settings)

        menu.addItem(.separator())

        let about = NSMenuItem(title: "About Baby Monitor", action: #selector(showAbout(_:)), keyEquivalent: "")
        about.target = self
        menu.addItem(about)

        let quit = NSMenuItem(title: "Quit", action: #selector(quit), keyEquivalent: "q")
        quit.target = self
        menu.addItem(quit)

        statusItem.menu = menu
    }

    /// The menu bar menu's items carry their own state; these are the main menu's (MACOS-14).
    func validateMenuItem(_ menuItem: NSMenuItem) -> Bool {
        switch menuItem.action {
        case #selector(toggleShape(_:)):
            menuItem.state = state.shape == .mini ? .on : .off
            return state.ui.screen == "viewer" // a sign-in form does not go in a tile
        default:
            return true
        }
    }

    // MARK: - Actions

    @objc private func toggleMute() { state.toggleMute() }

    @objc private func acknowledge() { state.acknowledge() }

    @objc private func startMonitoring() { state.start() }

    /// MACOS-3 / BG-11: a stray click in a menu can no more end monitoring than a stray tap can.
    @objc private func stopMonitoring() {
        let alert = NSAlert()
        alert.messageText = "Stop monitoring?"
        alert.informativeText = "Audio, the alarm and the connection all stop. The baby will not be monitored."
        alert.addButton(withTitle: "Stop Monitoring")
        alert.addButton(withTitle: "Cancel")
        alert.alertStyle = .warning
        NSApp.activate(ignoringOtherApps: true)
        if alert.runModal() == .alertFirstButtonReturn {
            state.stop()
            installStagedUpdateIfIdle()
        }
    }

    @objc private func dismissOutage() { state.dismissSleepOutage() }

    @objc func showMonitorWindow(_ sender: Any?) { monitor.show() }

    /// MACOS-7: the window closes; the monitor does not notice.
    @objc func hideMonitorWindow(_ sender: Any?) { monitor.hide() }

    /// MACOS-14: the same window, worn small — or worn full again.
    @objc func toggleShape(_ sender: Any?) {
        if !monitor.isVisible { monitor.show() }
        state.toggleShape()
    }

    @objc func showAbout(_ sender: Any?) {
        NSApp.activate(ignoringOtherApps: true)
        NSApp.orderFrontStandardAboutPanel(options: [
            .applicationName: "Baby Monitor",
            .credits: NSAttributedString(
                string: "Turns a Xiaomi camera into a baby monitor.\nIt keeps watching when the window is closed — only Quit stops it.",
                attributes: [
                    .font: NSFont.systemFont(ofSize: 11),
                    .foregroundColor: NSColor.secondaryLabelColor,
                ]
            ),
        ])
    }

    @objc func checkForUpdatesNow(_ sender: Any?) {
        Task { await checkForUpdate() }
    }

    /// UPD-5: only ever with monitoring stopped — the menu item is disabled otherwise.
    @objc private func installUpdate() { installStagedUpdateIfIdle() }

    @objc private func quit() { NSApp.terminate(nil) }

    // MARK: - Settings

    @objc func showSettings(_ sender: Any?) {
        if settingsWindow == nil {
            let window = NSWindow(
                contentRect: NSRect(x: 0, y: 0, width: 480, height: 640),
                styleMask: [.titled, .closable, .resizable],
                backing: .buffered,
                defer: false
            )
            window.title = "Settings"
            window.isReleasedWhenClosed = false
            window.center()
            window.setFrameAutosaveName("BabyMonitorSettings")
            window.minSize = NSSize(width: 480, height: 420)
            window.contentView = NSHostingView(rootView: SettingsView().environmentObject(state))
            window.appearance = NSAppearance(named: .darkAqua)
            window.delegate = self
            settingsWindow = window
        }
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
        settingsWindow?.makeKeyAndOrderFront(nil)
        updateActivationPolicy()
    }

    /// The settings window closed. If it was the last one, the app goes back to being a menu bar
    /// item (MACOS-12) — and the monitor, as ever, does not notice (BG-5).
    func windowWillClose(_ notification: Notification) {
        DispatchQueue.main.async { [weak self] in self?.updateActivationPolicy() }
    }

    // MARK: - Sleep and wake (BG-12, MACOS-11)

    private func observeSleepAndWake() {
        let center = NSWorkspace.shared.notificationCenter
        center.addObserver(
            forName: NSWorkspace.willSleepNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.state.systemWillSleep() }
        }
        center.addObserver(
            forName: NSWorkspace.didWakeNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.state.systemDidWake()
                // MACOS-11: the outage is surfaced, not swallowed. Put it where it will be seen.
                if self.state.ui.sleepOutage != nil { self.monitor.show() }
            }
        }
    }

    // MARK: - Updates

    private func startUpdateChecks() {
        // The visual harness must never reach for a Keychain item. Checking for an update reads the
        // updater's GitHub token, and a throwaway build is not the binary that wrote it — so macOS
        // stops and asks the person at the Mac for their login password. Looking at a window is not
        // worth a password prompt, let alone one per launch.
        guard !Preview.active else { return }

        Task { await self.checkForUpdate() }
        // Every 6 hours. This is a monitor, not a package manager — checking harder buys nothing.
        updateTimer = Timer.scheduledTimer(withTimeInterval: 6 * 3600, repeats: true) { [weak self] _ in
            Task { await self?.checkForUpdate() }
        }
    }

    private func checkForUpdate() async {
        state.updateStatus = .checking
        do {
            if let version = try await updater.check() {
                state.updateStatus = .readyToInstall(version: version)
                installStagedUpdateIfIdle() // UPD-5: only ever when nothing is being monitored
            } else {
                state.updateStatus = .idle
            }
        } catch Updater.UpdaterError.noToken {
            // Not a failure — updates simply have not been set up yet, and settings say so. UPD-4
            // is about an updater that HAS been set up and has quietly stopped working; an updater
            // that cries wolf on a fresh install is one nobody reads by the time it matters.
            state.updateStatus = .idle
        } catch {
            // UPD-8: a failed check never touches monitoring. UPD-4: but it is not swallowed either.
            let failing = await updater.isFailingPersistently
            Log.warn("update", "check failed: \(error.localizedDescription)")
            state.updateStatus = failing ? .failing(reason: error.localizedDescription) : .idle
        }
    }

    /// UPD-5: apply an update a previous run staged but could not install because the monitor was
    /// running. Returns true when the app is about to relaunch into the new version.
    ///
    /// Synchronous, and first: everything after it in `applicationDidFinishLaunching` would build
    /// an app that is about to be replaced, and starting a monitor we are seconds from tearing down
    /// would be a camera connection made and dropped for nothing.
    private func installStagedFromEarlierRun() -> Bool {
        guard let staged = StagedUpdate.find(newerThan: Self.version) else { return false }
        Log.warn("update", "a staged update (\(staged.version)) is waiting — installing it now, before monitoring starts")
        do {
            try StagedUpdate.install(staged)
            return true
        } catch {
            // A failed install must never keep the monitor from coming up. Carry on with the old
            // version: an outdated monitor beats no monitor.
            Log.error("update", "could not install the staged update: \(error.localizedDescription)")
            return false
        }
    }

    /// **UPD-5.** The whole reason this updater exists. It applies only with monitoring stopped.
    func installStagedUpdateIfIdle() {
        guard !state.ui.running else {
            Log.info("update", "an update is staged but monitoring is running — it waits")
            return
        }
        Task {
            guard await updater.staged != nil else { return }
            do {
                try await updater.install()
            } catch {
                Log.error("update", "could not install the staged update: \(error.localizedDescription)")
                state.updateStatus = .failing(reason: error.localizedDescription)
            }
        }
    }
}
