import ActivityKit
import AppIntents
import Foundation

/// BG-2i / IOS-3: the shape of the monitoring status the Live Activity carries onto the lock screen
/// and the Dynamic Island. Shared between the app (which starts and updates it) and the widget
/// extension (which draws it) — the one type both sides must agree on.
struct MonitorActivityAttributes: ActivityAttributes {
    struct ContentState: Codable, Hashable {
        /// The raw feed status ("live" / "reconnecting" / "error" / …) — drives the colour and glyph.
        var status: String
        /// The words a parent reads ("Nursery — Live"), already composed by core.
        var statusText: String
        var muted: Bool
        /// A ringing, unacknowledged alarm — the island and card go loud (IOS-3).
        var alarming: Bool
    }

    var cameraName: String
}

/// The Darwin-notification name the Stop control fires. A plain string both the intent and the app
/// know, so the widget never has to link the monitor.
let stopMonitoringDarwinName = "com.bluzi.babymonitor.stopFromLiveActivity"

/// BG-3i: the Live Activity's Stop control. Like Android's notification Stop (BG-3) it is a direct
/// action — the *confirmed* stop is the in-app one (BG-11); a deliberate tap on a specific lock-screen
/// button is not the stray tap that guards against.
///
/// A `LiveActivityIntent` runs in the **app's** process, which is alive because the monitor is playing
/// audio (BG-9i). Rather than link the whole monitor into the widget, it posts a Darwin notification
/// the running app observes and acts on — the widget stays a drawing of state and nothing more.
struct StopMonitoringIntent: LiveActivityIntent {
    static var title: LocalizedStringResource = "Stop Monitoring"

    func perform() async throws -> some IntentResult {
        CFNotificationCenterPostNotification(
            CFNotificationCenterGetDarwinNotifyCenter(),
            CFNotificationName(stopMonitoringDarwinName as CFString),
            nil, nil, true
        )
        return .result()
    }
}
