import AppKit
import BabyMonitorCore
import SwiftUI

/// A visual harness, and **nothing else**. It is dead code unless `BM_UI_PREVIEW` is set in the
/// environment, and it exists because the alternative — judging a night-time interface by reading
/// its source — is how interfaces end up looking the way this one used to.
///
/// It fakes exactly one thing: the `UiState` the shell renders. It touches no Keychain, opens no
/// socket, starts no monitor and writes no preference, so there is no way for it to disturb a real
/// installation.
///
///     BM_UI_PREVIEW=viewer  BM_UI_STATUS=live  ./macos/build/BabyMonitor.app/Contents/MacOS/BabyMonitor
///     BM_UI_PREVIEW=viewer  BM_UI_ALARM=BABY_NOISE  ...
///     BM_UI_PREVIEW=login   ...
///     BM_UI_PREVIEW=viewer  BM_UI_SHAPE=mini  ...
enum Preview {
    private static let env = ProcessInfo.processInfo.environment

    static let active = env["BM_UI_PREVIEW"] != nil

    static var shape: WindowShape { env["BM_UI_SHAPE"] == "mini" ? .mini : .full }

    /// Pretend the pointer is over the window, so the hover chrome can be looked at in a still
    /// picture (MACOS-15).
    static var hovering: Bool { env["BM_UI_HOVER"] != nil }

    /// Pretend a sign-in is in flight, so the spinning button can be looked at without a Mi account.
    static var busy: Bool { env["BM_UI_BUSY"] != nil }

    /// Pose a step of sign-in that only Xiaomi can normally produce: `captcha`, `code`, `error`.
    static var loginStep: String { env["BM_UI_STEP"] ?? "" }

    /// A drawn stand-in for the captcha Xiaomi would send.
    static var captchaImage: NSImage {
        let size = NSSize(width: 160, height: 44)
        let image = NSImage(size: size)
        image.lockFocus()
        NSColor(calibratedWhite: 0.92, alpha: 1).setFill()
        NSRect(origin: .zero, size: size).fill()
        let text = "7K4M" as NSString
        text.draw(
            at: NSPoint(x: 26, y: 8),
            withAttributes: [
                .font: NSFont.systemFont(ofSize: 24, weight: .bold),
                .foregroundColor: NSColor(calibratedWhite: 0.2, alpha: 1),
                .kern: 6,
            ]
        )
        image.unlockFocus()
        return image
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

    /// Which alert to put on screen and photograph: `quit`, `update`, `uptodate`, `failed`.
    static var alert: String? { env["BM_UI_ALERT"] }

    /// Pretend the camera sends a picture of this shape (`BM_UI_ASPECT=1.333`), so MACOS-19 can be
    /// checked against a camera that is not 16:9 without owning one.
    static var aspect: CGFloat { CGFloat(env["BM_UI_ASPECT"].flatMap(Double.init) ?? 0) }

    /// Where to write a picture of the app's own window, and then quit. Used to look at the design
    /// without a human having to be sitting in front of the Mac — including on a locked screen,
    /// where nothing outside the process can see a window at all.
    static var snapshotPath: String? { env["BM_UI_SNAPSHOT"] }

    static func snapshot(_ window: NSWindow, to path: String, attempt: Int = 0, done: @escaping () -> Void) {
        // The window server's own picture of the window: it is the only one that composites the
        // glass, and the glass is the thing being judged. On a locked screen it may hand back a
        // surface it has not drawn yet, so an empty answer is retried rather than believed.
        let image = CGWindowListCreateImage(
            .null,
            .optionIncludingWindow,
            CGWindowID(window.windowNumber),
            [.boundsIgnoreFraming, .bestResolution]
        )

        if let image, !isEmpty(image),
           let data = NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:])
        {
            try? data.write(to: URL(fileURLWithPath: path))
            Log.info("ui", "preview: wrote \(path)")
            return done()
        }

        guard attempt < 12 else {
            // Last resort: draw the view tree straight into a bitmap. No blur behind the glass, but
            // every measurement, colour and word lands where it really lands.
            if let view = window.contentView,
               let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds)
            {
                view.cacheDisplay(in: view.bounds, to: rep)
                if let data = rep.representation(using: .png, properties: [:]) {
                    try? data.write(to: URL(fileURLWithPath: path))
                    Log.warn("ui", "preview: wrote \(path) without the compositor")
                }
            }
            return done()
        }

        window.displayIfNeeded()
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) {
            snapshot(window, to: path, attempt: attempt + 1, done: done)
        }
    }

    /// Nothing was drawn: every pixel is fully transparent.
    private static func isEmpty(_ image: CGImage) -> Bool {
        guard let rep = NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]),
              let bitmap = NSBitmapImageRep(data: rep)
        else {
            return true
        }
        let stepX = max(bitmap.pixelsWide / 16, 1)
        let stepY = max(bitmap.pixelsHigh / 16, 1)
        for x in stride(from: 0, to: bitmap.pixelsWide, by: stepX) {
            for y in stride(from: 0, to: bitmap.pixelsHigh, by: stepY) {
                if let color = bitmap.colorAt(x: x, y: y), color.alphaComponent > 0.05 { return false }
            }
        }
        return true
    }

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

        let health = MonitorHealth(
            running: running,
            status: status,
            activeAlarm: alarm,
            sessionExpired: false,
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
            statusLine: "\(camera) — \(text)",
            cameraName: camera,
            level: Float(env["BM_UI_LEVEL"].flatMap(Double.init) ?? 6),
            levelMax: 40,
            thresholdDb: 17,
            muted: env["BM_UI_MUTED"] != nil,
            alarmEnabled: true,
            activeAlarm: alarm,
            sessionExpired: false,
            askingCryFeedback: env["BM_UI_FEEDBACK"] != nil,
            calibrationSteps: 0,
            sleepOutage: outage,
            canStop: running,
            canResume: !running
        )
    }

    private static func friendly(_ status: String) -> String {
        switch status {
        case "live": return "Live"
        case "connecting": return "Connecting…"
        case "stopped": return "Stopped"
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
                center: .init(x: 0.65, y: 0.35),
                startRadius: 8,
                endRadius: 420
            )
            Text("PREVIEW")
                .font(.system(size: 11, weight: .bold))
                .foregroundStyle(.white.opacity(0.18))
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
