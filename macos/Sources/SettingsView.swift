import AppKit
import BabyMonitorCore
import SwiftUI

/// Settings (ALRM, WATCH, DESK-19/11, UPD-3). Every alarm value here is read from and written back
/// to core's `Settings` as JSON — there is deliberately no second settings model on this side to
/// drift out of step with the phone's. The clamping, the defaults and the legacy migrations all
/// happen in core.
///
/// The shape is a grouped `Form`, which is what System Settings itself is: sections with a title
/// and a plain-English footer. A monitor's settings are read at 3am by someone who has been awake
/// for twenty hours, so every switch says what it *is*, and every one that could be misunderstood
/// says what happens if it is wrong.
struct SettingsView: View {
    @EnvironmentObject private var state: AppState
    @State private var settings: [String: Any] = [:]
    @State private var token = ""
    @State private var hasToken = false

    var body: some View {
        Form {
            cryingAlarm
            watchdog
            miniWindow
            startup
            updates
            theLid
        }
        .formStyle(.grouped)
        .frame(minWidth: 480, minHeight: 420)
        .preferredColorScheme(.dark)
        .onAppear {
            settings = state.settings
            // The visual harness never reads a real Keychain — a preview run must not be able to
            // put a password prompt on a parent's screen.
            hasToken = Preview.active ? false : UpdaterToken.load() != nil
        }
    }

    // MARK: - The crying alarm (ALRM-1/2/11/16)

    private var cryingAlarm: some View {
        Section {
            Toggle("Alarm when the baby cries", isOn: binding("alarmEnabled", false))

            VStack(alignment: .leading, spacing: 6) {
                LabeledContent("Sensitivity") {
                    HStack(spacing: 8) {
                        Text("Loud").font(.caption).foregroundStyle(.secondary)
                        Slider(value: intBinding("alarmSensitivity", 5), in: 1...10, step: 1)
                            .frame(width: 180)
                        Text("Quiet").font(.caption).foregroundStyle(.secondary)
                        Text("\(int("alarmSensitivity", 5))")
                            .font(.body.monospacedDigit())
                            .frame(width: 18, alignment: .trailing)
                    }
                }
                if state.ui.calibrationSteps > 0 {
                    // ALRM-16/17: the learned tuning is visible and resettable — never hidden.
                    HStack {
                        Text("Tuned \(state.ui.calibrationSteps) step\(state.ui.calibrationSteps == 1 ? "" : "s") stricter from your answers")
                            .font(.caption)
                            .foregroundStyle(.orange)
                        Button("Reset") { BabyMonitor.shared.resetCalibration() }
                            .buttonStyle(.link)
                            .font(.caption)
                    }
                }
            }
            .disabled(!bool("alarmEnabled", false))

            alarmSound(soundKey: "cryAlarmSound", volumeKey: "cryAlarmVolume")
                .disabled(!bool("alarmEnabled", false))
        } header: {
            Text("Crying alarm")
        } footer: {
            Text("Higher sensitivity means quieter crying sets it off. The alarm rings until you acknowledge it, and keeps working while the feed is muted.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    // MARK: - The feed watchdog (WATCH-1/9/10)

    private var watchdog: some View {
        Section {
            Toggle("Alarm if the feed goes down", isOn: binding("watchdogEnabled", false))

            if !bool("alarmEnabled", false), bool("watchdogEnabled", false) {
                // WATCH-9/10: the watchdog guards the crying alarm, so it is armed only while the
                // crying alarm could itself ring. Say so rather than look broken.
                Label(
                    "Inactive while the crying alarm is off — it is what guards that alarm.",
                    systemImage: "exclamationmark.triangle.fill"
                )
                .font(.caption)
                .foregroundStyle(.orange)
            }

            LabeledContent("Grace period") {
                HStack(spacing: 8) {
                    Slider(value: intBinding("watchdogGraceSeconds", 30), in: 5...120, step: 5)
                        .frame(width: 180)
                    Text("\(int("watchdogGraceSeconds", 30))s")
                        .font(.body.monospacedDigit())
                        .frame(width: 40, alignment: .trailing)
                }
            }
            .disabled(!bool("watchdogEnabled", false))

            alarmSound(soundKey: "feedAlarmSound", volumeKey: "feedAlarmVolume")
                .disabled(!bool("watchdogEnabled", false))
        } header: {
            Text("Feed watchdog")
        } footer: {
            Text("If audio stops arriving for longer than the grace period, this rings — a monitor that has quietly died must never be mistaken for a quiet baby.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    // MARK: - The mini window (DESK-11)

    private var miniWindow: some View {
        Section {
            Picker("Corner", selection: $state.miniCorner) {
                Text("Bottom right").tag(MacShell.shared.MINI_CORNER_BOTTOM_RIGHT)
                Text("Bottom left").tag(MacShell.shared.MINI_CORNER_BOTTOM_LEFT)
                Text("Top right").tag(MacShell.shared.MINI_CORNER_TOP_RIGHT)
                Text("Top left").tag(MacShell.shared.MINI_CORNER_TOP_LEFT)
            }

            Toggle("Fade it when the pointer is away", isOn: $state.miniFadeEnabled)

            LabeledContent("When faded") {
                HStack(spacing: 8) {
                    Text("Faint").font(.caption).foregroundStyle(.secondary)
                    Slider(
                        value: $state.miniIdleOpacity,
                        in: MacShell.shared.MINI_OPACITY_MIN...MacShell.shared.MINI_OPACITY_MAX
                    )
                    .frame(width: 180)
                    Text("Solid").font(.caption).foregroundStyle(.secondary)
                }
            }
            .disabled(!state.miniFadeEnabled)
        } header: {
            Text("Mini window")
        } footer: {
            Text("The mini window floats over your other work. It never fades while an alarm is ringing, while the feed is not live, or while there is anything else to tell you — only when there is genuinely nothing to see.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    // MARK: - Starting up (DESK-19)

    private var startup: some View {
        Section {
            Toggle(
                "Open Baby Monitor at login",
                isOn: Binding(get: { state.openAtLogin }, set: { state.setOpenAtLogin($0) })
            )
            if let error = state.loginItemError {
                Label(error, systemImage: "exclamationmark.triangle.fill")
                    .font(.caption)
                    .foregroundStyle(.orange)
            }
        } header: {
            Text("Startup")
        } footer: {
            Text("A Mac that restarts overnight comes back to a running monitor. Monitoring still has to be started — the app never starts watching without being asked.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    // MARK: - Updates (UPD-3/4/5)

    private var updates: some View {
        Section {
            LabeledContent("Status") {
                Text(updateStatusText)
                    .foregroundStyle(updateStatusColor)
            }

            if hasToken {
                HStack {
                    Text("GitHub token stored in your Keychain")
                        .foregroundStyle(.secondary)
                    Spacer()
                    Button("Check Now") { NSApp.sendAction(#selector(AppDelegate.checkForUpdatesNow(_:)), to: nil, from: nil) }
                    Button("Remove") {
                        UpdaterToken.clear()
                        hasToken = false
                        state.updateStatus = .idle
                        Log.warn("update", "update token removed — the app will no longer update itself")
                    }
                }
            } else {
                VStack(alignment: .leading, spacing: 8) {
                    SecureField("github_pat_…", text: $token)
                        .textFieldStyle(.roundedBorder)
                    Button("Save Token") {
                        let trimmed = token.trimmingCharacters(in: .whitespacesAndNewlines)
                        guard !trimmed.isEmpty else { return }
                        UpdaterToken.save(trimmed)
                        token = ""
                        hasToken = true
                        Log.info("update", "update token stored — checking now")
                        NSApp.sendAction(#selector(AppDelegate.checkForUpdatesNow(_:)), to: nil, from: nil)
                    }
                    .buttonStyle(.borderedProminent)
                    .disabled(token.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
            }
        } header: {
            Text("Updates")
        } footer: {
            Text("Baby Monitor updates itself from its private repository, which needs a fine-grained GitHub token with read-only access to Contents. It checks once, at launch, and never while it is running — an update arriving at 3am is a risk with no upside. What it finds is verified and installed on disk without touching the running monitor, and then it asks once whether to restart into it. It never restarts itself.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    private var updateStatusText: String {
        switch state.updateStatus {
        case .idle: return hasToken ? "Up to date (\(AppDelegate.version))" : "Not set up"
        case .checking: return "Checking…"
        case let .installed(version): return "\(version) installed — runs at the next launch"
        case let .failing(reason): return reason
        }
    }

    private var updateStatusColor: Color {
        switch state.updateStatus {
        case .failing: return .orange
        case .installed: return .green
        default: return .secondary
        }
    }

    // MARK: - What a Mac cannot do (DESK-21)

    private var theLid: some View {
        Section {
            Label("The Mac cannot monitor with the lid closed.", systemImage: "laptopcomputer.slash")
                .font(.callout.weight(.medium))
        } footer: {
            Text("While monitoring, the app stops the Mac from going to sleep on its own. But closing the lid, or choosing Sleep, stops the monitor — no app can prevent that. If the Mac does sleep, you will be told that monitoring was down and for how long.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    // MARK: - One alarm's sound and volume (ALRM-11/14)

    private func alarmSound(soundKey: String, volumeKey: String) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            LabeledContent("Sound") {
                HStack(spacing: 8) {
                    Picker("", selection: Binding(get: { string(soundKey, "") }, set: { set(soundKey, $0) })) {
                        ForEach(state.alarmSounds, id: \.id) { sound in
                            Text(sound.label).tag(sound.id)
                        }
                    }
                    .labelsHidden()
                    .frame(width: 160)
                    Button("Preview") {
                        state.previewAlarm(sound: string(soundKey, ""), volume: double(volumeKey, 0.85))
                    }
                }
            }
            LabeledContent("Volume") {
                HStack(spacing: 8) {
                    Image(systemName: "speaker.fill").font(.caption).foregroundStyle(.secondary)
                    // ALRM-4: never all the way down — a stored volume of zero would be a silent alarm.
                    Slider(value: doubleBinding(volumeKey, 0.85), in: 0.2...1.0)
                        .frame(width: 180)
                    Image(systemName: "speaker.wave.3.fill").font(.caption).foregroundStyle(.secondary)
                }
            }
        }
    }

    // MARK: - Settings plumbing

    private func bool(_ key: String, _ fallback: Bool) -> Bool { settings[key] as? Bool ?? fallback }
    private func int(_ key: String, _ fallback: Int) -> Int { settings[key] as? Int ?? fallback }
    private func double(_ key: String, _ fallback: Double) -> Double {
        settings[key] as? Double ?? Double(settings[key] as? Int ?? 0).nonZero ?? fallback
    }

    private func string(_ key: String, _ fallback: String) -> String {
        settings[key] as? String ?? fallback
    }

    private func set(_ key: String, _ value: Any) {
        settings[key] = value
        state.settings = settings
    }

    private func binding(_ key: String, _ fallback: Bool) -> Binding<Bool> {
        Binding(get: { bool(key, fallback) }, set: { set(key, $0) })
    }

    private func intBinding(_ key: String, _ fallback: Int) -> Binding<Double> {
        Binding(get: { Double(int(key, fallback)) }, set: { set(key, Int($0)) })
    }

    private func doubleBinding(_ key: String, _ fallback: Double) -> Binding<Double> {
        Binding(get: { double(key, fallback) }, set: { set(key, $0) })
    }
}

private extension Double {
    var nonZero: Double? { self == 0 ? nil : self }
}
