import BabyMonitorCore
import SwiftUI

/// The alarm settings (ALRM). Every value here is read from and written back to core's `Settings`
/// as JSON — there is deliberately no second settings model on this side to drift out of step with
/// the phone's. The clamping, the defaults and the legacy migrations all happen in core.
struct SettingsView: View {
    @EnvironmentObject private var state: AppState
    @State private var settings: [String: Any] = [:]

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                Text("Alerts").font(.title2.bold())

                // ALRM-1/2: the crying alarm and its one detection control.
                Toggle("Alarm when the baby cries", isOn: binding("alarmEnabled", false))
                    .toggleStyle(.switch)

                VStack(alignment: .leading) {
                    Text("Sensitivity: \(int("alarmSensitivity", 5))")
                    Slider(
                        value: intBinding("alarmSensitivity", 5),
                        in: 1...10,
                        step: 1
                    )
                    Text("Higher means quieter crying will set it off.")
                        .font(.caption).foregroundStyle(.secondary)
                    if state.ui.calibrationSteps > 0 {
                        // ALRM-16/17: the learned tuning is visible and resettable — never hidden.
                        HStack {
                            Text("Learned from your answers: \(state.ui.calibrationSteps) step(s) stricter")
                                .font(.caption).foregroundStyle(.orange)
                            Button("Reset") { BabyMonitor.shared.resetCalibration() }
                                .buttonStyle(.link)
                        }
                    }
                }
                .disabled(!bool("alarmEnabled", false))

                alarmSection(
                    title: "Crying alarm sound",
                    soundKey: "cryAlarmSound",
                    volumeKey: "cryAlarmVolume"
                )

                Divider()

                // WATCH-1: the feed watchdog.
                Toggle("Alarm if the feed goes down", isOn: binding("watchdogEnabled", false))
                    .toggleStyle(.switch)
                if !bool("alarmEnabled", false), bool("watchdogEnabled", false) {
                    // WATCH-9/10: the watchdog guards the crying alarm, so it is armed only while
                    // the crying alarm could itself ring. Say so rather than look broken.
                    Text("Inactive while the crying alarm is off — it guards that alarm.")
                        .font(.caption).foregroundStyle(.orange)
                }
                VStack(alignment: .leading) {
                    Text("Grace period: \(int("watchdogGraceSeconds", 30))s")
                    Slider(value: intBinding("watchdogGraceSeconds", 30), in: 5...120, step: 5)
                }
                .disabled(!bool("watchdogEnabled", false))

                alarmSection(
                    title: "Feed-down alarm sound",
                    soundKey: "feedAlarmSound",
                    volumeKey: "feedAlarmVolume"
                )

                Divider()

                Text("The Mac cannot monitor with the lid closed.")
                    .font(.callout).bold()
                Text(
                    "While monitoring, the app stops the Mac from going to sleep on its own. "
                        + "But closing the lid, or choosing Sleep, stops the monitor — no app can prevent that. "
                        + "If the Mac does sleep, you will be told how long the monitor was down."
                )
                .font(.caption)
                .foregroundStyle(.secondary)
            }
            .padding(24)
        }
        .frame(width: 460)
        .preferredColorScheme(.dark)
        .onAppear { settings = state.settings }
    }

    // ALRM-11: each alarm has its own sound and volume, previewable exactly as it would ring.
    private func alarmSection(title: String, soundKey: String, volumeKey: String) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title).font(.headline)
            ForEach(state.alarmSounds, id: \.id) { sound in
                HStack {
                    Button {
                        set(soundKey, sound.id)
                    } label: {
                        HStack {
                            Image(systemName: string(soundKey, "") == sound.id ? "largecircle.fill.circle" : "circle")
                            VStack(alignment: .leading) {
                                Text(sound.label)
                                Text(sound.description).font(.caption).foregroundStyle(.secondary)
                            }
                        }
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    Spacer()
                    Button("Preview") {
                        state.previewAlarm(sound: sound.id, volume: double(volumeKey, 0.85))
                    }
                    .controlSize(.small)
                }
            }
            HStack {
                Image(systemName: "speaker.fill")
                // ALRM-4: never all the way down — a stored volume of zero would be a silent alarm.
                Slider(value: doubleBinding(volumeKey, 0.85), in: 0.2...1.0)
                Image(systemName: "speaker.wave.3.fill")
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
