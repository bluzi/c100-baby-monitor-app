import AppKit
import BabyMonitorCore
import SwiftUI

/// LIVE-9m / MACOS-6: the full shape. Video full-bleed, two rows of chrome floating on glass above
/// it — status and level along the top, the controls along the bottom — and the rarely-used actions
/// behind a menu. Which controls exist, and when, is core's decision (BG-11, WATCH-11), so the
/// Mac's row and the phone's cannot disagree.
///
/// What fades and what does not is the whole ethic of this screen (LIVE-11m): the *controls* follow
/// the pointer, because a Mac has one; the *state* never does. Status, level, warnings and a
/// ringing alarm are on screen whatever the pointer is doing. Silence must never be mistaken for a
/// calm baby, and neither must an empty window.
struct ViewerChrome: View {
    @EnvironmentObject private var state: AppState

    var body: some View {
        VStack(spacing: 12) {
            statusBar
            Spacer(minLength: 0)
            banners
            controls
                .chromeVisible(state.chromeVisible, reduceMotion: state.reduceMotion)
                .onHover { state.pinChrome($0) }
        }
        // The top row clears the title bar: with a full-size content view the traffic lights float
        // over the video, and anything of ours underneath them would be unclickable.
        .padding(.init(top: 38, leading: 16, bottom: 16, trailing: 16))
    }

    // MARK: - Top row: what the feed is doing (LIVE-4, LIVE-6). Never fades.

    private var statusBar: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 10) {
                StatusDot(
                    status: state.ui.status,
                    running: state.ui.running,
                    alarming: state.ui.activeAlarm != nil
                )
                Text(state.ui.statusLine)
                    .font(.headline)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Spacer(minLength: 12)
                if state.ui.running {
                    Text("\(Int(state.ui.level)) dB")
                        .font(.caption.monospacedDigit())
                        .foregroundStyle(.secondary)
                        .help("How far above this room's usual loudness it is right now")
                }
            }
            LevelBar(
                level: state.ui.level,
                max: state.ui.levelMax,
                threshold: state.ui.thresholdDb,
                armed: state.ui.alarmEnabled
            )
            if state.sleepUnprotected {
                // BG-12: if the inhibitor could not be held, say so rather than appear to monitor.
                warning("The Mac may sleep and stop monitoring")
            }
            if state.networkDown {
                // LIVE-13: the camera lives on your network, and this Mac is not on one.
                HStack(spacing: 6) {
                    warning("This Mac is offline — the camera can only be reached on its own network")
                    Button("Open Network settings") { state.openNetworkSettings() }
                        .buttonStyle(.link)
                        .font(.caption)
                }
            }
        }
        .padding(12)
        .glassSurface(cornerRadius: 14)
    }

    private func warning(_ text: String) -> some View {
        Label(text, systemImage: "exclamationmark.triangle.fill")
            .font(.caption)
            .foregroundStyle(.orange)
    }

    // MARK: - Banners: things that must be read. Never fade.

    @ViewBuilder
    private var banners: some View {
        if let outage = state.ui.sleepOutage {
            // MACOS-11: the Mac slept, the monitor was down, and this is where it says so.
            Banner(symbol: "moon.zzz.fill", title: outage, tint: .orange, prominent: true) {
                BannerButton(title: "Dismiss", tint: .orange, filled: false) { state.dismissSleepOutage() }
            }
        }
        if state.ui.activeAlarm != nil {
            Banner(
                symbol: "bell.badge.fill",
                title: state.ui.activeAlarm == "BABY_NOISE" ? "The baby is crying" : "The feed is down",
                tint: .red,
                prominent: true
            ) {
                BannerButton(title: "Acknowledge", tint: .red) { state.acknowledge() }
                    .keyboardShortcut(.defaultAction)
            }
        }
        if state.ui.askingCryFeedback {
            // ALRM-15/16: one yes/no moves this camera's learned tuning a step. Dismissing learns
            // nothing, and must stay just as easy — a question that punishes you for ignoring it is
            // a question that gets wrong answers.
            Banner(symbol: "questionmark.circle.fill", title: "Was the baby crying?") {
                HStack(spacing: 8) {
                    Button("Yes") { BabyMonitor.shared.submitCryFeedback(wasCry: true) }
                        .buttonStyle(.borderedProminent)
                    Button("No, false alarm") { BabyMonitor.shared.submitCryFeedback(wasCry: false) }
                        .buttonStyle(.bordered)
                    Button("Dismiss") { BabyMonitor.shared.dismissCryFeedback() }
                        .buttonStyle(.link)
                }
            }
        }
        if state.loginOfferPending {
            // MACOS-8: asked once, plainly, and never turned on for you.
            Banner(symbol: "power", title: "Open Baby Monitor at login, so a Mac that restarts overnight comes back watching?") {
                HStack(spacing: 8) {
                    Button("Open at Login") { state.setOpenAtLogin(true) }
                        .buttonStyle(.borderedProminent)
                    Button("Not Now") { state.answerLoginOffer() }
                        .buttonStyle(.bordered)
                }
            }
        }
    }

    // MARK: - Bottom row: the controls (BG-11, LIVE-2, LIVE-10). These follow the pointer.

    /// BG-11m: **there is no Stop here, and there is not meant to be.** On a Mac the app is the
    /// monitor: it watches from the moment it opens until it is quit, so an app that is running and
    /// not watching cannot exist — and neither can the screen that would have shown it. Start
    /// survives for the one case that needs it: a monitor that failed on its own (WATCH-11).
    /// Which controls appear is core's decision, and it is tested there (`MacShell.macViewerActions`).
    private var controls: some View {
        HStack(spacing: 4) {
            if state.ui.canResume {
                ControlButton(symbol: "play.fill", label: "Start monitoring") { state.start() }
                separator
            }

            ControlButton(
                symbol: state.ui.muted ? "speaker.slash.fill" : "speaker.wave.2.fill",
                label: state.ui.muted ? "Muted — the alarm still works. Click for sound" : "Mute the speaker",
                latched: state.ui.muted // LIVE-2: muted draws latched — never just a changed glyph
            ) { state.toggleMute() }

            NightVisionControl()

            separator

            ControlButton(symbol: "pip.enter", label: "Mini window (⌘⇧M)") { state.setShape(.mini) }
            ControlButton(symbol: "slider.horizontal.3", label: "Alerts and settings (⌘,)") {
                NSApp.sendAction(#selector(AppDelegate.showSettings(_:)), to: nil, from: nil)
            }
            moreMenu
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 6)
        .glassSurface(cornerRadius: 26)
    }

    private var separator: some View {
        Divider().frame(height: 20).padding(.horizontal, 4)
    }

    private var moreMenu: some View {
        ControlMenuButton(symbol: "ellipsis", label: "More") {
            [
                .action(title: "Switch Camera…") { state.switchCamera() },
                .action(title: "Sign Out") { state.signOut() },
                .separator,
                // UPD-9: a check can always be asked for, rather than waiting for the next launch.
                .action(title: "Check for Updates…") {
                    NSApp.sendAction(#selector(AppDelegate.checkForUpdatesNow(_:)), to: nil, from: nil)
                },
                .info("Baby Monitor \(AppDelegate.version)"), // LIVE-15 / UPD-6
                .separator,
                // BG-11m: quitting is how a Mac stops monitoring, so it is offered where stopping
                // used to be — and it asks first (MACOS-3).
                .action(title: "Quit Baby Monitor", destructive: true) {
                    NSApp.terminate(nil)
                },
            ]
        }
    }
}

/// LIVE-10: the camera's three night-vision modes. The mode lives on the camera and is shared by
/// everyone viewing it, so this shows what the camera says — and a failed write leaves the shown
/// mode alone rather than lying about what the camera is doing.
struct NightVisionControl: View {
    @State private var mode: String?
    @State private var error: String?

    /// The camera's current mode, at a glance. All three are filled forms, so the moon carries the
    /// same weight as the speaker next to it.
    private var symbol: String {
        switch mode {
        case "ON": return "moon.fill"
        case "AUTO": return "moon.stars.fill"
        case "OFF": return "moon.slash.fill"
        default: return "moon.fill" // not read yet
        }
    }

    var body: some View {
        ControlMenuButton(symbol: symbol, label: "Night vision") {
            var items: [ControlMenuItem] = [
                .action(title: "Off", checked: mode == "OFF") { set("OFF") },
                .action(title: "Auto", checked: mode == "AUTO") { set("AUTO") },
                .action(title: "On", checked: mode == "ON") { set("ON") },
            ]
            if let error {
                // LIVE-10: a failed read or write is said in words, and the shown mode is left alone.
                items.append(.separator)
                items.append(.info(error))
            }
            return items
        }
        .onAppear {
            BabyMonitor.shared.nightVision { value, message in
                mode = value
                error = message
            }
        }
    }

    private func set(_ option: String) {
        BabyMonitor.shared.setNightVision(mode: option) { message in
            if let message {
                error = message // LIVE-10: readable error, and the displayed mode does not change
            } else {
                mode = option
                error = nil
            }
        }
    }
}
