import BabyMonitorCore
import SwiftUI
import UIKit

@main
struct BabyMonitorApp: App {
    @UIApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @StateObject private var state = AppState()

    var body: some Scene {
        WindowGroup {
            RootView()
                .environmentObject(state)
                .preferredColorScheme(.dark) // UI-1: dark, for a dark room, always
        }
    }
}

final class AppDelegate: NSObject, UIApplicationDelegate {
    func application(
        _ application: UIApplication,
        didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]? = nil
    ) -> Bool {
        // IOS-5: acknowledging an alarm from its notification routes back through core.
        UNUserNotificationCenter.current().delegate = AlarmNotifications.shared
        return true
    }

    /// LIVE-9 (mobile): the live feed is landscape only; every other screen is portrait. The mask is
    /// owned by `OrientationLock`, which the viewer narrows on appear and widens again on leave.
    func application(
        _ application: UIApplication,
        supportedInterfaceOrientationsFor window: UIWindow?
    ) -> UIInterfaceOrientationMask {
        OrientationLock.mask
    }
}

/// LIVE-9: which orientations are allowed right now. The viewer locks landscape; the rest of the app
/// is portrait. The Info.plist allows all three, and this narrows it at runtime per screen.
@MainActor
enum OrientationLock {
    static var mask: UIInterfaceOrientationMask = .portrait

    static func set(_ new: UIInterfaceOrientationMask) {
        guard mask != new else { return }
        mask = new
        guard let scene = UIApplication.shared.connectedScenes.first as? UIWindowScene else { return }
        scene.keyWindow?.rootViewController?.setNeedsUpdateOfSupportedInterfaceOrientations()
        scene.requestGeometryUpdate(.iOS(interfaceOrientations: new)) { error in
            Log.warn("ui", "orientation update failed: \(error.localizedDescription)")
        }
    }
}
