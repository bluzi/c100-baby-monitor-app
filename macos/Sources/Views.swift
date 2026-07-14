import AppKit
import BabyMonitorCore
import SwiftUI

// UI-1: dark throughout, for use in a dark room at night. UI-2: English.

/// APP-1: routing is a pure function of what the app already knows — and it is core's function,
/// not a second copy of the rule living here.
struct RootView: View {
    @EnvironmentObject private var state: AppState

    var body: some View {
        Group {
            switch state.ui.screen {
            case "login": LoginView()
            case "devices": CamerasView()
            default: ViewerView()
            }
        }
        .frame(minWidth: 720, minHeight: 460)
        .background(Color.black)
        .preferredColorScheme(.dark)
    }
}

// MARK: - Sign in (AUTH)

struct LoginView: View {
    @EnvironmentObject private var state: AppState
    @State private var username = ""
    @State private var password = ""
    @State private var region = "sg"
    @State private var busy = false
    @State private var error: String?
    @State private var captcha: NSImage?
    @State private var captchaCode = ""
    @State private var codeChannel: String?
    @State private var codeTarget = ""
    @State private var code = ""
    @FocusState private var focus: Field?

    private enum Field { case username, password, captcha, code }

    var body: some View {
        VStack(spacing: 16) {
            Text("Baby Monitor").font(.largeTitle.bold())
            Text("Sign in to your Mi account").foregroundStyle(.secondary)

            if let codeChannel {
                // AUTH-11: the code field is focused with the cursor in it — typing just works.
                Text("A code was sent to your \(codeChannel) \(codeTarget)")
                    .font(.callout).foregroundStyle(.secondary)
                TextField("Verification code", text: $code)
                    .textFieldStyle(.roundedBorder)
                    .focused($focus, equals: .code)
                    .onSubmit(submitCode)
                Button("Submit code", action: submitCode).buttonStyle(.borderedProminent).disabled(busy)
                Button("Start over") { reset() }.buttonStyle(.link)
            } else if let captcha {
                Image(nsImage: captcha).interpolation(.high).frame(height: 60)
                TextField("Captcha", text: $captchaCode)
                    .textFieldStyle(.roundedBorder)
                    .focused($focus, equals: .captcha)
                    .onSubmit(submitCaptcha)
                Button("Submit", action: submitCaptcha).buttonStyle(.borderedProminent).disabled(busy)
            } else {
                TextField("Email or username", text: $username)
                    .textFieldStyle(.roundedBorder)
                    .focused($focus, equals: .username)
                    .onSubmit { focus = .password }
                SecureField("Password", text: $password)
                    .textFieldStyle(.roundedBorder)
                    .focused($focus, equals: .password)
                    .onSubmit(signIn)
                Picker("Server region", selection: $region) {
                    ForEach(BabyMonitor.shared.regions(), id: \.self) { Text($0.uppercased()).tag($0) }
                }
                Button("Sign in", action: signIn)
                    .buttonStyle(.borderedProminent)
                    .disabled(busy || username.isEmpty || password.isEmpty)
            }

            if busy { ProgressView().controlSize(.small) }
            // AUTH-9: words, never the gateway's raw JSON.
            if let error {
                Text(error).foregroundStyle(.red).font(.callout).multilineTextAlignment(.center)
            }
        }
        .padding(40)
        .frame(maxWidth: 420)
        .onAppear { focus = .username } // AUTH-11
    }

    private func signIn() {
        busy = true
        error = nil
        BabyMonitor.shared.signIn(username: username, password: password, region: region) { handle($0) }
    }

    private func submitCaptcha() {
        busy = true
        error = nil
        BabyMonitor.shared.submitCaptcha(code: captchaCode) { handle($0) }
    }

    private func submitCode() {
        busy = true
        error = nil
        BabyMonitor.shared.submitCode(ticket: code) { handle($0) }
    }

    private func handle(_ result: SignInResult) {
        busy = false
        switch result.kind {
        case "ok":
            reset()
        case "captcha":
            captcha = result.captchaImage.flatMap { NSImage(data: $0) }
            captchaCode = ""
            focus = .captcha
        case "code":
            codeChannel = result.channel
            codeTarget = result.maskedTarget ?? ""
            focus = .code
        default:
            error = result.message ?? "Sign-in failed."
        }
    }

    private func reset() {
        captcha = nil
        codeChannel = nil
        code = ""
        captchaCode = ""
    }
}

// MARK: - Camera picker (CAM)

struct CamerasView: View {
    @EnvironmentObject private var state: AppState
    @State private var cameras: [CameraInfo] = []
    @State private var busy = true
    @State private var error: String?

    var body: some View {
        VStack(spacing: 12) {
            Text("Choose a camera").font(.title2.bold())

            if busy {
                ProgressView()
            } else if let error {
                // CAM-5 / APP-3: readable, with a way to retry — never a dead end.
                Text(error).foregroundStyle(.red).multilineTextAlignment(.center)
                Button("Try again", action: load).buttonStyle(.borderedProminent)
            } else if cameras.isEmpty {
                Text("No cameras on this account.").foregroundStyle(.secondary)
                Button("Try again", action: load)
            } else {
                List(cameras, id: \.did) { camera in
                    Button {
                        BabyMonitor.shared.selectCamera(camera: camera)
                        BabyMonitor.shared.start()
                    } label: {
                        VStack(alignment: .leading) {
                            Text(camera.name.isEmpty ? camera.did : camera.name).font(.headline)
                            Text(camera.model).font(.caption).foregroundStyle(.secondary)
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                }
            }

            Button("Sign out") { state.signOut() }.buttonStyle(.link)
        }
        .padding(24)
        .onAppear(perform: load)
    }

    private func load() {
        busy = true
        error = nil
        BabyMonitor.shared.loadCameras { list, message in
            busy = false
            cameras = list ?? []
            error = message
        }
    }
}

// MARK: - The live feed (LIVE)

/// The picture. Core pushes frames into the renderer; this view just owns the layer.
struct VideoSurface: NSViewRepresentable {
    func makeNSView(context: Context) -> VideoLayerView {
        let view = VideoLayerView(frame: .zero)
        let bridge = VideoRendererBridge(view: view)
        context.coordinator.bridge = bridge
        AppleVideo.shared.renderer = bridge
        return view
    }

    func updateNSView(_ nsView: VideoLayerView, context: Context) {}

    func makeCoordinator() -> Coordinator { Coordinator() }

    static func dismantleNSView(_ nsView: VideoLayerView, coordinator: Coordinator) {
        // LIVE-7: the picture goes away, audio does not. Core simply stops having anywhere to draw.
        if AppleVideo.shared.renderer === coordinator.bridge {
            AppleVideo.shared.renderer = nil
        }
    }

    final class Coordinator {
        var bridge: VideoRendererBridge?
    }
}

struct ViewerView: View {
    @EnvironmentObject private var state: AppState
    @State private var confirmingStop = false

    var body: some View {
        ZStack {
            VideoSurface().ignoresSafeArea()

            VStack {
                topBar
                Spacer()
                if let outage = state.ui.sleepOutage { sleepBanner(outage) }
                if state.ui.activeAlarm != nil { alarmBanner }
                if state.ui.askingCryFeedback { feedbackBanner }
                controls
            }
            .padding(16)
        }
        .background(Color.black)
        .onAppear { if !state.ui.running { state.start() } }
        .confirmationDialog("Stop monitoring?", isPresented: $confirmingStop) {
            Button("Stop monitoring", role: .destructive) { state.stop() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Audio, the alarm and the connection all stop. The baby will not be monitored.")
        }
    }

    // LIVE-4/6: status and the ambient-relative level, with the alarm's trigger point on it.
    private var topBar: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text(state.ui.statusLine).font(.headline)
                Spacer()
                Menu {
                    Button("Switch camera") { state.switchCamera() }
                    Button("Sign out") { state.signOut() }
                    Divider()
                    Text("Version \(AppDelegate.version)") // LIVE-15 / UPD-6
                } label: {
                    Image(systemName: "ellipsis.circle")
                }
                .menuStyle(.borderlessButton)
                .frame(width: 40)
            }
            LevelBar(
                level: state.ui.level,
                max: state.ui.levelMax,
                threshold: state.ui.thresholdDb,
                armed: state.ui.alarmEnabled
            )
            if state.sleepUnprotected {
                // BG-12: if the inhibitor could not be held, say so rather than appear to monitor.
                Label("The Mac may sleep and stop monitoring", systemImage: "exclamationmark.triangle.fill")
                    .font(.caption).foregroundStyle(.orange)
            }
        }
        .padding(12)
        .background(.black.opacity(0.55), in: RoundedRectangle(cornerRadius: 10))
    }

    private var controls: some View {
        HStack(spacing: 28) {
            // BG-11 / WATCH-11: which controls exist is core's decision, shared with the phone.
            if state.ui.canResume {
                ControlButton(symbol: "play.fill", label: "Resume") { state.start() }
            }
            if state.ui.canStop {
                ControlButton(symbol: "stop.fill", label: "Stop") { confirmingStop = true }
            }
            ControlButton(
                symbol: state.ui.muted ? "speaker.slash.fill" : "speaker.wave.2.fill",
                label: state.ui.muted ? "Muted — click for sound" : "Mute",
                active: state.ui.muted // LIVE-2: muted draws latched, unmistakably a state
            ) { state.toggleMute() }
            NightVisionButton()
            ControlButton(symbol: "slider.horizontal.3", label: "Alerts") {
                NSApp.sendAction(Selector(("showSettings")), to: nil, from: nil)
            }
        }
        .padding(12)
        .background(.black.opacity(0.55), in: RoundedRectangle(cornerRadius: 10))
    }

    private var alarmBanner: some View {
        HStack {
            Label(
                state.ui.activeAlarm == "BABY_NOISE" ? "The baby is crying" : "Feed unavailable",
                systemImage: "bell.badge.fill"
            )
            .font(.headline)
            Spacer()
            Button("Acknowledge") { state.acknowledge() }.buttonStyle(.borderedProminent)
        }
        .padding(12)
        .background(.red.opacity(0.85), in: RoundedRectangle(cornerRadius: 10))
    }

    // ALRM-15/16: one yes/no moves this camera's learned tuning a step. Dismissing learns nothing.
    private var feedbackBanner: some View {
        HStack {
            Text("Was the baby crying?").font(.headline)
            Spacer()
            Button("Yes") { BabyMonitor.shared.submitCryFeedback(wasCry: true) }
            Button("No, false alarm") { BabyMonitor.shared.submitCryFeedback(wasCry: false) }
            Button("Dismiss") { BabyMonitor.shared.dismissCryFeedback() }.buttonStyle(.link)
        }
        .padding(12)
        .background(.black.opacity(0.75), in: RoundedRectangle(cornerRadius: 10))
    }

    /// MACOS-11: the Mac slept, the monitor was down, and this is where it says so.
    private func sleepBanner(_ outage: String) -> some View {
        HStack {
            Label(outage, systemImage: "moon.zzz.fill").font(.headline)
            Spacer()
            Button("Dismiss") { state.dismissSleepOutage() }
        }
        .padding(12)
        .background(.orange.opacity(0.9), in: RoundedRectangle(cornerRadius: 10))
    }
}

struct NightVisionButton: View {
    @State private var mode: String?
    @State private var error: String?

    var body: some View {
        Menu {
            // LIVE-10: the camera's three modes; the mode lives on the camera, shared by viewers.
            ForEach(["OFF", "AUTO", "ON"], id: \.self) { option in
                Button(option.capitalized) { set(option) }
            }
            if let error { Text(error) }
        } label: {
            Image(systemName: mode == "ON" ? "moon.fill" : "moon")
                .font(.title2)
        }
        .menuStyle(.borderlessButton)
        .frame(width: 44)
        .onAppear {
            BabyMonitor.shared.nightVision { value, message in
                mode = value
                error = message
            }
        }
    }

    private func set(_ option: String) {
        BabyMonitor.shared.setNightVision(mode: option) { message in
            if let message {
                error = message // LIVE-10: readable error, and the displayed mode does not change
            } else {
                mode = option
            }
        }
    }
}

struct ControlButton: View {
    let symbol: String
    let label: String
    var active = false
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.title2)
                .frame(width: 40, height: 40)
                .background(active ? Color.red.opacity(0.85) : .clear, in: Circle())
        }
        .buttonStyle(.plain)
        .help(label)
        .accessibilityLabel(label)
    }
}

/// LIVE-6 / ALRM-12: loudness above the room's own baseline, with the alarm's trigger point marked.
struct LevelBar: View {
    let level: Float
    let max: Float
    let threshold: Float
    let armed: Bool

    var body: some View {
        GeometryReader { geo in
            let width = geo.size.width
            let fill = CGFloat(min(level / Swift.max(max, 1), 1)) * width
            let mark = CGFloat(min(threshold / Swift.max(max, 1), 1)) * width
            ZStack(alignment: .leading) {
                Capsule().fill(.white.opacity(0.15))
                Capsule()
                    .fill(armed && level >= threshold ? Color.red : Color.green)
                    .frame(width: fill)
                if armed {
                    Rectangle().fill(.white).frame(width: 2).offset(x: mark)
                }
            }
        }
        .frame(height: 6)
    }
}

// MARK: - Mini window (MACOS-5)

struct MiniView: View {
    @EnvironmentObject private var state: AppState

    var body: some View {
        ZStack(alignment: .bottom) {
            VideoSurface()
            HStack(spacing: 8) {
                Circle()
                    .fill(state.ui.status == "live" ? .green : .orange)
                    .frame(width: 8, height: 8)
                Text(state.ui.statusText).font(.caption)
                Spacer()
                Button {
                    state.toggleMute()
                } label: {
                    Image(systemName: state.ui.muted ? "speaker.slash.fill" : "speaker.wave.2.fill")
                }
                .buttonStyle(.plain)
                if state.ui.activeAlarm != nil {
                    Button("Acknowledge") { state.acknowledge() }.controlSize(.small)
                }
            }
            .padding(8)
            .background(.black.opacity(0.6))
        }
        .background(Color.black)
        .preferredColorScheme(.dark)
    }
}
