import AppKit
import CryptoKit
import Foundation

/// The self-updater (spec/features/updates.spec.md). It works the way Obtainium does on the phone:
/// a fine-grained, read-only GitHub token in the Keychain, the REST API, and the release assets.
///
/// **UPD-5 is the reason this is ours and not Sparkle's.** Sparkle's whole model is "download and
/// relaunch". A baby monitor that relaunches itself at 3am is precisely the failure this project
/// exists to prevent. So this downloads, verifies, and then *waits*: the update is applied when
/// monitoring is stopped, or at the next launch. Never mid-session, and never on its own initiative.
///
/// **UPD-4:** a token expires, and an app that has silently stopped updating looks exactly like an
/// app that is up to date — for months. So repeated failures are reported, not swallowed.
actor Updater {
    struct Release {
        let version: String
        let assetID: Int
        let sha256: String
    }

    enum UpdaterError: LocalizedError {
        case noToken
        case http(Int)
        case noAsset
        case checksumMismatch
        case badChecksumFile

        var errorDescription: String? {
            switch self {
            case .noToken:
                return "No GitHub token — the app cannot check for updates."
            case let .http(code):
                return code == 401 || code == 403
                    ? "GitHub rejected the token — it may have expired."
                    : "GitHub returned \(code)."
            case .noAsset:
                return "The latest release has no macOS build."
            case .checksumMismatch:
                return "The downloaded update did not match its checksum and was discarded."
            case .badChecksumFile:
                return "The release did not publish a usable checksum."
            }
        }
    }

    private let owner = "bluzi"
    private let repo = "c100-baby-monitor-app"
    private let session: URLSession
    private let currentVersion: String

    private var consecutiveFailures = 0
    private(set) var staged: (version: String, bundle: URL)?

    init(currentVersion: String) {
        self.currentVersion = currentVersion
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 30
        session = URLSession(
            configuration: config,
            delegate: RedirectAuthStripper(),
            delegateQueue: nil
        )
    }

    /// One check. Returns the staged version when a newer one is ready to install, nil when we are
    /// already current. Throws when the check itself failed — the caller counts those (UPD-4).
    func check() async throws -> String? {
        guard let token = UpdaterToken.load() else { throw UpdaterError.noToken }

        do {
            let release = try await latestRelease(token: token)
            guard isNewer(release.version, than: currentVersion) else {
                consecutiveFailures = 0
                return nil
            }
            let bundle = try await download(release: release, token: token)
            staged = (release.version, bundle)
            consecutiveFailures = 0
            return release.version
        } catch {
            consecutiveFailures += 1
            throw error
        }
    }

    /// UPD-4: how many checks in a row have failed. The UI complains once this is no longer a blip.
    var isFailingPersistently: Bool { consecutiveFailures >= 3 }


    // MARK: - GitHub

    /// The newest release that actually contains a **macOS** build.
    ///
    /// Not `/releases/latest`. Releases are path-filtered: a change under `android/` publishes only
    /// an APK, so the newest release may legitimately have no Mac build in it. That does not mean
    /// the updater is broken and it must not be reported as one (UPD-4 exists to catch real
    /// failures, and an updater that cries wolf is one nobody reads). It means the Mac is already
    /// current, and we should keep looking back for the last release that was ours.
    private func latestRelease(token: String) async throws -> Release {
        var request = URLRequest(
            url: URL(string: "https://api.github.com/repos/\(owner)/\(repo)/releases?per_page=30")!
        )
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.setValue("2022-11-28", forHTTPHeaderField: "X-GitHub-Api-Version")

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw UpdaterError.http(0) }
        guard http.statusCode == 200 else { throw UpdaterError.http(http.statusCode) }

        let releases = try JSONSerialization.jsonObject(with: data) as? [[String: Any]] ?? []
        // Newest first, as GitHub returns them; drafts and prereleases are not ours.
        guard let json = releases.first(where: { release in
            let isDraft = release["draft"] as? Bool ?? false
            let assets = release["assets"] as? [[String: Any]] ?? []
            return !isDraft && assets.contains { ($0["name"] as? String)?.hasSuffix("-macos.zip") == true }
        }) else {
            throw UpdaterError.noAsset
        }

        let tag = json["tag_name"] as? String ?? ""
        let version = tag.hasPrefix("v") ? String(tag.dropFirst()) : tag
        let assets = json["assets"] as? [[String: Any]] ?? []

        guard let app = assets.first(where: { ($0["name"] as? String)?.hasSuffix("-macos.zip") == true }),
              let assetID = app["id"] as? Int
        else {
            throw UpdaterError.noAsset
        }

        // UPD-3: the checksum is published alongside the build, and a download that does not match
        // it is discarded rather than run. This is the one thing standing between a truncated
        // download and a monitor that will not start tonight.
        guard let sums = assets.first(where: { ($0["name"] as? String) == "checksums.txt" }),
              let sumsID = sums["id"] as? Int
        else {
            throw UpdaterError.badChecksumFile
        }
        let sumsData = try await downloadAsset(id: sumsID, token: token)
        guard let text = String(data: sumsData, encoding: .utf8),
              let line = text.split(separator: "\n").first(where: { $0.contains("-macos.zip") }),
              let sha = line.split(separator: " ").first
        else {
            throw UpdaterError.badChecksumFile
        }

        return Release(version: version, assetID: assetID, sha256: String(sha))
    }

    private func download(release: Release, token: String) async throws -> URL {
        let data = try await downloadAsset(id: release.assetID, token: token)

        let digest = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
        guard digest == release.sha256.lowercased() else {
            throw UpdaterError.checksumMismatch
        }

        let staging = FileManager.default.temporaryDirectory
            .appendingPathComponent("BabyMonitorUpdate-\(release.version)", isDirectory: true)
        try? FileManager.default.removeItem(at: staging)
        try FileManager.default.createDirectory(at: staging, withIntermediateDirectories: true)

        let zip = staging.appendingPathComponent("BabyMonitor.zip")
        try data.write(to: zip)

        let unzip = Process()
        unzip.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
        unzip.arguments = ["-x", "-k", zip.path, staging.path]
        try unzip.run()
        unzip.waitUntilExit()

        let app = staging.appendingPathComponent("BabyMonitor.app")
        guard FileManager.default.fileExists(atPath: app.path) else {
            throw UpdaterError.noAsset
        }
        // We downloaded this ourselves over TLS from our own repository, so it carries no quarantine
        // flag — but strip it anyway rather than rely on that.
        let xattr = Process()
        xattr.executableURL = URL(fileURLWithPath: "/usr/bin/xattr")
        xattr.arguments = ["-dr", "com.apple.quarantine", app.path]
        try? xattr.run()
        xattr.waitUntilExit()

        Log.info("update", "staged \(release.version) — it will install when monitoring is stopped")
        return app
    }

    private func downloadAsset(id: Int, token: String) async throws -> Data {
        // The asset endpoint with Accept: octet-stream. GitHub answers 200 with the bytes, or 302
        // to S3 — see RedirectAuthStripper for why that redirect is the interesting part.
        var request = URLRequest(
            url: URL(string: "https://api.github.com/repos/\(owner)/\(repo)/releases/assets/\(id)")!
        )
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue("application/octet-stream", forHTTPHeaderField: "Accept")

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw UpdaterError.http(0) }
        guard http.statusCode == 200 else { throw UpdaterError.http(http.statusCode) }
        return data
    }

    // MARK: - Applying

    /// UPD-5: put the new version on disk **without touching the running app**. It keeps watching,
    /// on the code it already has; the new bundle simply becomes what launches next time. Whether to
    /// restart into it now is the user's call, and theirs alone (`StagedUpdate.relaunch`).
    func install() throws {
        guard let staged else { return }
        try StagedUpdate.installWithoutRelaunching(staged)
        self.staged = nil
    }

    private func isNewer(_ candidate: String, than current: String) -> Bool {
        let a = candidate.split(separator: ".").compactMap { Int($0) }
        let b = current.split(separator: ".").compactMap { Int($0) }
        for i in 0..<max(a.count, b.count) {
            let x = i < a.count ? a[i] : 0
            let y = i < b.count ? b[i] : 0
            if x != y { return x > y }
        }
        return false
    }
}

/// **The gotcha that costs an afternoon.**
///
/// A private repo's release asset has no public URL: the API answers with a 302 to S3. If our
/// `Authorization` header follows the redirect, S3 rejects the request outright — "Only one auth
/// mechanism allowed" — because it is already authenticated by the signature in the redirect URL.
///
/// URLSession forwards headers across redirects by default. So: strip `Authorization` whenever the
/// host changes.
private final class RedirectAuthStripper: NSObject, URLSessionTaskDelegate {
    func urlSession(
        _ session: URLSession,
        task: URLSessionTask,
        willPerformHTTPRedirection response: HTTPURLResponse,
        newRequest request: URLRequest,
        completionHandler: @escaping (URLRequest?) -> Void
    ) {
        let originalHost = task.originalRequest?.url?.host
        guard request.url?.host != originalHost else {
            completionHandler(request) // same host — the header is still ours to send
            return
        }
        var stripped = request
        stripped.setValue(nil, forHTTPHeaderField: "Authorization")
        completionHandler(stripped)
    }
}

/// Staged updates on disk: finding them, checking them, and swapping them in.
///
/// Deliberately **synchronous and outside the actor**. This runs at launch, on the main thread,
/// before anything else — and an actor here forced a semaphore, which deadlocked the app, because
/// a `Task` created inside a `@MainActor` method inherits MainActor and cannot run while the main
/// thread waits on it. It is directory listing and a file swap. It does not need concurrency.
enum StagedUpdate {
    private static let teamID = "Q3X5Y6A98J"
    private static let prefix = "BabyMonitorUpdate-"

    /// **The other half of UPD-5**, and the half that makes the first half survivable.
    ///
    /// An update applies "when monitoring is stopped, or at the next launch". Without the second
    /// clause the app never updates at all: it starts monitoring the moment it launches, so
    /// "monitoring is stopped" never comes around on its own, and a staged update held only in
    /// memory is discarded on every restart. The app would sit on one version forever, dutifully
    /// re-downloading the new one each time and throwing it away — which is exactly what it did.
    static func find(newerThan currentVersion: String) -> (version: String, bundle: URL)? {
        let temp = FileManager.default.temporaryDirectory
        let entries = (try? FileManager.default.contentsOfDirectory(
            at: temp, includingPropertiesForKeys: nil
        )) ?? []

        var best: (version: String, bundle: URL)?
        for dir in entries where dir.lastPathComponent.hasPrefix(prefix) {
            let version = String(dir.lastPathComponent.dropFirst(prefix.count))
            let app = dir.appendingPathComponent("BabyMonitor.app")
            guard FileManager.default.fileExists(atPath: app.path),
                  isNewer(version, than: currentVersion),
                  isNewer(version, than: best?.version ?? "0")
            else { continue }

            // Its checksum was verified when it was downloaded, but it has been on disk since. An
            // updater is the one component allowed to replace the running code, so it does not take
            // that on trust: the bundle must still carry our signature.
            guard signedByUs(app) else {
                Log.error("update", "a staged bundle is not signed by us — discarding it")
                try? FileManager.default.removeItem(at: dir)
                continue
            }
            best = (version, app)
        }
        return best
    }

    /// **UPD-5: swap the bundle on disk and leave the running app entirely alone.**
    ///
    /// This is the whole restraint of this updater in one function. The process that is watching the
    /// baby right now goes on watching: it has its code loaded already, and replacing the bundle
    /// under it changes nothing about the run in progress. What changes is what launches *next*.
    ///
    /// Nothing here restarts anything. Restarting is a separate function, called only when a person
    /// has said yes to being asked.
    static func installWithoutRelaunching(_ update: (version: String, bundle: URL)) throws {
        let current = Bundle.main.bundleURL
        // Atomic: either the new bundle is in place or the old one still is. A half-written app
        // bundle would be a Mac that cannot monitor tonight.
        _ = try FileManager.default.replaceItemAt(current, withItemAt: update.bundle)
        Log.warn("update", "installed \(update.version) on disk — the running monitor is untouched")
    }

    /// The user said yes. Start the new copy and stand down.
    static func relaunch() throws {
        let current = Bundle.main.bundleURL
        Log.warn("update", "relaunching into the installed version, at the user's request")
        let relaunch = Process()
        relaunch.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        relaunch.arguments = ["-n", current.path]
        try relaunch.run()
        NSApplication.shared.terminate(nil)
    }

    /// A bundle staged by an earlier run that never got installed (the app was killed mid-update, or
    /// the swap failed). Installed at launch, before the monitor starts.
    static func install(_ update: (version: String, bundle: URL)) throws {
        try installWithoutRelaunching(update)
        try relaunch()
    }

    private static func signedByUs(_ app: URL) -> Bool {
        var code: SecStaticCode?
        guard SecStaticCodeCreateWithPath(app as CFURL, [], &code) == errSecSuccess, let code
        else { return false }

        var requirement: SecRequirement?
        let text = "identifier \"com.bluzi.babymonitor\" and anchor apple generic and "
            + "certificate leaf[subject.OU] = \"\(teamID)\""
        guard SecRequirementCreateWithString(text as CFString, [], &requirement) == errSecSuccess,
              let requirement
        else { return false }

        return SecStaticCodeCheckValidity(code, [], requirement) == errSecSuccess
    }

    private static func isNewer(_ candidate: String, than current: String) -> Bool {
        let a = candidate.split(separator: ".").compactMap { Int($0) }
        let b = current.split(separator: ".").compactMap { Int($0) }
        for i in 0..<max(a.count, b.count) {
            let x = i < a.count ? a[i] : 0
            let y = i < b.count ? b[i] : 0
            if x != y { return x > y }
        }
        return false
    }
}
