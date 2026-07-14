import AppKit
import BabyMonitorCore
import SwiftUI

/// AUTH-1/3/4/9/11: sign in to the Mi account that owns the camera.
///
/// **It is shaped like the system's own authorisation panel** — the one macOS puts up when an app
/// wants into the Keychain: a big icon on the left, a bold sentence saying who wants what, a quieter
/// line saying why, the fields with their labels right-aligned against them, and the buttons in the
/// bottom right corner where a Mac user's hand already is. That is not decoration. This is the one
/// screen in the app that asks a parent to hand over a password, and it should look like every other
/// thing on their Mac that has ever asked them that — familiar, plain, and obviously not a web page.
///
/// The fields are ordinary AppKit text fields on purpose: that is what makes ⌘V work (DESK-16),
/// what makes the Passwords app offer to fill them, and what makes Tab go where a Mac user expects.
struct LoginView: View {
    @EnvironmentObject private var state: AppState
    @State private var username = ""
    @State private var password = ""
    @State private var region = "sg"
    @State private var busy = Preview.busy
    @State private var error: String? = Preview.loginStep == "error" ? "That Mi account or password was not accepted." : nil
    @State private var captcha: NSImage? = Preview.loginStep == "captcha" ? Preview.captchaImage : nil
    @State private var captchaCode = ""
    @State private var codeChannel: String? = Preview.loginStep == "code" ? "phone" : nil
    @State private var codeTarget = Preview.loginStep == "code" ? "•••• 4821" : ""
    @State private var code = ""
    @FocusState private var focus: Field?

    private enum Field { case username, password, captcha, code }

    /// Xiaomi's server codes mean nothing to anyone. The camera is registered in one of these
    /// regions, and picking the wrong one looks exactly like a wrong password — so name them.
    private static let regionNames = [
        "cn": "China",
        "de": "Europe",
        "us": "United States",
        "ru": "Russia",
        "sg": "Singapore",
        "i2": "India",
    ]

    /// The view **is** the panel — it does not sit inside a window, it is what the window is sized
    /// to (`MonitorWindow.Chrome.dialog`). So no spacers, no expanding frames: whatever this view's
    /// ideal size turns out to be, that is exactly how big the dialog is, and it grows by itself when
    /// a captcha or an error appears.
    var body: some View {
        panel
            .fixedSize()
            .onAppear { focus = .username } // AUTH-11: ready to type, no click first
    }

    // MARK: - The panel

    /// The action row spans the **whole panel**, not the text column — so Quit sits on the panel's
    /// own left edge, under the icon, where the way out of a dialog belongs. Nesting it in the column
    /// beside the icon left it floating in the middle of nothing.
    private var panel: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack(alignment: .top, spacing: 18) {
                AppMark(size: 64)

                VStack(alignment: .leading, spacing: 14) {
                    VStack(alignment: .leading, spacing: 4) {
                        Text(title)
                            .font(.system(size: 13, weight: .bold))
                            .fixedSize(horizontal: false, vertical: true)
                        Text(subtitle)
                            .font(.system(size: 12))
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }

                    fields

                    if let error {
                        // AUTH-9: words, never the gateway's raw JSON.
                        Label(error, systemImage: "exclamationmark.circle.fill")
                            .font(.callout)
                            .foregroundStyle(.red)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
            }

            actions
        }
        .padding(22)
        .frame(width: 460)
        .panelSurface()
    }

    private var title: String {
        if codeChannel != nil { return "Baby Monitor needs the verification code Xiaomi just sent you." }
        if captcha != nil { return "Xiaomi wants to check that you are human." }
        return "Baby Monitor wants to sign in to your Mi account."
    }

    private var subtitle: String {
        if let codeChannel { return "The code was sent to your \(codeChannel) \(codeTarget)." }
        if captcha != nil { return "Type the characters in the picture to carry on signing in." }
        return "It uses the account that owns the camera, and stores nothing but its session — encrypted, in your Keychain."
    }

    // MARK: - Fields (labels right-aligned against them, as the system's panel does)

    @ViewBuilder
    private var fields: some View {
        if codeChannel != nil {
            LabeledField("Code:") {
                TextField("", text: $code)
                    .textFieldStyle(.roundedBorder)
                    .focused($focus, equals: .code)
                    .onSubmit(submitCode)
            }
        } else if let captcha {
            LabeledField("") {
                Image(nsImage: captcha)
                    .interpolation(.high)
                    .resizable()
                    .aspectRatio(contentMode: .fit)
                    .frame(height: 44)
                    .clipShape(RoundedRectangle(cornerRadius: 5, style: .continuous))
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            LabeledField("Captcha:") {
                TextField("", text: $captchaCode)
                    .textFieldStyle(.roundedBorder)
                    .focused($focus, equals: .captcha)
                    .onSubmit(submitCaptcha)
            }
        } else {
            LabeledField("Mi Account:") {
                TextField("", text: $username, prompt: Text("Email, phone or Mi ID"))
                    .textFieldStyle(.roundedBorder)
                    .textContentType(.username)
                    .focused($focus, equals: .username)
                    .onSubmit { focus = .password }
            }
            LabeledField("Password:") {
                SecureField("", text: $password)
                    .textFieldStyle(.roundedBorder)
                    .textContentType(.password)
                    .focused($focus, equals: .password)
                    .onSubmit(signIn)
            }
            LabeledField("Region:") {
                Picker("", selection: $region) {
                    ForEach(BabyMonitor.shared.regions(), id: \.self) { code in
                        Text(Self.regionNames[code] ?? code.uppercased()).tag(code)
                    }
                }
                .labelsHidden()
                .frame(maxWidth: 180, alignment: .leading)
            }
        }
    }

    // MARK: - Actions (bottom right, as they are everywhere else on this platform)

    /// The button *becomes* the progress: waiting on Xiaomi takes seconds, and a spinner parked
    /// anywhere else on the panel makes a parent wonder whether their click landed at all. The answer
    /// belongs on the thing they clicked. The label keeps its place while it spins so the button does
    /// not change size, and the button stays prominent rather than greying itself out — a grey button
    /// with a grey spinner on it reads as "nothing is happening", which is the opposite of the truth.
    private var actions: some View {
        HStack(spacing: 10) {
            // A borderless dialog has no traffic lights, so the way out has to be *on* it. Nothing
            // is being monitored on this screen — there is no camera yet — so Quit here is just
            // quitting, and does not stop a watch or ask whether you meant to (BG-14).
            Button("Quit") { NSApp.terminate(nil) }
                .disabled(busy)

            Spacer()

            if captcha != nil || codeChannel != nil {
                Button("Start Over") { reset() }
                    .keyboardShortcut(.cancelAction)
                    .disabled(busy)
            }
            Button(action: submit) {
                ZStack {
                    Text(primaryTitle).opacity(busy ? 0 : 1)
                    if busy {
                        ProgressView()
                            .controlSize(.small)
                            .tint(.white)
                    }
                }
                .frame(minWidth: 64)
            }
            .buttonStyle(.borderedProminent)
            .keyboardShortcut(.defaultAction)
            .disabled(!canSubmit && !busy)
            .allowsHitTesting(!busy)
            .accessibilityLabel(busy ? "Working…" : primaryTitle)
        }
        .padding(.top, 2)
    }

    private var primaryTitle: String {
        if codeChannel != nil { return "Verify" }
        if captcha != nil { return "Continue" }
        return "Sign In"
    }

    private var canSubmit: Bool {
        if codeChannel != nil { return !code.isEmpty }
        if captcha != nil { return !captchaCode.isEmpty }
        return !username.isEmpty && !password.isEmpty
    }

    private func submit() {
        if codeChannel != nil { return submitCode() }
        if captcha != nil { return submitCaptcha() }
        signIn()
    }

    // MARK: - Talking to Xiaomi

    private func signIn() {
        guard canSubmit, !busy else { return }
        busy = true
        error = nil
        BabyMonitor.shared.signIn(username: username, password: password, region: region) { handle($0) }
    }

    private func submitCaptcha() {
        guard canSubmit, !busy else { return }
        busy = true
        error = nil
        BabyMonitor.shared.submitCaptcha(code: captchaCode) { handle($0) }
    }

    private func submitCode() {
        guard canSubmit, !busy else { return }
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
            // AUTH-3: a rejected code yields a fresh captcha to retry, not a failure.
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
        focus = .username
    }
}

/// A label and its field, the label right-aligned against a fixed column — the layout of every
/// system panel that has ever asked a Mac user for a password.
private struct LabeledField<Content: View>: View {
    let label: String
    @ViewBuilder var content: Content

    init(_ label: String, @ViewBuilder content: () -> Content) {
        self.label = label
        self.content = content()
    }

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Text(label)
                .font(.system(size: 12))
                .frame(width: 92, alignment: .trailing)
            content
        }
    }
}

/// The app's own mark, for the places a Mac app shows itself: sign-in, and About.
struct AppMark: View {
    var size: CGFloat = 64

    var body: some View {
        Group {
            if let icon = NSApp.applicationIconImage {
                Image(nsImage: icon).resizable()
            } else {
                Image(systemName: "waveform")
                    .resizable()
                    .scaledToFit()
                    .padding(size * 0.22)
                    .background(Color.accentColor.gradient, in: RoundedRectangle(cornerRadius: size * 0.22, style: .continuous))
            }
        }
        .frame(width: size, height: size)
        .accessibilityHidden(true)
    }
}
