import AppKit
import BabyMonitorCore
import Combine
import SwiftUI

/// DESK-1/2/6: the app lives in the menu bar. Windows come and go; the menu bar item and the
/// monitor behind it do not. **Quit is the only thing that ends the app** — closing a window never
/// does, because closing a window must never stop a monitor (BG-5).
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate, NSMenuItemValidation, NSMenuDelegate {
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

    /// True only between "the user said yes to a restart" and the process going away — see
    /// `applicationShouldTerminate`.
    private var relaunchingForUpdate = false

    /// The cameras on the account, and which one is being watched — for the menu bar's camera
    /// submenu (DESK-2). Held rather than fetched on demand: the device list is a *signed request to
    /// Xiaomi*, and this menu is rebuilt on every state tick. It is refreshed when the menu opens,
    /// which is the only moment its freshness can matter.
    private var cameras: [CameraInfo] = []
    private var selectedCameraDid: String?

    /// Everything the menu's contents depend on, as one string. The state ticks about twenty times a
    /// second while live; rebuilding a menu at that rate is absurd, and it would also fight the user
    /// by rebuilding the menu they are currently reading.
    private var menuSignature = ""

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

        MainMenu.install() // DESK-16: and with it, ⌘V

        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem.menu = NSMenu()
        refreshCameraList() // so the camera submenu is populated the first time it is opened

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
        // the camera picker live in the same window (DESK-9).
        monitor.show()

        if Preview.active { logMenus() }
        if let path = Preview.snapshotPath { takePreviewSnapshot(to: path) }
    }

    /// Says out loud what the menu bar actually contains — because DESK-16's whole failure mode was
    /// a menu that silently was not there, and "the code looks right" is what let that ship.
    private func logMenus() {
        // The menu bar's own menu, which is the one that changes with what the app can do (DESK-2).
        if let tray = statusItem.menu {
            let items = tray.items.filter { !$0.isSeparatorItem }.map { item -> String in
                let mark = item.state == .on ? "✓" : ""
                let sub = item.submenu.map { s in
                    " ▸ [" + s.items.filter { !$0.isSeparatorItem }
                        .map { ($0.state == .on ? "✓" : "") + $0.title }
                        .joined(separator: " · ") + "]"
                } ?? ""
                return mark + item.title + sub
            }
            Log.info("ui", "tray (\(state.ui.screen)): \(items.joined(separator: " · "))")
        }

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
        // An alert is a window like any other, so it can be put on screen and photographed without
        // being run modally — which is the only way to look at these without a real camera, a real
        // Mi account and a real published release.
        if let kind = Preview.alert {
            let alert: NSAlert
            switch kind {
            case "quit": alert = Alerts.quit()
            case "update": alert = Alerts.updateInstalled(version: "0.1.31", monitoring: true)
            case "update-idle": alert = Alerts.updateInstalled(version: "0.1.31", monitoring: false)
            case "uptodate": alert = Alerts.plain(title: "Baby Monitor is up to date.", body: "You are running \(Self.version).")
            default: alert = Alerts.plain(
                title: "Could not check for updates.",
                body: "GitHub could not be reached."
            )
            }
            alert.layout()
            let window = alert.window
            window.center()
            window.orderFrontRegardless()
            NSApp.activate(ignoringOtherApps: true)
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.8) {
                Preview.snapshot(window, to: path) { NSApp.terminate(nil) }
            }
            return
        }

        let wantsSettings = ProcessInfo.processInfo.environment["BM_UI_PREVIEW"] == "settings"
        if wantsSettings { showSettings(nil) }

        // BM_UI_TOGGLE: change shape and change back before the picture is taken. The log then says
        // whether the video surface survived it (DESK-9) — the one promise of the one-window
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

    /// DESK-6: closing the last window is not a reason to quit — the monitor is still running.
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    /// Clicking the Dock icon (which only exists while a window is open) brings the monitor back.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows: Bool) -> Bool {
        if !hasVisibleWindows { monitor.show() }
        return true
    }

    /// DESK-14: an app with no windows recedes into the menu bar and stops cluttering the switcher;
    /// an app with one is reachable by Cmd-Tab and Mission Control like any other.
    private func updateActivationPolicy() {
        let hasWindow = monitor.isVisible || settingsWindow?.isVisible == true
        NSApp.setActivationPolicy(hasWindow ? .regular : .accessory)
        rebuildMenu(state.ui)
    }

    // MARK: - Menu bar (DESK-1, DESK-2)

    /// DESK-1. **While the monitor is doing its job, this is just the app's mark: a waveform, in
    /// the menu bar's own colour.** No moon, no spinner, no grey — a menu bar item that keeps
    /// changing its face is one a parent learns to stop reading.
    ///
    /// It changes for exactly two things, and both mean *go and look*: an alarm is ringing, or the
    /// monitor has stopped working. A menu bar that looks the same whether the feed is live or dead
    /// would be the failure this whole project is built against, so those two are loud — and
    /// everything else is quiet.
    private func refreshStatusItem(_ ui: UiState) {
        guard let button = statusItem.button else { return }
        let (symbol, description, tint): (String, String, NSColor?) = {
            if ui.activeAlarm != nil {
                // ALRM-4: unmistakable, even on a Mac with its volume down.
                return ("bell.badge.fill", "Alarm ringing", .systemRed)
            }
            switch ui.status {
            case "session-expired", "unsupported-camera", "monitor-failed":
                return ("exclamationmark.triangle.fill", ui.statusText, .systemOrange)
            default:
                // Live, connecting, reconnecting: the monitor is working, or is working on it.
                // Nothing here is worth a parent's attention, so nothing here asks for it.
                return ("waveform", ui.statusText, nil)
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

    /// **DESK-2: the menu offers what the app can actually do right now, and nothing else.**
    ///
    /// There are three of them, because there are three genuinely different situations — and putting
    /// Mute and Show Camera in front of someone who has not signed in is the app describing a monitor
    /// that does not exist:
    ///
    ///  - **not signed in**: sign in. Nothing else is true yet.
    ///  - **signed in, no camera chosen**: choose one, or sign out.
    ///  - **watching**: everything, plus the account's cameras as a submenu with the watched one
    ///    checked, so a parent with two children can look at the other room from the menu bar
    ///    instead of walking back through the picker.
    private func rebuildMenu(_ ui: UiState) {
        let signature = [
            ui.screen, ui.statusLine, String(ui.running), String(ui.muted), ui.activeAlarm ?? "",
            String(ui.canResume), ui.sleepOutage ?? "", String(state.sleepUnprotected),
            String(describing: state.updateStatus), state.shape.rawValue,
            cameras.map(\.did).joined(separator: ","), selectedCameraDid ?? "",
        ].joined(separator: "|")
        guard signature != menuSignature else { return }
        menuSignature = signature

        let menu = NSMenu()
        switch ui.screen {
        case "login": buildSignedOutMenu(menu)
        case "devices": buildNoCameraMenu(menu)
        default: buildMonitorMenu(menu, ui)
        }
        appendAppItems(to: menu)
        menu.delegate = self // menuWillOpen — see refreshCameraList()
        statusItem.menu = menu
    }

    /// AUTH-1: nothing is being monitored and nothing can be. The menu says so, and offers the one
    /// thing that changes it.
    private func buildSignedOutMenu(_ menu: NSMenu) {
        let header = NSMenuItem(title: "Not signed in", action: nil, keyEquivalent: "")
        header.isEnabled = false
        menu.addItem(header)
        menu.addItem(.separator())

        let signIn = NSMenuItem(title: "Sign In…", action: #selector(showMonitorWindow(_:)), keyEquivalent: "")
        signIn.target = self
        menu.addItem(signIn)
        menu.addItem(.separator())
    }

    /// CAM-1: signed in, but no camera chosen — so there is still nothing to mute, and nothing to show.
    private func buildNoCameraMenu(_ menu: NSMenu) {
        let header = NSMenuItem(title: "No camera chosen", action: nil, keyEquivalent: "")
        header.isEnabled = false
        menu.addItem(header)
        menu.addItem(.separator())

        let choose = NSMenuItem(title: "Choose Camera…", action: #selector(showMonitorWindow(_:)), keyEquivalent: "")
        choose.target = self
        menu.addItem(choose)

        let signOut = NSMenuItem(title: "Sign Out", action: #selector(signOut), keyEquivalent: "")
        signOut.target = self
        menu.addItem(signOut)
        menu.addItem(.separator())
    }

    private func buildMonitorMenu(_ menu: NSMenu, _ ui: UiState) {
        // DESK-2: what is happening, in words, before anything you can click.
        let header = NSMenuItem(title: ui.statusLine, action: nil, keyEquivalent: "")
        header.isEnabled = false
        menu.addItem(header)

        if let outage = ui.sleepOutage {
            // DESK-21: the Mac slept and the monitor was down. Never a quiet reconnect.
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

        menu.addItem(.separator())

        if ui.activeAlarm != nil {
            let ack = NSMenuItem(title: "Acknowledge alarm", action: #selector(acknowledge), keyEquivalent: "")
            ack.target = self
            menu.addItem(ack)
            menu.addItem(.separator())
        }

        // DESK-4 / LIVE-2: says which state it is IN, never what clicking would do.
        let mute = NSMenuItem(title: ui.muted ? "Muted (sound off)" : "Sound on", action: #selector(toggleMute), keyEquivalent: "")
        mute.target = self
        mute.state = ui.muted ? .on : .off
        menu.addItem(mute)

        // DESK-2/9: one window, two shapes — so one item shows it and one changes its shape.
        let show = NSMenuItem(title: "Show Camera", action: #selector(showMonitorWindow(_:)), keyEquivalent: "")
        show.target = self
        menu.addItem(show)

        let mini = NSMenuItem(title: "Mini Window", action: #selector(toggleShape(_:)), keyEquivalent: "")
        mini.target = self
        mini.state = state.shape == .mini ? .on : .off
        menu.addItem(mini)

        menu.addItem(.separator())

        // BG-14 / WATCH-11: there is no Stop on a Mac — quitting is how a Mac stops. Start is here
        // only for a monitor that failed on its own, and whether it appears is core's decision
        // (MacShell.macViewerActions), tested there, so it cannot quietly drift from the window's.
        if ui.canResume {
            let resume = NSMenuItem(title: "Start Monitoring", action: #selector(startMonitoring), keyEquivalent: "")
            resume.target = self
            menu.addItem(resume)
        }

        menu.addItem(cameraMenuItem())

        // The ellipsis is Apple's, and it means one thing: "this opens something that needs more
        // from you." Settings opens a window; it does not happen on the click itself, so it says so.
        let settings = NSMenuItem(title: "Settings…", action: #selector(showSettings(_:)), keyEquivalent: "")
        settings.target = self
        menu.addItem(settings)

        let signOut = NSMenuItem(title: "Sign Out", action: #selector(signOut), keyEquivalent: "") // AUTH-10
        signOut.target = self
        menu.addItem(signOut)

        menu.addItem(.separator())
    }

    /// CAM-4 / DESK-2: **the account's cameras, with the one being watched checked.**
    ///
    /// The list is whatever was last fetched, and it is refreshed when the menu opens — never on the
    /// state tick. Asking Xiaomi for the device list twenty times a second would be a signed request
    /// per tick, and an account that gets itself rate-limited is an account that cannot reconnect.
    private func cameraMenuItem() -> NSMenuItem {
        let item = NSMenuItem(title: "Camera", action: nil, keyEquivalent: "")
        let submenu = NSMenu()

        if cameras.isEmpty {
            let loading = NSMenuItem(title: "Looking for cameras…", action: nil, keyEquivalent: "")
            loading.isEnabled = false
            submenu.addItem(loading)
        }
        for camera in cameras {
            let entry = NSMenuItem(
                title: camera.name.isEmpty ? camera.did : camera.name,
                action: #selector(pickCamera(_:)),
                keyEquivalent: ""
            )
            entry.target = self
            entry.representedObject = camera
            entry.state = camera.did == selectedCameraDid ? .on : .off
            submenu.addItem(entry)
        }

        submenu.addItem(.separator())
        let picker = NSMenuItem(title: "Choose Camera…", action: #selector(switchCamera), keyEquivalent: "")
        picker.target = self
        submenu.addItem(picker)

        item.submenu = submenu
        return item
    }

    /// What is true of the app whatever it happens to be doing.
    private func appendAppItems(to menu: NSMenu) {
        if case let .failing(reason) = state.updateStatus {
            // UPD-4: an app that has silently stopped updating looks exactly like one that is current.
            let item = NSMenuItem(title: "⚠︎ Can't check for updates: \(reason)", action: nil, keyEquivalent: "")
            item.isEnabled = false
            menu.addItem(item)
        }
        if case let .installed(version) = state.updateStatus {
            // UPD-7: it is on disk and it will run next time. Said once, quietly — the parent already
            // declined a restart, and an app that keeps asking is an app that gets ignored.
            let item = NSMenuItem(title: "\(version) installed — runs at the next launch", action: nil, keyEquivalent: "")
            item.isEnabled = false
            menu.addItem(item)
        }

        let updates = NSMenuItem(title: "Check for Updates…", action: #selector(checkForUpdatesNow(_:)), keyEquivalent: "")
        updates.target = self
        menu.addItem(updates)

        menu.addItem(.separator())

        let about = NSMenuItem(title: "About Baby Monitor", action: #selector(showAbout(_:)), keyEquivalent: "")
        about.target = self
        menu.addItem(about)

        // BG-14: quitting IS stopping, so this is the control that ends the watch — and it asks
        // first while the monitor is running (DESK-3), which `applicationShouldTerminate` handles
        // for every route out of the app at once: this item, ⌘Q, the Dock, and the More menu.
        let quit = NSMenuItem(title: "Quit Baby Monitor", action: #selector(quit), keyEquivalent: "q")
        quit.target = self
        menu.addItem(quit)
    }

    /// The menu is about to be read, which is the one moment the camera list's freshness matters.
    func menuWillOpen(_ menu: NSMenu) { refreshCameraList() }

    private func refreshCameraList() {
        selectedCameraDid = state.selectedCamera?.did
        guard state.ui.screen == "viewer", !Preview.active else { return }
        state.loadCameras { [weak self] list in
            guard let self else { return }
            self.cameras = list
            self.rebuildMenu(self.state.ui)
        }
    }

    /// The menu bar menu's items carry their own state; these are the main menu's (DESK-9).
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

    @objc private func signOut() { state.signOut() } // AUTH-10

    @objc private func switchCamera() { state.switchCamera() } // CAM-4: back to the picker

    /// One click in the menu bar, and the app is watching the other room.
    @objc private func pickCamera(_ sender: NSMenuItem) {
        guard let camera = sender.representedObject as? CameraInfo else { return }
        state.selectCamera(camera)
        refreshCameraList()
    }

    /// **BG-14 / DESK-3: quitting is how a Mac stops monitoring, so quitting asks first.**
    ///
    /// The phone protects its Stop button with a confirmation because a single stray tap must never
    /// end a watch. On a Mac that weight has moved onto Quit — and Quit is reachable by ⌘Q, by the
    /// menu bar, by the feed's own menu and by the Dock. Putting the question here, in
    /// `applicationShouldTerminate`, guards **every one of those routes at once**; putting it in the
    /// menu items would guard the ones we remembered.
    ///
    /// It asks only while the monitor is actually running. Quitting an app that is not watching
    /// anything is just quitting an app.
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        // A relaunch into a new version is not someone quitting: they were asked, and they said yes.
        // Asking a second question ("are you sure you want to stop monitoring?") on the way out of a
        // restart they just approved would be the app arguing with itself.
        guard !relaunchingForUpdate else { return .terminateNow }
        guard state != nil, state.ui.running, !Preview.active else { return .terminateNow }

        NSApp.activate(ignoringOtherApps: true)
        return Alerts.quit().runModal() == .alertFirstButtonReturn ? .terminateNow : .terminateCancel
    }

    @objc private func dismissOutage() { state.dismissSleepOutage() }

    @objc func showMonitorWindow(_ sender: Any?) { monitor.show() }

    /// DESK-13: the window closes; the monitor does not notice.
    @objc func hideMonitorWindow(_ sender: Any?) { monitor.hide() }

    /// DESK-9: the same window, worn small — or worn full again.
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

    /// UPD-9: a check asked for by hand, from the menu bar, the feed's menu or the app menu. It
    /// behaves exactly like the launch check — verify, install, ask once — and it answers, so a
    /// parent who wants to know is never left wondering whether anything happened.
    @objc func checkForUpdatesNow(_ sender: Any?) {
        Task { [weak self] in await self?.checkForUpdate(askedByUser: true) }
    }

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
    /// item (DESK-14) — and the monitor, as ever, does not notice (BG-5).
    func windowWillClose(_ notification: Notification) {
        DispatchQueue.main.async { [weak self] in self?.updateActivationPolicy() }
    }

    // MARK: - Sleep and wake (BG-12, DESK-21)

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
                // DESK-21: the outage is surfaced, not swallowed. Put it where it will be seen.
                if self.state.ui.sleepOutage != nil { self.monitor.show() }
            }
        }
    }

    // MARK: - Updates

    /// **UPD-3: the app checks for an update at launch, and never again while it runs.**
    ///
    /// The old updater checked every six hours, which meant it could find an update at 3am — and an
    /// update nobody asked for, arriving in the middle of the night, is a risk with no upside
    /// whatsoever. A monitor has nothing to gain from learning about a new version before morning.
    /// So: once, at launch, when the parent is demonstrably at the Mac, because they just opened it.
    /// After that the only checks are the ones a human asks for (UPD-9).
    private func startUpdateChecks() {
        // The visual harness must never touch the network or stage a real update — it is a throwaway
        // build posing a UI state, not a running monitor.
        guard !Preview.active else { return }
        // UPD-11: a parent can switch off the automatic launch check. A manual check (UPD-9) still
        // works — this gate is only on the check the app runs on its own.
        guard state.autoUpdatesEnabled else {
            Log.info("update", "automatic updates are off — skipping the launch check")
            return
        }
        Task { [weak self] in await self?.checkForUpdate(askedByUser: false) }
    }

    private func checkForUpdate(askedByUser: Bool) async {
        state.updateStatus = .checking
        do {
            guard let version = try await updater.check() else {
                state.updateStatus = .idle
                if askedByUser { tellUser(title: "Baby Monitor is up to date.", body: "You are running \(Self.version).") }
                return
            }
            try await installAndOfferRestart(version)
        } catch {
            // UPD-8: a failed check never touches monitoring. UPD-4: but it is not swallowed either.
            let failing = await updater.isFailingPersistently
            Log.warn("update", "check failed: \(error.localizedDescription)")
            state.updateStatus = failing ? .failing(reason: error.localizedDescription) : .idle
            if askedByUser {
                tellUser(title: "Could not check for updates.", body: error.localizedDescription)
            }
        }
    }

    /// **UPD-5.** The new version goes on disk; the running monitor is not touched and keeps
    /// watching. Then — and only then — the app asks, once, whether to restart into it.
    ///
    /// Saying no is a real answer: nothing happens, nothing is asked again, and the version already
    /// on disk takes over at the next launch. The app never restarts itself, which is the promise
    /// this updater exists to keep.
    private func installAndOfferRestart(_ version: String) async throws {
        try await updater.install()
        state.updateStatus = .installed(version: version)

        let alert = Alerts.updateInstalled(version: version, monitoring: state.ui.running)
        NSApp.activate(ignoringOtherApps: true)

        guard alert.runModal() == .alertFirstButtonReturn else {
            Log.info("update", "the user chose not to restart — \(version) will run at the next launch")
            return
        }
        relaunchingForUpdate = true
        try StagedUpdate.relaunch()
    }

    private func tellUser(title: String, body: String) {
        NSApp.activate(ignoringOtherApps: true)
        Alerts.plain(title: title, body: body).runModal()
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

}
