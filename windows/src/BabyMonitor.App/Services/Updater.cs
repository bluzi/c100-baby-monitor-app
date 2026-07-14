using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Log = BabyMonitor.App.Services.Logging.Log;

namespace BabyMonitor.App.Services;

public enum UpdateState
{
    Idle,
    Checking,

    /// <summary>
    /// UPD-5/7: downloaded, verified, and **already on disk** — the running monitor was never touched.
    /// It takes over at the next launch. The app does not restart itself to get there.
    /// </summary>
    Installed,

    /// <summary>UPD-4: the check failed. The app says so rather than going quiet.</summary>
    Failing,
}

public sealed record UpdateStatus(UpdateState State, string? Version = null, string? Reason = null)
{
    public static readonly UpdateStatus Idle = new(UpdateState.Idle);
}

/// <summary>
/// The self-updater (spec/features/updates.spec.md). It works the way the Mac's does: a fine-grained,
/// read-only GitHub token, the REST API, and the release assets.
///
/// **UPD-5 is the whole design.** It downloads, verifies, and then *waits*: the update is applied when
/// monitoring is stopped, or at the next launch. Never mid-session, and never on its own initiative. A
/// baby monitor that restarts itself at 3am is precisely the failure this project exists to prevent.
///
/// **UPD-4:** a token expires, and an app that has silently stopped updating looks exactly like an app
/// that is up to date — for months. So repeated failures are reported, not swallowed.
/// </summary>
public sealed class Updater : IDisposable
{
    private const string Owner = "bluzi";
    private const string Repo = "c100-baby-monitor-app";
    private const string AssetSuffix = "-windows.zip";
    private const string ChecksumAsset = "checksums-windows.txt";

    private readonly HttpClient _http;
    private readonly string _currentVersion;

    private int _consecutiveFailures;

    public Updater(string currentVersion)
    {
        _currentVersion = currentVersion;

        // The gotcha that costs an afternoon: a private repo's release asset is a 302 to S3, and S3
        // rejects the request outright ("Only one auth mechanism allowed") if our Authorization header
        // follows the redirect. So we follow redirects ourselves and strip the header across hosts.
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(5), // the app is tens of megabytes; a slow line is not a failure
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BabyMonitor", currentVersion));
    }

    /// <summary>The version that is downloaded, verified and waiting (UPD-7), if any.</summary>
    public StagedUpdate? Staged { get; private set; }

    /// <summary>UPD-4: how many checks in a row have failed. The UI complains once this is no longer a blip.</summary>
    public bool IsFailingPersistently => _consecutiveFailures >= 3;

    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>
    /// One check. Returns the staged version when a newer one is ready to install, null when we are
    /// already current. Throws when the check itself failed — the caller counts those (UPD-4).
    /// </summary>
    public async Task<string?> CheckAsync(CancellationToken ct = default)
    {
        var token = UpdaterToken.Load();
        if (token == null)
        {
            // Not a failure — updates simply have not been set up yet, and settings say so. UPD-4 is
            // about an updater that HAS been set up and has quietly stopped working.
            throw new NoTokenException();
        }

        try
        {
            var release = await LatestReleaseAsync(token, ct).ConfigureAwait(false);
            if (!IsNewer(release.Version, _currentVersion))
            {
                _consecutiveFailures = 0;
                return null;
            }

            Staged = await DownloadAsync(release, token, ct).ConfigureAwait(false);
            _consecutiveFailures = 0;
            return release.Version;
        }
        catch (Exception)
        {
            _consecutiveFailures++;
            throw;
        }
    }

    /// <summary>
    /// UPD-5, the "or at the next launch" half — and the half that makes the first half survivable.
    ///
    /// Without it the app would never update at all: it starts monitoring the moment it launches, so
    /// "monitoring is stopped" never comes around on its own, and a staged update held only in memory
    /// would be discarded on every restart. The app would sit on one version forever, dutifully
    /// re-downloading the new one each time and throwing it away.
    /// </summary>
    public static StagedUpdate? FindStaged(string currentVersion)
    {
        try
        {
            if (!Directory.Exists(AppPaths.StagingRoot))
            {
                return null;
            }

            StagedUpdate? best = null;
            foreach (var dir in Directory.GetDirectories(AppPaths.StagingRoot))
            {
                var version = Path.GetFileName(dir);
                var exe = Path.Combine(dir, "BabyMonitor.exe");
                if (!File.Exists(exe) ||
                    !IsNewer(version, currentVersion) ||
                    (best != null && !IsNewer(version, best.Version)))
                {
                    continue;
                }

                best = new StagedUpdate(version, dir);
            }

            return best;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("update", $"could not look for a staged update: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// UPD-5: apply a staged update. Only ever called with monitoring stopped — at launch before the
    /// monitor starts, or once the user has stopped it.
    ///
    /// Windows will not let a running program overwrite itself, so the swap is done **by the new
    /// version**: we start the staged executable, hand it our process id and where we live, and exit.
    /// It waits for us to go, replaces the files, and starts the installed app again.
    /// </summary>
    public static bool Install(StagedUpdate staged)
    {
        try
        {
            var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var stagedExe = Path.Combine(staged.Directory, "BabyMonitor.exe");

            Log.Warn("update", $"installing {staged.Version} — handing over to the staged build and exiting");
            Process.Start(new ProcessStartInfo
            {
                FileName = stagedExe,
                UseShellExecute = false,
                ArgumentList =
                {
                    "--apply-update",
                    installDir,
                    Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
            });
            return true;
        }
        catch (Exception e)
        {
            // A failed install must never keep the monitor from running. An outdated monitor beats no
            // monitor.
            Log.Error("update", $"could not start the staged build: {e.Message}", e);
            return false;
        }
    }

    /// <summary>
    /// Throw away every staged build this version has already outgrown.
    ///
    /// Each update leaves its staging folder behind (the process doing the swap is *running* from it,
    /// so it cannot delete itself). Left alone, a monitor kept running for a year would quietly
    /// accumulate a year of app folders — and a full disk is a stopped monitor, which is the same
    /// reason the log is capped.
    /// </summary>
    public static void CleanStaging(string currentVersion)
    {
        try
        {
            if (!Directory.Exists(AppPaths.StagingRoot))
            {
                return;
            }

            foreach (var dir in Directory.GetDirectories(AppPaths.StagingRoot))
            {
                if (IsNewer(Path.GetFileName(dir), currentVersion))
                {
                    continue; // still ahead of us: it is waiting to be installed
                }

                Directory.Delete(dir, recursive: true);
                Log.Info("update", $"removed the staged build {Path.GetFileName(dir)} — this app is it now");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("update", $"could not tidy the staging folder: {e.Message}");
        }
    }

    /// <summary>
    /// The other side of <see cref="Install"/>: we are the staged build, started by the old one. Wait
    /// for it to exit, replace it, and start it again. Returns true when this process's whole job was
    /// the swap and it should now exit.
    /// </summary>
    public static bool TryApplyStagedUpdate(string[] args)
    {
        if (args.Length < 3 || args[0] != "--apply-update")
        {
            return false;
        }

        var installDir = args[1];
        var oldPid = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);

        try
        {
            WaitForExit(oldPid);
            CopyOver(AppContext.BaseDirectory, installDir);

            var exe = Path.Combine(installDir, "BabyMonitor.exe");
            Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = false });
            return true;
        }
        catch (Exception e)
        {
            // The old version is still on disk and still works. Start it again and leave it alone —
            // an update that cannot be applied must never cost a night of monitoring.
            Log.Error("update", $"could not apply the update: {e.Message}", e);
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(installDir, "BabyMonitor.exe"),
                    UseShellExecute = false,
                });
            }
            catch (Exception restart)
            {
                Log.Error("update", $"and could not restart the installed build: {restart.Message}", restart);
            }

            return true;
        }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// The newest release that actually contains a **Windows** build.
    ///
    /// Not `/releases/latest`. Releases are path-filtered: a change under `android/` publishes only an
    /// APK, so the newest release may legitimately have no Windows build in it. That does not mean the
    /// updater is broken and it must not be reported as one — an updater that cries wolf is one nobody
    /// reads. It means this PC is already current, and we keep looking back for the last release that
    /// was ours.
    /// </summary>
    private async Task<Release> LatestReleaseAsync(string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=30");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new UpdaterException(HttpFailureMessage((int)response.StatusCode));
        }

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean())
            {
                continue;
            }

            if (!release.TryGetProperty("assets", out var assets))
            {
                continue;
            }

            var app = assets.EnumerateArray().FirstOrDefault(a =>
                a.GetProperty("name").GetString()?.EndsWith(AssetSuffix, StringComparison.Ordinal) == true);
            if (app.ValueKind == JsonValueKind.Undefined)
            {
                continue; // an Android-only or Mac-only release: this PC is already current
            }

            // Our own checksum file, not the Mac's: the two release jobs run at the same time, and a
            // shared file would be a race whose loser publishes a build with no checksum — which this
            // updater would then discard (UPD-3) rather than install, forever.
            var sums = assets.EnumerateArray().FirstOrDefault(a =>
                a.GetProperty("name").GetString() == ChecksumAsset);
            if (sums.ValueKind == JsonValueKind.Undefined)
            {
                throw new UpdaterException("The release did not publish a usable checksum.");
            }

            var tag = release.GetProperty("tag_name").GetString() ?? string.Empty;
            var version = tag.StartsWith('v') ? tag[1..] : tag;
            var assetName = app.GetProperty("name").GetString()!;

            // UPD-3: the checksum is published alongside the build, and a download that does not match
            // it is discarded rather than run. It is the one thing standing between a truncated
            // download and a PC that will not monitor tonight.
            var sumsText = System.Text.Encoding.UTF8.GetString(
                await DownloadAssetAsync(sums.GetProperty("id").GetInt64(), token, ct).ConfigureAwait(false));

            var line = sumsText
                .Split('\n')
                .FirstOrDefault(l => l.Contains(AssetSuffix, StringComparison.Ordinal));
            var sha = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (sha == null)
            {
                throw new UpdaterException("The release did not publish a usable checksum.");
            }

            return new Release(version, app.GetProperty("id").GetInt64(), sha, assetName);
        }

        throw new UpdaterException("No release carries a Windows build yet.");
    }

    private async Task<StagedUpdate> DownloadAsync(Release release, string token, CancellationToken ct)
    {
        var data = await DownloadAssetAsync(release.AssetId, token, ct).ConfigureAwait(false);

        var digest = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        if (!string.Equals(digest, release.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdaterException("The downloaded update did not match its checksum and was discarded.");
        }

        var directory = Path.Combine(AppPaths.StagingRoot, release.Version);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        Directory.CreateDirectory(directory);

        var zipPath = Path.Combine(AppPaths.StagingRoot, release.AssetName);
        await File.WriteAllBytesAsync(zipPath, data, ct).ConfigureAwait(false);
        ZipFile.ExtractToDirectory(zipPath, directory, overwriteFiles: true);
        File.Delete(zipPath);

        // The zip may hold the app folder at its root or one level down; find the executable either way.
        var exe = Directory.GetFiles(directory, "BabyMonitor.exe", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new UpdaterException("The downloaded update has no BabyMonitor.exe in it.");

        var root = Path.GetDirectoryName(exe)!;
        Log.Info("update", $"staged {release.Version} — it will install when monitoring is stopped");
        return new StagedUpdate(release.Version, root);
    }

    private async Task<byte[]> DownloadAssetAsync(long id, string token, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/assets/{id}";
        var host = new Uri(url).Host;

        for (var hop = 0; hop < 5; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

            // Only while we are still talking to GitHub. S3 authenticates by signature and rejects a
            // request that also carries a bearer token.
            if (new Uri(url).Host == host)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location != null)
            {
                url = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location.ToString()
                    : new Uri(new Uri(url), response.Headers.Location).ToString();
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new UpdaterException(HttpFailureMessage((int)response.StatusCode));
            }

            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }

        throw new UpdaterException("GitHub redirected the download too many times.");
    }

    private static string HttpFailureMessage(int status) => status is 401 or 403
        ? "GitHub rejected the token — it may have expired."
        : $"GitHub returned {status}.";

    private static void WaitForExit(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.WaitForExit(30_000))
            {
                throw new UpdaterException("the previous version did not exit");
            }
        }
        catch (ArgumentException)
        {
            // It is already gone, which is all we were waiting for.
        }

        // Windows keeps a file lock for a moment after the process is reaped.
        Thread.Sleep(500);
    }

    private static void CopyOver(string from, string to)
    {
        foreach (var source in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(from, source);
            var target = Path.Combine(to, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }
    }

    private static bool IsNewer(string candidate, string current)
    {
        var a = candidate.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var b = current.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y)
            {
                return x > y;
            }
        }

        return false;
    }

    private sealed record Release(string Version, long AssetId, string Sha256, string AssetName);
}

public sealed record StagedUpdate(string Version, string Directory);

public sealed class UpdaterException : Exception
{
    public UpdaterException(string message)
        : base(message)
    {
    }
}

/// <summary>Updates are not set up yet. Not a failure, and never reported as one (UPD-4).</summary>
public sealed class NoTokenException : Exception
{
    public NoTokenException()
        : base("No GitHub token — the app cannot check for updates.")
    {
    }
}
