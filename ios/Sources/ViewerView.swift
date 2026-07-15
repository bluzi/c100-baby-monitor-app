import BabyMonitorCore
import SwiftUI

/// LIVE-9 (mobile) / LIVE-4/6/10: the live feed. Video full-bleed in landscape, with two rows of
/// chrome floating on glass above it — status and level along the top, controls along the bottom —
/// and the rare actions behind a menu. Which controls exist, and when, is core's decision (BG-11,
/// WATCH-11), surfaced as `canStop`/`canResume`, so the phone's row and the Mac's cannot disagree.
///
/// What fades and what does not is the whole ethic of this screen (LIVE-11): the *controls* toggle on
/// a tap of the video; the *state* never does. Status, level, warnings and a ringing alarm are on
/// screen whatever the parent taps. Silence must never be mistaken for a calm baby, and neither must
/// an empty screen.
struct ViewerView: View {
    @EnvironmentObject private var state: AppState
    @State private var chromeVisible = true
    @State private var showStop = false
    @State private var showSettings = false

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()
            videoStage

            // LIVE-11: a tap on the bare video toggles the controls. This transparent layer sits above
            // the picture and below the chrome, so taps that miss a control land here; taps on the
            // status panel or a button are caught by them and never hide anything.
            Color.clear
                .contentShape(Rectangle())
                .onTapGesture { chromeVisible.toggle() }

            VStack(spacing: 12) {
                statusBar
                Spacer(minLength: 0)
                banners
                controls.chromeVisible(chromeVisible)
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 14)
        }
        .statusBarHidden(true)
        .persistentSystemOverlays(chromeVisible ? .automatic : .hidden)
        .onAppear {
            OrientationLock.set([.landscapeLeft, .landscapeRight]) // LIVE-9
            if !Preview.active {
                state.requestNotifications() // IOS-5, asked in context
                if !state.ui.running { state.start() } // BG-5
            }
        }
        .sheet(isPresented: $showSettings) { SettingsView() }
        .onChange(of: showSettings) { _, open in
            // IOS-1: the feed is landscape, but its settings are a form — let it go portrait while the
            // sheet is up (the viewer follows), then lock back to landscape on dismiss.
            OrientationLock.set(open ? .allButUpsideDown : [.landscapeLeft, .landscapeRight])
        }
        .confirmationDialog("Stop monitoring?", isPresented: $showStop, titleVisibility: .visible) {
            // BG-11: a single stray tap can never stop the monitor — it asks first, and says what
            // stopping costs, not "are you sure?".
            Button("Stop Monitoring", role: .destructive) { state.stop() }
            Button("Keep Monitoring", role: .cancel) {}
        } message: {
            Text("Audio, the alarm and the connection all stop. The baby will not be monitored until you start again.")
        }
    }

    private var videoStage: some View {
        ZStack {
            if Preview.active {
                Preview.backdrop
            } else {
                VideoSurface()
            }
        }
        .ignoresSafeArea()
    }

    // MARK: - Top: what the feed is doing (LIVE-4, LIVE-6). Never fades.

    private var statusBar: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 10) {
                StatusDot(status: state.ui.status, running: state.ui.running, alarming: state.ui.activeAlarm != nil)
                Text(state.ui.statusLine)
                    .font(.headline)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Spacer(minLength: 12)
                if state.ui.running {
                    Text("\(Int(state.ui.level)) dB")
                        .font(.caption.monospacedDigit())
                        .foregroundStyle(.secondary)
                }
            }
            LevelBar(
                level: state.ui.level, max: state.ui.levelMax,
                threshold: state.ui.thresholdDb, armed: state.ui.alarmEnabled
            )
            if state.audioSessionLost {
                warning("Monitoring was interrupted and could not resume — reopen to restart it")
            }
            if state.notificationsDenied {
                // IOS-5: the alarm still sounds and vibrates; the parent should know it will not show.
                Button { state.openSettings() } label: {
                    warning("Notifications are off — the alarm still sounds, but no alert will appear. Turn them on in Settings")
                }
                .buttonStyle(.plain)
            }
            if state.networkDown {
                // LIVE-13: the camera lives on your network, and this phone is not on one.
                Button {
                    state.openSettings()
                } label: {
                    warning("This phone is offline — the camera can only be reached on its own network. Open Settings")
                }
                .buttonStyle(.plain)
            }
        }
        .padding(12)
        .glassSurface(cornerRadius: 16)
    }

    private func warning(_ text: String) -> some View {
        Label(text, systemImage: "exclamationmark.triangle.fill")
            .font(.caption)
            .foregroundStyle(.orange)
            .frame(maxWidth: .infinity, alignment: .leading)
    }

    // MARK: - Banners: things that must be read. Never fade.

    @ViewBuilder
    private var banners: some View {
        if state.bootOutage {
            // BG-10i: the app came back after a reboot or force-quit; say the monitor was down.
            Banner(symbol: "arrow.clockwise.circle.fill", title: "Monitoring was down — the app had stopped. It is running again now.", tint: .orange, prominent: true) {
                BannerButton(title: "OK", tint: .orange, filled: false) { state.dismissBootOutage() }
            }
        }
        if state.ui.sessionExpired {
            Banner(symbol: "person.crop.circle.badge.exclamationmark.fill", title: "Your Mi session expired — sign in again.", tint: .orange, prominent: true) {
                BannerButton(title: "Sign In", tint: .orange) { state.signOut() }
            }
        }
        if state.ui.activeAlarm != nil {
            Banner(
                symbol: "bell.badge.fill",
                title: state.ui.activeAlarm == "BABY_NOISE" ? "The baby is crying" : "The feed is down",
                tint: .red, prominent: true
            ) {
                BannerButton(title: "Acknowledge", tint: .red) { state.acknowledge() }
            }
        }
        if state.ui.askingCryFeedback {
            // ALRM-15/16: one yes/no moves this camera's learned tuning a step. Dismissing learns
            // nothing and stays just as easy — a question that punishes being ignored gets wrong answers.
            Banner(symbol: "questionmark.circle.fill", title: "Was the baby crying?") {
                HStack(spacing: 8) {
                    BannerButton(title: "Yes", tint: .accentColor) { state.submitCryFeedback(true) }
                    BannerButton(title: "No", tint: .accentColor, filled: false) { state.submitCryFeedback(false) }
                    Button { state.dismissCryFeedback() } label: {
                        Image(systemName: "xmark").font(.footnote.weight(.bold)).foregroundStyle(.secondary).padding(6)
                    }
                    .buttonStyle(.plain)
                }
            }
        }
    }

    // MARK: - Bottom: the controls (BG-11, LIVE-2, LIVE-10). These fade on a tap.

    private var controls: some View {
        HStack(spacing: 6) {
            if state.ui.canResume {
                ControlButton(symbol: "play.fill", label: "Start monitoring") { state.start() }
            }
            if state.ui.canStop {
                ControlButton(symbol: "stop.fill", label: "Stop monitoring", tint: .red) { showStop = true }
            }
            ControlButton(
                symbol: state.ui.muted ? "speaker.slash.fill" : "speaker.wave.2.fill",
                label: state.ui.muted ? "Muted — the alarm still works. Tap for sound" : "Mute the speaker",
                latched: state.ui.muted // LIVE-2: muted draws latched — never just a changed glyph
            ) { state.toggleMute() }

            NightVisionControl()

            ControlButton(symbol: "slider.horizontal.3", label: "Alerts and settings") { showSettings = true }

            moreMenu
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 6)
        .glassSurface(cornerRadius: 32)
    }

    private var moreMenu: some View {
        ControlMenu(symbol: "ellipsis", label: "More") {
            Button { state.switchCamera() } label: { Label("Switch Camera…", systemImage: "arrow.triangle.2.circlepath") }
            Button(role: .destructive) { state.signOut() } label: { Label("Sign Out", systemImage: "rectangle.portrait.and.arrow.right") }
            Divider()
            // LIVE-15 / UPD-2i: the running version, and a plain statement that the App Store updates it.
            Section("Baby Monitor \(Self.appVersion)") {
                Text("Updates come from the App Store.")
            }
        }
    }

    static var appVersion: String {
        Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "—"
    }
}

/// LIVE-10: the camera's three night-vision modes. The mode lives on the camera and is shared by
/// everyone viewing it, so this shows what the camera says — and a failed write leaves the shown mode
/// alone rather than lying about what the camera is doing.
struct NightVisionControl: View {
    @State private var mode: String?
    @State private var error: String?

    // Base names, not the `.fill` forms: ControlGlyph applies `.symbolVariant(.fill)` itself, and that
    // falls back to the base symbol where no fill variant exists. `moon.slash` has no `.fill`, so the
    // literal "moon.slash.fill" resolved to nothing and Off showed no icon at all.
    private var symbol: String {
        switch mode {
        case "ON": return "moon"
        case "AUTO": return "moon.stars"
        case "OFF": return "moon.slash"
        default: return "moon"
        }
    }

    var body: some View {
        ControlMenu(symbol: symbol, label: "Night vision", latched: mode == "ON") {
            Picker("Night vision", selection: Binding(get: { mode ?? "" }, set: { set($0) })) {
                Text("Off").tag("OFF")
                Text("Auto").tag("AUTO")
                Text("On").tag("ON")
            }
            if let error {
                Section { Text(error) } // LIVE-10: a failed read/write is said in words
            }
        }
        .onAppear {
            guard !Preview.active else { mode = "AUTO"; return }
            BabyMonitor.shared.nightVision { value, message in mode = value; error = message }
        }
    }

    private func set(_ option: String) {
        guard option != mode else { return }
        guard !Preview.active else { mode = option; return }
        BabyMonitor.shared.setNightVision(mode: option) { message in
            if let message { error = message } else { mode = option; error = nil }
        }
    }
}
