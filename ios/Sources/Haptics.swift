import AudioToolbox
import Foundation

/// ALRM-11 vibrate, the iOS half of it — the Mac has no motor, but a phone does. The alarm *tone* is
/// core's (`AppleRinger`, the same sound the other platforms make); this only adds the buzz, looped
/// alongside the tone while an alarm rings and stopped on acknowledge.
///
/// `kSystemSoundID_Vibrate` is used rather than Core Haptics because it works on every iPhone without
/// a haptics-capability check, and vibration is a hardware promise this app will not gate behind a
/// device query. (It is silent on the Simulator, which has no motor — a `[device]` line on the
/// checklist, not something a screenshot can prove.)
@MainActor
enum Haptics {
    private static var timer: Timer?

    /// Start the repeating buzz that rides alongside a ringing alarm.
    static func startAlarm() {
        guard timer == nil else { return }
        vibrate()
        // The system vibration is ~0.4s; repeat on a cadence that reads as an alarm, not a tap.
        timer = Timer.scheduledTimer(withTimeInterval: 1.2, repeats: true) { _ in
            MainActor.assumeIsolated { vibrate() }
        }
    }

    static func stopAlarm() {
        timer?.invalidate()
        timer = nil
    }

    /// A single buzz, for previewing an alarm's vibrate setting from settings (ALRM-11).
    static func previewAlarm() {
        vibrate()
    }

    private static func vibrate() {
        AudioServicesPlaySystemSound(kSystemSoundID_Vibrate)
    }
}
