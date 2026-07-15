import ActivityKit
import Foundation

/// BG-2 / IOS-3: drives the monitoring Live Activity from the app side — started when monitoring
/// starts, updated as the feed state changes, ended when monitoring stops. A Live Activity outliving
/// the watch it describes would be its own quiet lie, so `end()` is not optional.
///
/// Updates are throttled by the caller to meaningful changes (status / muted / alarm), never the
/// 20 Hz level tick — iOS rate-limits Live Activity updates, and a parent does not need the decibels
/// on their lock screen, only whether the monitor is alive.
@MainActor
final class LiveActivityController {
    private var activity: Activity<MonitorActivityAttributes>?

    /// The card is marked "stale" by iOS if it is not refreshed within this window. `AppState` refreshes
    /// it every couple of minutes while the app is alive, so a healthy monitor always beats the clock;
    /// a suspended or killed app cannot, and the staleness is then the honest cue that it may be old.
    private let staleAfter: TimeInterval = 300

    func startOrUpdate(camera: String, state: MonitorActivityAttributes.ContentState) {
        guard ActivityAuthorizationInfo().areActivitiesEnabled else { return }
        let content = ActivityContent(state: state, staleDate: Date().addingTimeInterval(staleAfter))
        if let activity {
            Task { await activity.update(content) }
            return
        }
        do {
            activity = try Activity.request(
                attributes: MonitorActivityAttributes(cameraName: camera),
                content: content
            )
            Log.info("app", "live activity started for \(camera)")
        } catch {
            Log.warn("app", "could not start the live activity: \(error.localizedDescription)")
        }
    }

    func end() {
        guard let activity else { return }
        self.activity = nil
        Task { await activity.end(nil, dismissalPolicy: .immediate) }
        Log.info("app", "live activity ended")
    }
}
