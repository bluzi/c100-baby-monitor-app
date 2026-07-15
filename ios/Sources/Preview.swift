import BabyMonitorCore
import SwiftUI
import UIKit

/// A visual harness, and **nothing else** — dead code unless `BM_UI_PREVIEW` is set. It exists
/// because judging a night-time interface by reading its source is how interfaces end up looking the
/// way they should not. It fakes exactly one thing, the `UiState` the shell renders, and touches no
/// Keychain, opens no socket, starts no monitor and writes no real preference.
///
/// Under the simulator, environment goes in with the `SIMCTL_CHILD_` prefix:
///
///     xcrun simctl launch --terminate-running-process \
///       --setenv? no: SIMCTL_CHILD_BM_UI_PREVIEW=viewer SIMCTL_CHILD_BM_UI_ALARM=BABY_NOISE \
///       xcrun simctl launch <udid> com.bluzi.babymonitor
enum Preview {
    private static let env = ProcessInfo.processInfo.environment

    static let active = env["BM_UI_PREVIEW"] != nil

    /// Pretend a sign-in is in flight, so the spinning button can be looked at without a Mi account.
    static var busy: Bool { env["BM_UI_BUSY"] != nil }

    /// Pose a step of sign-in only Xiaomi can normally produce: `captcha`, `code`, `error`.
    static var loginStep: String { env["BM_UI_STEP"] ?? "" }

    /// A drawn stand-in for the captcha Xiaomi would send.
    static var captchaImage: UIImage {
        let size = CGSize(width: 200, height: 60)
        return UIGraphicsImageRenderer(size: size).image { ctx in
            UIColor(white: 0.92, alpha: 1).setFill()
            ctx.fill(CGRect(origin: .zero, size: size))
            let attrs: [NSAttributedString.Key: Any] = [
                .font: UIFont.systemFont(ofSize: 30, weight: .bold),
                .foregroundColor: UIColor(white: 0.2, alpha: 1),
                .kern: 8,
            ]
            ("7K4M" as NSString).draw(at: CGPoint(x: 40, y: 12), withAttributes: attrs)
        }
    }

    /// The cameras the picker should show. `BM_UI_CAMERAS=` (empty) poses an account with none;
    /// `BM_UI_CAMERAS_ERROR=…` poses the failure the picker must never be a dead end in (CAM-5).
    static var cameras: [CameraInfo] {
        let names = env["BM_UI_CAMERAS"] ?? "Nursery,Toddler Room"
        return names.split(separator: ",").enumerated().map { index, name in
            CameraInfo(
                did: "11784950\(index)",
                name: String(name),
                model: "chuangmi.camera.077ac1",
                mac: "B8:88:80:5E:BE:4\(index)",
                ip: "192.168.1.13\(index)"
            )
        }
    }

    static var camerasError: String? { env["BM_UI_CAMERAS_ERROR"] }

    /// Start a real Live Activity from the posed state, so the Dynamic Island + lock-screen card can be
    /// looked at without a Mi account (BG-2i/IOS-3).
    static var liveActivity: Bool { env["BM_UI_LIVEACTIVITY"] != nil }

    /// An in-memory core: settings and sounds work, nothing is persisted, no camera is contacted.
    static func install() {
        BabyMonitor.shared.install(
            keyValueStore: MemoryStore(),
            secretBox: PassthroughBox(),
            logSink: { level, tag, message in Log.sink(level: level, tag: tag, message: message) }
        )
    }

    static func state() -> UiState {
        let screen = env["BM_UI_PREVIEW"] ?? "viewer"
        let status = env["BM_UI_STATUS"] ?? "live"
        let alarm = env["BM_UI_ALARM"]
        let outage = env["BM_UI_OUTAGE"]
        let running = env["BM_UI_STOPPED"] == nil
        let camera = env["BM_UI_CAMERA"] ?? "Nursery"
        let muted = env["BM_UI_MUTED"] != nil
        let sessionExpired = env["BM_UI_SESSION_EXPIRED"] != nil

        let health = MonitorHealth(
            running: running,
            status: status,
            activeAlarm: alarm,
            sessionExpired: sessionExpired,
            sleepOutage: outage
        )
        let text = friendly(status)
        return UiState(
            health: health,
            needsAttention: MacShell.shared.needsAttention(health: health),
            screen: screen,
            running: running,
            status: status,
            statusText: text,
            statusLine: muted ? "\(camera) — \(text) · muted" : "\(camera) — \(text)",
            cameraName: camera,
            level: Float(env["BM_UI_LEVEL"].flatMap(Double.init) ?? 6),
            levelMax: Float(env["BM_UI_LEVELMAX"].flatMap(Double.init) ?? 24),
            thresholdDb: Float(env["BM_UI_THRESHOLD"].flatMap(Double.init) ?? 12),
            muted: muted,
            alarmEnabled: env["BM_UI_ALARM_OFF"] == nil,
            activeAlarm: alarm,
            sessionExpired: sessionExpired,
            askingCryFeedback: env["BM_UI_FEEDBACK"] != nil,
            calibrationSteps: Int32(env["BM_UI_CALIBRATION"].flatMap { Int($0) } ?? 0),
            sleepOutage: outage,
            canStop: running,
            canResume: !running
        )
    }

    private static func friendly(_ status: String) -> String {
        switch status {
        case "live": return "Live"
        case "connecting": return "Connecting…"
        case "reconnecting": return "Reconnecting…"
        case "stopped": return "Stopped"
        case "session-expired": return "Session expired"
        default: return status
        }
    }

    /// Something for the glass to sit on: a dim room at night, roughly what the camera sends.
    static var backdrop: some View {
        ZStack {
            LinearGradient(
                colors: [Color(red: 0.09, green: 0.10, blue: 0.14), Color(red: 0.03, green: 0.03, blue: 0.05)],
                startPoint: .top,
                endPoint: .bottom
            )
            RadialGradient(
                colors: [Color(red: 0.35, green: 0.30, blue: 0.24).opacity(0.55), .clear],
                center: .init(x: 0.65, y: 0.4),
                startRadius: 8,
                endRadius: 520
            )
            Text("PREVIEW")
                .font(.system(size: 11, weight: .bold))
                .foregroundStyle(.white.opacity(0.16))
        }
    }
}

private final class MemoryStore: NSObject, KeyValueStore {
    private var values: [String: String] = [:]
    func get(key: String) -> String? { values[key] }
    func put(key: String, value: String) { values[key] = value }
    func remove(key: String) { values.removeValue(forKey: key) }
}

private final class PassthroughBox: NSObject, SecretBox {
    func seal(plain: String) -> String? { plain }
    func open(sealed: String) -> String? { sealed }
}
