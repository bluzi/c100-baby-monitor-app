import AVKit
import BabyMonitorCore
import SwiftUI

/// Settings (ALRM, WATCH). Every value here is read from and written back to core's `Settings` as
/// JSON — there is deliberately no second settings model on this side to drift out of step with the
/// other platforms'. The clamping, the defaults and the legacy migrations all happen in core.
///
/// A monitor's settings are read at 3am by someone awake for twenty hours, so every switch says what
/// it *is*, and every one that could be misunderstood says what happens if it is wrong.
struct SettingsView: View {
    @EnvironmentObject private var state: AppState
    @Environment(\.dismiss) private var dismiss
    @State private var settings: [String: Any] = [:]
    @State private var peak: Float = 0

    var body: some View {
        NavigationStack {
            Form {
                cryingAlarm
                watchdog
                pictureInPicture
            }
            .scrollContentBackground(.hidden)
            .background(Color.black)
            .navigationTitle("Alerts & Settings")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() } }
            }
        }
        .preferredColorScheme(.dark)
        .onAppear {
            settings = state.settings
            peak = 0 // ALRM-12: the loudest-heard readout resets each time settings open
        }
        .onChange(of: state.ui.level) { _, new in peak = Swift.max(peak, new) }
    }

    // MARK: - The crying alarm (ALRM-1/2/7/11/12/16)

    private var cryingAlarm: some View {
        Section {
            Toggle("Alarm when the baby cries", isOn: binding("alarmEnabled", false))

            if bool("alarmEnabled", false) {
                sensitivity
                schedule
                alarmSound(soundKey: "cryAlarmSound", volumeKey: "cryAlarmVolume", vibrateKey: "cryAlarmVibrate", kind: "BABY_NOISE")
            }
        } header: {
            Text("Crying alarm")
        } footer: {
            Text("Higher sensitivity means quieter crying sets it off. The alarm rings until you acknowledge it, and keeps working while the feed is muted.")
        }
    }

    private var sensitivity: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("Sensitivity")
                Spacer()
                Text("\(int("alarmSensitivity", 5))").font(.body.monospacedDigit()).foregroundStyle(.secondary)
            }
            HStack(spacing: 10) {
                Text("Loud").font(.caption).foregroundStyle(.secondary)
                Slider(value: intBinding("alarmSensitivity", 5), in: 1...10, step: 1)
                Text("Quiet").font(.caption).foregroundStyle(.secondary)
            }
            // ALRM-12: the live room level on the same scale, with the trigger point marked, so the
            // alarm can be tuned against the real room.
            LevelBar(level: state.ui.level, max: state.ui.levelMax, threshold: state.ui.thresholdDb, armed: true)
                .padding(.top, 2)
            Text("Room now \(Int(state.ui.level)) dB · loudest since opening \(Int(peak)) dB")
                .font(.caption2.monospacedDigit())
                .foregroundStyle(.secondary)

            if state.ui.calibrationSteps > 0 {
                // ALRM-16/17: the learned tuning is visible and resettable — never hidden.
                HStack {
                    Text("Tuned \(state.ui.calibrationSteps) step\(state.ui.calibrationSteps == 1 ? "" : "s") stricter from your answers")
                        .font(.caption).foregroundStyle(.orange)
                    Spacer()
                    Button("Reset") { state.resetCalibration() }.font(.caption)
                }
            }
        }
    }

    // MARK: - The schedule (ALRM-7)

    private var schedule: some View {
        VStack(alignment: .leading, spacing: 8) {
            Picker("Active", selection: Binding(
                get: { string("alarmScheduleMode", "always") },
                set: { set("alarmScheduleMode", $0) }
            )) {
                Text("Always").tag("always")
                Text("Between times").tag("window")
            }
            .pickerStyle(.segmented)

            if string("alarmScheduleMode", "always") == "window" {
                DatePicker("From", selection: timeBinding("alarmWindowStartMinutes", 19 * 60), displayedComponents: .hourAndMinute)
                DatePicker("Until", selection: timeBinding("alarmWindowEndMinutes", 7 * 60), displayedComponents: .hourAndMinute)
                Text("Outside these hours nothing triggers. A window may cross midnight (e.g. 19:00–07:00).")
                    .font(.caption2).foregroundStyle(.secondary)
            }
        }
    }

    // MARK: - Picture-in-picture (BG-18/19/20)

    private var pictureInPicture: some View {
        // BG-20: where the OS has no PiP — the Simulator, or unusual hardware — the switch is off and
        // disabled, and the footer says why, rather than offering a control that would do nothing.
        let available = AVPictureInPictureController.isPictureInPictureSupported()
        return Section {
            Toggle(
                "Keep the video floating when you leave the app",
                isOn: available ? binding("pipEnabled", true) : .constant(false)
            )
            .disabled(!available)
        } header: {
            Text("Picture-in-picture")
        } footer: {
            Text(available
                ? "When you switch to another app, the live video stays in a small floating window. Audio and the crying alarm keep working either way."
                : "Picture-in-picture isn't available on this device.")
        }
    }

    // MARK: - The feed watchdog (WATCH-1/9/10)

    private var watchdog: some View {
        Section {
            Toggle("Alarm if the feed goes down", isOn: binding("watchdogEnabled", false))

            if !bool("alarmEnabled", false), bool("watchdogEnabled", false) {
                // WATCH-9/10: the watchdog guards the crying alarm, so it is armed only while that
                // alarm could itself ring. Say so rather than look broken.
                Label("Inactive while the crying alarm is off — it is what guards that alarm.", systemImage: "exclamationmark.triangle.fill")
                    .font(.caption).foregroundStyle(.orange)
            }

            if bool("watchdogEnabled", false) {
                HStack {
                    Text("Grace period")
                    Spacer()
                    Text("\(int("watchdogGraceSeconds", 30))s").font(.body.monospacedDigit()).foregroundStyle(.secondary)
                }
                Slider(value: intBinding("watchdogGraceSeconds", 30), in: 5...120, step: 5)
                alarmSound(soundKey: "feedAlarmSound", volumeKey: "feedAlarmVolume", vibrateKey: "feedAlarmVibrate", kind: "FEED_DOWN")
            }
        } header: {
            Text("Feed watchdog")
        } footer: {
            Text("If audio stops arriving for longer than the grace period, this rings — a monitor that has quietly died must never be mistaken for a quiet baby.")
        }
    }

    // MARK: - One alarm's sound, volume and vibrate (ALRM-11/14)

    private func alarmSound(soundKey: String, volumeKey: String, vibrateKey: String, kind: String) -> some View {
        Group {
            HStack {
                Picker("Sound", selection: Binding(get: { string(soundKey, "") }, set: { set(soundKey, $0) })) {
                    ForEach(state.alarmSounds, id: \.id) { sound in Text(sound.label).tag(sound.id) }
                }
                Button {
                    state.previewAlarm(sound: string(soundKey, ""), volume: double(volumeKey, 0.85), vibrate: bool(vibrateKey, true), kind: kind)
                } label: {
                    Image(systemName: "play.circle.fill").font(.title3)
                }
                .buttonStyle(.plain)
            }
            HStack(spacing: 10) {
                Image(systemName: "speaker.fill").font(.caption).foregroundStyle(.secondary)
                // ALRM-4: never all the way down — a stored volume of zero would be a silent alarm.
                Slider(value: doubleBinding(volumeKey, 0.85), in: 0.2...1.0)
                Image(systemName: "speaker.wave.3.fill").font(.caption).foregroundStyle(.secondary)
            }
            Toggle("Vibrate", isOn: binding(vibrateKey, true))
        }
    }

    // MARK: - Settings plumbing

    private func bool(_ key: String, _ fallback: Bool) -> Bool { settings[key] as? Bool ?? fallback }
    private func int(_ key: String, _ fallback: Int) -> Int { settings[key] as? Int ?? fallback }
    private func double(_ key: String, _ fallback: Double) -> Double {
        (settings[key] as? Double) ?? (settings[key] as? Int).map(Double.init) ?? fallback
    }
    private func string(_ key: String, _ fallback: String) -> String { settings[key] as? String ?? fallback }

    private func set(_ key: String, _ value: Any) {
        settings[key] = value
        state.settings = settings // ALRM-2: effective immediately
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

    /// Minutes-since-midnight ⟷ a Date the DatePicker can show, on an arbitrary fixed day.
    private func timeBinding(_ key: String, _ fallback: Int) -> Binding<Date> {
        Binding(
            get: {
                let minutes = int(key, fallback)
                return Calendar.current.date(bySettingHour: minutes / 60, minute: minutes % 60, second: 0, of: Date()) ?? Date()
            },
            set: { date in
                let c = Calendar.current.dateComponents([.hour, .minute], from: date)
                set(key, (c.hour ?? 0) * 60 + (c.minute ?? 0))
            }
        )
    }
}
