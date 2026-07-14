import AppKit
import BabyMonitorCore
import Combine
import ServiceManagement
import SwiftUI

/// MACOS-1/2/9: the app lives in the menu bar. Windows come and go; the menu bar item and the
/// monitor behind it do not. **Quit is the only thing that ends the app** — closing a window never
/// does, because closing a window must never stop a monitor (BG-5).
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    /// Built in `applicationDidFinishLaunching`, NOT at construction. Reading the stored session
    /// touches the Keychain, and macOS may want to put an access prompt on screen to allow it —
    /// which it cannot do before `NSApplication.run()` has a run loop to show it on. Doing this
    /// eagerly deadlocks the app on launch, silently, with a menu bar icon and no window.
    private var state: AppState!
    private var statusItem: NSStatusItem!
    private var cancellables = Set<AnyCancellable>()

    private var mainWindow: NSWindow?
    private var miniWindow: NSPanel?
    private var settingsWindow: NSWindow?

    private lazy var updater = Updater(currentVersion: Self.version)
    private var updateTimer: Timer?

    /// Cached, because the menu is rebuilt on every state tick (~20 Hz while live) and reading the
    /// Keychain that often is both absurd and, when the read is not pre-authorised, a machine gun
    /// of password prompts. It only changes when the user sets or removes the token.
    private var hasUpdateToken = UpdaterToken.load() != nil

    static var version: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.0.0"
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        state = AppState()
        Log.info("app", "Baby Monitor \(Self.version) starting — screen=\(state.ui.screen)")
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem.menu = NSMenu()

        state.$ui
            .receive(on: RunLoop.main)
            .sink { [weak self] ui in
                self?.refreshStatusItem(ui)
                self?.rebuildMenu(ui)
                self?.routeWindows(ui)
            }
            .store(in: &cancellables)

        state.$updateStatus
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in self?.rebuildMenu(self?.state.ui ?? BabyMonitor.shared.state()) }
            .store(in: &cancellables)

        observeSleepAndWake()
        startUpdateChecks()

        // BG-13: a Mac that restarted overnight comes back with the monitor stopped. Say so, and
        // let one click fix it — the window opens on the viewer with Resume right there.
        if state.ui.screen == "viewer" {
            showMain()
        } else {
            showMain() // sign-in / camera picker also live in the main window
        }
    }

    /// MACOS-9: the app has no Dock icon and no windows to speak of. Closing the last one is not a
    /// reason to quit — the monitor is still running.
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

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
        let mute = NSMenuItem(
            title: ui.muted ? "Muted (sound off)" : "Sound on",
            action: #selector(toggleMute),
            keyEquivalent: "m"
        )
        mute.target = self
        mute.state = ui.muted ? .on : .off
        menu.addItem(mute)

        let main = NSMenuItem(title: "Show camera", action: #selector(showMain), keyEquivalent: "1")
        main.target = self
        menu.addItem(main)

        let mini = NSMenuItem(
            title: miniWindow?.isVisible == true ? "Hide mini window" : "Show mini window",
            action: #selector(toggleMini),
            keyEquivalent: "2"
        )
        mini.target = self
        menu.addItem(mini)

        menu.addItem(.separator())

        // BG-11 / WATCH-11 / MACOS-3: which of these exist is the SHARED decision (core's
        // viewerActionKinds), so this menu and the phone's button row cannot disagree.
        if ui.canResume {
            let resume = NSMenuItem(title: "Start monitoring", action: #selector(startMonitoring), keyEquivalent: "")
            resume.target = self
            menu.addItem(resume)
        }
        if ui.canStop {
            let stop = NSMenuItem(title: "Stop monitoring…", action: #selector(stopMonitoring), keyEquivalent: "")
            stop.target = self
            menu.addItem(stop)
        }

        let settings = NSMenuItem(title: "Alerts…", action: #selector(showSettings), keyEquivalent: ",")
        settings.target = self
        menu.addItem(settings)

        let token = NSMenuItem(
            title: hasUpdateToken ? "Change update token…" : "Set up automatic updates…",
            action: #selector(setUpdateToken),
            keyEquivalent: ""
        )
        token.target = self
        menu.addItem(token)

        menu.addItem(.separator())

        let about = NSMenuItem(title: "Baby Monitor \(Self.version)", action: nil, keyEquivalent: "")
        about.isEnabled = false
        menu.addItem(about)

        let quit = NSMenuItem(title: "Quit", action: #selector(quit), keyEquivalent: "q")
        quit.target = self
        menu.addItem(quit)

        statusItem.menu = menu
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
        alert.addButton(withTitle: "Stop monitoring")
        alert.addButton(withTitle: "Cancel")
        alert.alertStyle = .warning
        NSApp.activate(ignoringOtherApps: true)
        if alert.runModal() == .alertFirstButtonReturn {
            state.stop()
            installStagedUpdateIfIdle()
        }
    }

    @objc private func dismissOutage() { state.dismissSleepOutage() }

    /// UPD-5: only reachable while monitoring is stopped — the menu item is disabled otherwise.
    @objc private func installUpdate() { installStagedUpdateIfIdle() }

    /// The repository is private, so the updater needs a credential to read it — exactly as
    /// Obtainium does on the phone. A fine-grained token with read-only Contents access, kept in
    /// the Keychain and never in the binary or the repo.
    @objc private func setUpdateToken() {
        let alert = NSAlert()
        alert.messageText = "Automatic updates"
        alert.informativeText = """
        Baby Monitor updates itself from the private repository, and needs a GitHub token to read it.

        Create a fine-grained personal access token with read-only access to Contents on
        bluzi/c100-baby-monitor-app, and paste it below. It is stored in your Keychain.

        Updates are downloaded and verified in the background, and only ever installed while \
        monitoring is stopped — the app will never restart itself while it is watching the baby.
        """
        let field = NSSecureTextField(frame: NSRect(x: 0, y: 0, width: 320, height: 24))
        field.placeholderString = "github_pat_…"
        alert.accessoryView = field
        alert.addButton(withTitle: "Save")
        alert.addButton(withTitle: "Cancel")
        if hasUpdateToken { alert.addButton(withTitle: "Remove") }

        NSApp.activate(ignoringOtherApps: true)
        switch alert.runModal() {
        case .alertFirstButtonReturn:
            let token = field.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !token.isEmpty else { return }
            UpdaterToken.save(token)
            hasUpdateToken = true
            Log.info("update", "update token stored — checking now")
            Task { await checkForUpdate() }
        case .alertThirdButtonReturn:
            UpdaterToken.clear()
            hasUpdateToken = false
            state.updateStatus = .idle
            Log.warn("update", "update token removed — the app will no longer update itself")
        default:
            break
        }
        rebuildMenu(state.ui)
    }

    @objc private func quit() { NSApp.terminate(nil) }

    // MARK: - Windows

    @objc private func showMain() {
        if mainWindow == nil {
            let window = NSWindow(
                contentRect: NSRect(x: 0, y: 0, width: 960, height: 600),
                styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
                backing: .buffered,
                defer: false
            )
            window.title = "Baby Monitor"
            window.titlebarAppearsTransparent = true
            window.isReleasedWhenClosed = false // MACOS-7: closing it is not destroying it
            window.center()
            window.setFrameAutosaveName("BabyMonitorMain")
            window.contentView = NSHostingView(rootView: RootView().environmentObject(state))
            window.appearance = NSAppearance(named: .darkAqua) // UI-1
            window.delegate = self
            mainWindow = window
        }
        present(mainWindow)
    }

    /// MACOS-12: a window you cannot Cmd-Tab to is a window you have to go hunting for, and at 3am
    /// that is the difference between glancing at the baby and giving up.
    ///
    /// A pure `.accessory` app is deliberately excluded from the app switcher and the Dock. So the
    /// app is `.accessory` only when it has nothing to switch *to*: open a window and it becomes a
    /// regular app (Cmd-Tab, Dock icon, Mission Control); close the last one and it recedes back
    /// into the menu bar. The monitor is untouched by any of it — it never lived in a window (BG-5).
    private func present(_ window: NSWindow?) {
        guard let window else { return }
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
        window.orderFrontRegardless()
    }

    /// The last real window closed — go back to being a menu bar app. The mini panel does not
    /// count: it is a floating view, and keeping a Dock icon alive for it would defeat the point.
    func windowWillClose(_ notification: Notification) {
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            let stillOpen = [self.mainWindow, self.settingsWindow].contains { $0?.isVisible == true }
            if !stillOpen { NSApp.setActivationPolicy(.accessory) }
        }
    }

    /// MACOS-5: small, always on top, over full-screen apps and across spaces. A view, not the
    /// monitor — closing it never stops anything (BG-7m).
    @objc private func toggleMini() {
        if let miniWindow, miniWindow.isVisible {
            miniWindow.orderOut(nil)
            rebuildMenu(state.ui)
            return
        }
        if miniWindow == nil {
            let panel = NSPanel(
                contentRect: NSRect(x: 0, y: 0, width: 320, height: 200),
                styleMask: [.titled, .closable, .resizable, .nonactivatingPanel, .utilityWindow],
                backing: .buffered,
                defer: false
            )
            panel.title = "Baby Monitor"
            panel.level = .floating // above ordinary windows
            panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
            panel.isReleasedWhenClosed = false
            panel.hidesOnDeactivate = false
            panel.setFrameAutosaveName("BabyMonitorMini") // MACOS-5: its position is remembered
            panel.contentView = NSHostingView(rootView: MiniView().environmentObject(state))
            panel.appearance = NSAppearance(named: .darkAqua)
            if panel.frame.origin == .zero, let screen = NSScreen.main {
                let visible = screen.visibleFrame
                panel.setFrameOrigin(NSPoint(x: visible.maxX - 340, y: visible.maxY - 220))
            }
            miniWindow = panel
        }
        miniWindow?.orderFrontRegardless()
        rebuildMenu(state.ui)
    }

    @objc private func showSettings() {
        if settingsWindow == nil {
            let window = NSWindow(
                contentRect: NSRect(x: 0, y: 0, width: 460, height: 620),
                styleMask: [.titled, .closable],
                backing: .buffered,
                defer: false
            )
            window.title = "Alerts"
            window.isReleasedWhenClosed = false
            window.center()
            window.contentView = NSHostingView(rootView: SettingsView().environmentObject(state))
            window.appearance = NSAppearance(named: .darkAqua)
            window.delegate = self
            settingsWindow = window
        }
        present(settingsWindow)
    }

    /// APP-1: the main window follows the stored state — sign-in, camera picker, or the feed.
    private func routeWindows(_ ui: UiState) {
        if ui.screen != "viewer", miniWindow?.isVisible == true {
            miniWindow?.orderOut(nil) // nothing to show
        }
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
                self?.state.systemDidWake()
                // MACOS-11: the outage is surfaced, not swallowed. Put it where it will be seen.
                if self?.state.ui.sleepOutage != nil { self?.showMain() }
            }
        }
    }

    // MARK: - Updates

    private func startUpdateChecks() {
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
            // Not a failure — updates simply have not been set up yet, and the menu says so. UPD-4
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

    /// **UPD-5.** The whole reason this updater exists. It applies only with monitoring stopped.
    private func installStagedUpdateIfIdle() {
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
