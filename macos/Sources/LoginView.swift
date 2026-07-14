import AppKit
import BabyMonitorCore
import SwiftUI

/// AUTH-1/3/4/9/11: sign in to the Mi account that owns the camera.
///
/// The fields are ordinary AppKit text fields on purpose — that is what makes ⌘V work (MACOS-13),
/// what makes the Passwords app offer to fill them, and what makes the Tab key go where a Mac user
/// expects. A hand-rolled field would look the same and behave subtly wrong, at the exact moment a
/// parent is trying to get this over with.
struct LoginView: View {
    @EnvironmentObject private var state: AppState
    @State private var username = ""
    @State private var password = ""
    @State private var region = "sg"
    @State private var busy = Preview.busy
    @State private var error: String?
    @State private var captcha: NSImage?
    @State private var captchaCode = ""
    @State private var codeChannel: String?
    @State private var codeTarget = ""
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

    var body: some View {
        ScrollView {
            VStack(spacing: 18) {
                AppMark(size: 64)
                    .padding(.bottom, 2)

                VStack(spacing: 6) {
                    Text("Baby Monitor")
                        .font(.system(size: 26, weight: .semibold))
                    Text(subtitle)
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                        .fixedSize(horizontal: false, vertical: true)
                }

                card

                if let error {
                    // AUTH-9: words, never the gateway's raw JSON.
                    Label(error, systemImage: "exclamationmark.circle.fill")
                        .font(.callout)
                        .foregroundStyle(.red)
                        .multilineTextAlignment(.leading)
                        .fixedSize(horizontal: false, vertical: true)
                        .frame(maxWidth: 340)
                }
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 48)
        }
        .onAppear { focus = .username } // AUTH-11: ready to type, no click first
    }

    private var subtitle: String {
        if codeChannel != nil { return "A verification code was sent to your \(codeChannel ?? "") \(codeTarget)" }
        if captcha != nil { return "Xiaomi wants to check you are human" }
        return "Sign in to the Mi account that owns the camera"
    }

    private var card: some View {
        VStack(alignment: .leading, spacing: 14) {
            if codeChannel != nil {
                // AUTH-4/11: the code field is focused with the cursor in it — typing just works.
                field("Verification code", text: $code, focus: .code, submit: submitCode)
                primaryButton("Verify", action: submitCode, enabled: !code.isEmpty)
                Button("Start over") { reset() }
                    .buttonStyle(.link)
            } else if let captcha {
                Image(nsImage: captcha)
                    .interpolation(.high)
                    .resizable()
                    .aspectRatio(contentMode: .fit)
                    .frame(height: 56)
                    .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
                field("Captcha", text: $captchaCode, focus: .captcha, submit: submitCaptcha)
                primaryButton("Continue", action: submitCaptcha, enabled: !captchaCode.isEmpty)
                Button("Start over") { reset() }
                    .buttonStyle(.link)
            } else {
                VStack(alignment: .leading, spacing: 6) {
                    fieldLabel("Mi account")
                    TextField("Email, phone or Mi ID", text: $username)
                        .textFieldStyle(.roundedBorder)
                        .textContentType(.username)
                        .focused($focus, equals: .username)
                        .onSubmit { focus = .password }
                }
                VStack(alignment: .leading, spacing: 6) {
                    fieldLabel("Password")
                    SecureField("Password", text: $password)
                        .textFieldStyle(.roundedBorder)
                        .textContentType(.password)
                        .focused($focus, equals: .password)
                        .onSubmit(signIn)
                }
                VStack(alignment: .leading, spacing: 6) {
                    fieldLabel("Server region")
                    Picker("", selection: $region) {
                        ForEach(BabyMonitor.shared.regions(), id: \.self) { code in
                            Text(Self.regionNames[code] ?? code.uppercased()).tag(code)
                        }
                    }
                    .labelsHidden()
                    .pickerStyle(.menu)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    Text("The region the camera is registered in.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                primaryButton(
                    "Sign In",
                    action: signIn,
                    enabled: !username.isEmpty && !password.isEmpty
                )
            }

        }
        .padding(20)
        .frame(width: 340)
        .glassSurface(cornerRadius: 18)
    }

    private func fieldLabel(_ text: String) -> some View {
        Text(text)
            .font(.caption.weight(.medium))
            .foregroundStyle(.secondary)
    }

    private func field(_ title: String, text: Binding<String>, focus field: Field, submit: @escaping () -> Void) -> some View {
        TextField(title, text: text)
            .textFieldStyle(.roundedBorder)
            .focused($focus, equals: field)
            .onSubmit(submit)
    }

    /// The button *becomes* the progress. Waiting on Xiaomi takes seconds, and a spinner parked
    /// somewhere else on the card makes the parent wonder whether their click landed at all — the
    /// answer belongs on the thing they clicked. The label is kept in the layout while it spins, so
    /// the button does not change size and nothing under it jumps.
    private func primaryButton(_ title: String, action: @escaping () -> Void, enabled: Bool) -> some View {
        Button {
            guard !busy else { return } // a second click while Xiaomi thinks would sign in twice
            action()
        } label: {
            ZStack {
                // The label keeps its place in the layout while it is invisible, so the button does
                // not change size and nothing under it jumps.
                Text(title).opacity(busy ? 0 : 1)
                if busy {
                    ProgressView()
                        .controlSize(.small)
                        .tint(.white)
                }
            }
            .frame(maxWidth: .infinity)
        }
        .buttonStyle(.borderedProminent)
        .controlSize(.large)
        .keyboardShortcut(.defaultAction)
        // Not `.disabled` while busy: a disabled button greys itself out, and a grey button with a
        // grey spinner on it reads as "nothing is happening" — which is the opposite of the truth.
        // It stays prominent and simply stops responding.
        .disabled(!enabled && !busy)
        .allowsHitTesting(!busy)
        .accessibilityLabel(busy ? "Working…" : title)
    }

    // MARK: - Actions

    private func signIn() {
        guard !username.isEmpty, !password.isEmpty else { return }
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
