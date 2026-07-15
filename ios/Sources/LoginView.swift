import BabyMonitorCore
import SwiftUI

/// AUTH-1/3/4/9/11: sign in to the Mi account that owns the camera. Signing in happens once;
/// afterwards the session persists and refreshes itself.
///
/// The fields are ordinary SwiftUI text fields on purpose (IOS-2): that is what makes a password
/// manager's paste work, what makes the Passwords app offer to fill them, and what makes the keyboard
/// return key go where a parent expects. The one screen that asks for a password should feel like
/// every other iOS sign-in.
struct LoginView: View {
    @EnvironmentObject private var state: AppState
    @State private var username = ""
    @State private var password = ""
    @State private var region = "sg"
    @State private var busy = Preview.busy
    @State private var error: String? = Preview.loginStep == "error" ? "That Mi account or password was not accepted." : nil
    @State private var captcha: UIImage? = Preview.loginStep == "captcha" ? Preview.captchaImage : nil
    @State private var captchaCode = ""
    @State private var codeChannel: String? = Preview.loginStep == "code" ? "phone" : nil
    @State private var codeTarget = Preview.loginStep == "code" ? "•••• 4821" : ""
    @State private var code = ""
    @FocusState private var focus: Field?

    private enum Field { case username, password, captcha, code }

    /// Xiaomi's server codes mean nothing to anyone; the camera is registered in one of these regions,
    /// and picking the wrong one looks exactly like a wrong password — so name them.
    private static let regionNames = [
        "cn": "China", "de": "Europe", "us": "United States",
        "ru": "Russia", "sg": "Singapore", "i2": "India",
    ]

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()
            ScrollView {
                VStack(spacing: 22) {
                    Spacer(minLength: 40)
                    AppMark(size: 76)
                    header
                    fields
                    if let error {
                        Label(error, systemImage: "exclamationmark.circle.fill")
                            .font(.callout)
                            .foregroundStyle(.red)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    actions
                    Spacer(minLength: 20)
                }
                .padding(.horizontal, 24)
                .frame(maxWidth: 520)
                .frame(maxWidth: .infinity)
            }
        }
        .onAppear {
            OrientationLock.set(.portrait)
            if !Preview.active, focus == nil { focus = firstField } // AUTH-11: ready to type
        }
    }

    private var firstField: Field {
        if codeChannel != nil { return .code }
        if captcha != nil { return .captcha }
        return .username
    }

    private var header: some View {
        VStack(spacing: 6) {
            Text(title)
                .font(.title2.bold())
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
            Text(subtitle)
                .font(.subheadline)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private var title: String {
        if codeChannel != nil { return "Enter the code Xiaomi just sent you" }
        if captcha != nil { return "Xiaomi wants to check that you are human" }
        return "Sign in to your Mi account"
    }

    private var subtitle: String {
        if let codeChannel { return "The code was sent to your \(codeChannel) \(codeTarget)." }
        if captcha != nil { return "Type the characters in the picture to carry on." }
        return "The account that owns the camera. Only its session is stored — encrypted, in your Keychain."
    }

    // MARK: - Fields

    @ViewBuilder
    private var fields: some View {
        VStack(spacing: 12) {
            if codeChannel != nil {
                field {
                    TextField("Verification code", text: $code)
                        .keyboardType(.numberPad)
                        .textContentType(.oneTimeCode)
                        .focused($focus, equals: .code)
                }
            } else if let captcha {
                Image(uiImage: captcha)
                    .interpolation(.high)
                    .resizable()
                    .aspectRatio(contentMode: .fit)
                    .frame(height: 56)
                    .frame(maxWidth: .infinity)
                    .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
                field {
                    TextField("Captcha", text: $captchaCode)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .focused($focus, equals: .captcha)
                }
            } else {
                field {
                    TextField("Email, phone or Mi ID", text: $username)
                        .textContentType(.username)
                        .keyboardType(.emailAddress)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .submitLabel(.next)
                        .focused($focus, equals: .username)
                        .onSubmit { focus = .password }
                }
                field {
                    SecureField("Password", text: $password)
                        .textContentType(.password)
                        .submitLabel(.go)
                        .focused($focus, equals: .password)
                        .onSubmit(signIn)
                }
                field {
                    HStack {
                        Text("Region").foregroundStyle(.secondary)
                        Spacer()
                        Picker("Region", selection: $region) {
                            ForEach(BabyMonitor.shared.regions(), id: \.self) { code in
                                Text(Self.regionNames[code] ?? code.uppercased()).tag(code)
                            }
                        }
                        .labelsHidden()
                        .tint(.primary)
                    }
                }
            }
        }
    }

    private func field<Content: View>(@ViewBuilder _ content: () -> Content) -> some View {
        content()
            .padding(.horizontal, 14)
            .padding(.vertical, 13)
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(.white.opacity(0.08)))
            .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(.white.opacity(0.10), lineWidth: 0.5))
    }

    // MARK: - Actions

    private var actions: some View {
        VStack(spacing: 12) {
            Button(action: submit) {
                ZStack {
                    Text(primaryTitle).opacity(busy ? 0 : 1)
                    if busy { ProgressView().tint(.white) }
                }
                .font(.headline)
                .frame(maxWidth: .infinity)
                .frame(height: 52)
            }
            .buttonStyle(.glassProminent)
            .disabled((!canSubmit || busy))

            if captcha != nil || codeChannel != nil {
                Button("Start over", action: reset)
                    .font(.subheadline)
                    .disabled(busy)
            }
        }
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
        busy = true; error = nil
        BabyMonitor.shared.signIn(username: username, password: password, region: region) { handle($0) }
    }

    private func submitCaptcha() {
        guard canSubmit, !busy else { return }
        busy = true; error = nil
        BabyMonitor.shared.submitCaptcha(code: captchaCode) { handle($0) }
    }

    private func submitCode() {
        guard canSubmit, !busy else { return }
        busy = true; error = nil
        BabyMonitor.shared.submitCode(ticket: code) { handle($0) }
    }

    private func handle(_ result: SignInResult) {
        busy = false
        switch result.kind {
        case "ok":
            reset()
        case "captcha":
            // AUTH-3: a rejected code yields a fresh captcha to retry, not a failure.
            captcha = result.captchaImage.flatMap { UIImage(data: $0) }
            captchaCode = ""
            focus = .captcha
        case "code":
            codeChannel = result.channel
            codeTarget = result.maskedTarget ?? ""
            focus = .code
        default:
            error = result.message ?? "Sign-in failed." // AUTH-9: words, never raw JSON
        }
    }

    private func reset() {
        captcha = nil; codeChannel = nil; code = ""; captchaCode = ""
        focus = .username
    }
}
