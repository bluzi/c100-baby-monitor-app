import BabyMonitorCore
import Foundation
import UserNotifications

/// IOS-5 / ALRM-4: alarms post a local notification with an Acknowledge action.
///
/// The *sound* of the alarm is core's (`AppleRinger` plays the chosen tone on its own engine, over
/// the active `.playback` session, so it rings on a locked, silenced phone — ALRM-10i). This
/// notification is the **visual**: it puts the alarm on the lock screen, names what it is, and lets a
/// parent acknowledge without unlocking into the app. It carries no sound of its own — the tone is
/// the alarm, and two sounds at once is worse than one. If the device cannot make the tone at all
/// (ALRM-4), this still appears, which is the point: the failure is never silent.
final class AlarmNotifications: NSObject, UNUserNotificationCenterDelegate {
    static let shared = AlarmNotifications()

    private let center = UNUserNotificationCenter.current()
    private let categoryId = "ALARM"
    private let acknowledgeId = "ACKNOWLEDGE"
    private let alarmRequestId = "alarm"

    /// IOS-5: whether the parent granted notifications is surfaced through `AppState.notificationsDenied`
    /// (which these callbacks feed) — that is where the viewer reads it. If refused, the alarm still
    /// sounds and vibrates; this only governs whether the *notification* appears.

    func configure() {
        let acknowledge = UNNotificationAction(
            identifier: acknowledgeId,
            title: "Acknowledge",
            options: [.foreground]
        )
        let category = UNNotificationCategory(
            identifier: categoryId,
            actions: [acknowledge],
            intentIdentifiers: [],
            options: [.customDismissAction]
        )
        center.setNotificationCategories([category])
    }

    func requestAuthorization(_ done: @escaping (Bool) -> Void = { _ in }) {
        center.requestAuthorization(options: [.alert, .sound, .badge]) { granted, error in
            if let error { Log.warn("app", "notification authorization error: \(error.localizedDescription)") }
            Log.info("app", "notification permission \(granted ? "granted" : "denied — the alarm will still sound, but not show")")
            Task { @MainActor in done(granted) }
        }
    }

    /// Re-read the current status: the parent may have granted or revoked it in Settings since.
    func refreshAuthorization(_ done: @escaping (Bool) -> Void) {
        center.getNotificationSettings { settings in
            let ok = settings.authorizationStatus == .authorized || settings.authorizationStatus == .provisional
            Task { @MainActor in done(ok) }
        }
    }

    /// Post (or replace) the alarm notification. One at a time, keyed by a fixed id so a re-alarm
    /// updates rather than stacks.
    func postAlarm(kind: String, camera: String) {
        let content = UNMutableNotificationContent()
        content.title = kind == "BABY_NOISE" ? "The baby is crying" : "The feed is down"
        content.body = camera
        content.categoryIdentifier = categoryId
        content.interruptionLevel = .timeSensitive
        content.sound = nil // the tone is core's; see the note above
        let request = UNNotificationRequest(identifier: alarmRequestId, content: content, trigger: nil)
        center.add(request) { error in
            if let error { Log.warn("app", "could not post the alarm notification: \(error.localizedDescription)") }
        }
    }

    func clearAlarm() {
        center.removeDeliveredNotifications(withIdentifiers: [alarmRequestId])
        center.removePendingNotificationRequests(withIdentifiers: [alarmRequestId])
    }

    // MARK: - UNUserNotificationCenterDelegate

    /// Show the alarm even while the app is in the foreground — a parent watching the feed still needs
    /// to see it fired.
    func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        willPresent notification: UNNotification,
        withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void
    ) {
        completionHandler([.banner, .list])
    }

    /// ALRM-4 / IOS-5: acknowledge from the notification, without opening into the app first.
    func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        didReceive response: UNNotificationResponse,
        withCompletionHandler completionHandler: @escaping () -> Void
    ) {
        if response.actionIdentifier == acknowledgeId {
            Log.info("ui", "alarm acknowledged from the notification")
            BabyMonitor.shared.acknowledge()
        }
        completionHandler()
    }
}
