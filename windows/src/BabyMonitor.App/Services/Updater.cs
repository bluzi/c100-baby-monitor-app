using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using BabyMonitor.Core.Update;
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
/// The self-updater (spec/features/updates.spec.md). It works the way the Mac's does: the public
/// repository's REST API and its release assets — no credential, nothing to set up.
///
/// The *decisions* — which asset is ours, which line of the checksum file to read, which version is
/// newer — and everything that touches the disk live in <see cref="BabyMonitor.Core.Update"/>, where
/// the spec suite runs them on every machine. What is left here is the part that genuinely needs the
/// network and the process: talk to GitHub, download bytes, hand over to the new build.
///
/// **UPD-5 is the whole design.** It downloads, verifies, and then *waits*: the update is applied when
/// monitoring is stopped, or at the next launch. Never mid-session, and never on its own initiative.
///
/// **UPD-4:** a check can fail — GitHub unreachable, an API error, a download that will not verify —
/// and an app that has silently stopped updating looks exactly like one that is up to date, for
/// months. So a failed check is reported, not swallowed.
/// </summary>
public sealed class Updater : IDisposable
{
    private const string Owner = "bluzi";
    private const string Repo = "c100-baby-monitor-app";

    private readonly HttpClient _http;
    private readonly string _currentVersion;
    private readonly UpdateStaging _staging = new(AppPaths.StagingRoot);

    public Updater(string currentVersion)
    {
        _currentVersion = currentVersion;

        // A release asset is a 302 to GitHub's CDN. We follow redirects ourselves (AllowAutoRedirect
        // is off) so each hop gets its own inactivity budget below — and on a public repo there is no
        // credential to carry across hosts in the first place.
        //
        // No total Timeout: HttpClient.Timeout bounds the *whole* operation, download included, so a
        // five-minute cap would fail a large update on a slow line — and then fail it again at every
        // launch, so a house with slow broadband would silently never update. The per-request budgets
        // below are inactivity timeouts, which is what URLSession gives the Mac for free.
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BabyMonitor", currentVersion));
    }

    /// <summary>The version that has been downloaded, verified and laid out ready to install, if any.</summary>
    public StagedUpdate? Staged { get; private set; }

    /// <summary>
    /// The newer release <see cref="CheckAsync"/> found and <see cref="DownloadAndStageAsync"/> will
    /// fetch when the parent accepts it. Detection and download are deliberately separate: at launch the
    /// app must be able to *ask* about an update before showing a window, without paying for a
    /// tens-of-megabytes download the parent may decline (UPD-5).
    /// </summary>
    private Release? _pending;

    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>
    /// One check — **detection only**. Returns the version of a newer release, or null when we are
    /// already current. Nothing large is downloaded here: only the release list and its small checksum
    /// file. The update itself is fetched only if the parent accepts it, in
    /// <see cref="DownloadAndStageAsync"/> (UPD-5) — so the launch check can put its question on screen
    /// before a window, without paying for a download that may be declined. Throws when the check
    /// itself failed — the caller reports those (UPD-4).
    /// </summary>
    public async Task<string?> CheckAsync(CancellationToken ct = default)
    {
        if (!UpdateRules.CanCheck(_currentVersion))
        {
            // A dev build (0.0.0) is older than every release; checking would "find an update" every
            // time and offer to copy CI's latest over the tree you are building.
            _pending = null;
            return null;
        }

        _pending = await LatestReleaseAsync(ct).ConfigureAwait(false);
        return _pending?.Version; // null when no release newer than us carries a Windows build
    }

    /// <summary>
    /// UPD-5: the parent accepted the update — now download it, verify it against the published
    /// checksum, and lay it out ready to install (<see cref="Staged"/>). Only meaningful after
    /// <see cref="CheckAsync"/> has found a newer release. Throws on a network or verification failure;
    /// the caller reports it and stays on the current version.
    /// </summary>
    public async Task DownloadAndStageAsync(CancellationToken ct = default)
    {
        if (_pending is not { } release)
        {
            return;
        }

        var zip = await DownloadAssetAsync(release.AssetId, ct).ConfigureAwait(false);
        Staged = await Task.Run(() => _staging.Stage(zip, release.Version, release.Sha256), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// UPD-10, and it must run before anything else exists — before the engine, before the tray, and
    /// above all before the monitor connects to a camera it would have to drop again to relaunch.
    ///
    /// A version the last run staged is re-verified, laid out, and handed control here. Returns true
    /// when this process has handed over and should now exit.
    /// </summary>
    public static bool ApplyStagedAtLaunch()
    {
        var staging = new UpdateStaging(AppPaths.StagingRoot);
        var staged = staging.Find(CurrentVersion);
        if (staged == null)
        {
            // Nothing newer is waiting, so whatever is left beside us is this version or older: the
            // update landed, and the folders it came out of are just disk now.
            staging.Clean(CurrentVersion);
            return false;
        }

        Log.Warn("update", $"{staged.Version} is waiting — installing it now, before monitoring starts");
        return Install(staged);
    }

    /// <summary>
    /// UPD-5: apply a staged update. Only ever called with monitoring stopped — at launch before the
    /// monitor starts, or when the user has just asked for the restart.
    ///
    /// Windows will not let a running program overwrite itself, so the swap is done **by the new
    /// version**: we re-verify and lay out the staged build, start it with our process id and where we
    /// live, and — only once we have seen it survive its first moment — exit. It waits for us to go,
    /// swaps the files, and starts the installed app again.
    /// </summary>
    public static bool Install(StagedUpdate staged)
    {
        try
        {
            var staging = new UpdateStaging(AppPaths.StagingRoot);
            var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var appDir = staging.Open(staged); // re-verifies the digest; throws (and discards) if it rotted
            var stagedExe = Path.Combine(appDir, "BabyMonitor.exe");

            Log.Warn("update", $"installing {staged.Version} — handing over to the staged build");
            var child = Process.Start(new ProcessStartInfo
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

            // Did the new build actually come up? An unsigned, freshly-downloaded exe under LocalAppData
            // is exactly what Defender quarantines; a bad extract dies the same way. If it is gone
            // within its first second, we never handed over — so stay up and keep monitoring on the old
            // version, which is the stated preference. It will try again next launch.
            if (child != null && child.WaitForExit(3000) && child.ExitCode != 0)
            {
                Log.Error("update", $"the staged build exited immediately (code {child.ExitCode}) — staying on {CurrentVersion}");
                return false;
            }

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
    /// The other side of <see cref="Install"/>: we are the new build, started by the old one. Wait for
    /// it to exit, swap the files, and start it again. Returns true when this process's whole job was
    /// the swap and it should now exit.
    /// </summary>
    public static bool TryApplyStagedUpdate(string[] args)
    {
        if (args.Length < 3 || args[0] != "--apply-update")
        {
            return false;
        }

        var installDir = args[1];

        try
        {
            if (int.TryParse(args[2], System.Globalization.CultureInfo.InvariantCulture, out var oldPid))
            {
                WaitForExit(oldPid);
            }

            UpdateStaging.Swap(AppContext.BaseDirectory, installDir);
            StartInstalled(installDir);
            return true;
        }
        catch (Exception e)
        {
            // The swap rolls itself back, so the old version is still on disk and still works. Two
            // things then have to happen, and the order matters. First DISCARD the staged update:
            // without this, the restarted old build would find it again, try the same swap again, fail
            // again, and relaunch — an infinite loop that never reaches the monitor. Dropping the zip
            // and digest makes the next launch see nothing staged and simply monitor on the old
            // version (the normal check re-downloads it later). Then start the old build again.
            Log.Error("update", $"could not apply the update: {e.Message} — discarding it so it is not retried", e);
            try
            {
                new UpdateStaging(AppPaths.StagingRoot).Discard(CurrentVersion);
            }
            catch (Exception discard)
            {
                Log.Error("update", $"and could not discard the failed update: {discard.Message}", discard);
            }

            StartInstalled(installDir);
            return true;
        }
    }

    public void Dispose() => _http.Dispose();

    private static void StartInstalled(string installDir)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(installDir, "BabyMonitor.exe"),
                UseShellExecute = false,
            });
        }
        catch (Exception e)
        {
            Log.Error("update", $"could not restart the installed build: {e.Message}", e);
        }
    }

    /// <summary>
    /// The newest release that actually contains a **Windows** build, or null when we are current.
    ///
    /// Not `/releases/latest`. Releases are path-filtered: a change under `android/` publishes only an
    /// APK, so the newest release may legitimately have no Windows build in it. That does not mean the
    /// updater is broken and it must not be reported as one — an updater that cries wolf is one nobody
    /// reads.
    ///
    /// So we walk back from newest and **stop at the first release that is not newer than us** — our
    /// own release is necessarily in the list, so reaching it means "already current", not "broken".
    /// That is correct regardless of how many Android/Mac releases sit on top of ours, which a fixed
    /// page count is not: thirty releases of other-platform work is a few weeks, and past that a
    /// count-limited scan would report a false failure and never look further.
    /// </summary>
    private async Task<Release?> LatestReleaseAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=50");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new UpdaterException(HttpFailureMessage((int)response.StatusCode));
        }

        // Through the same inactivity-bounded reader as the asset download — the releases list is small,
        // but a body read that can hang forever is a check that goes quiet, which UPD-4 forbids.
        var json = await ReadBodyAsync(response, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean())
            {
                continue;
            }

            var tag = release.GetProperty("tag_name").GetString() ?? string.Empty;
            var version = tag.StartsWith('v') ? tag[1..] : tag;

            // The list is newest-first, and our own release is in it. The first release that is not
            // strictly newer than us is us (or older): everything past it is older still, so we are
            // current and there is nothing to report.
            if (!UpdateRules.IsNewer(version, _currentVersion))
            {
                return null;
            }

            if (!release.TryGetProperty("assets", out var assets))
            {
                continue;
            }

            var app = assets.EnumerateArray().FirstOrDefault(a =>
                UpdateRules.IsOurAsset(a.GetProperty("name").GetString() ?? string.Empty));
            if (app.ValueKind == JsonValueKind.Undefined)
            {
                continue; // a newer Android-only or Mac-only release: not ours, keep looking back
            }

            var sums = assets.EnumerateArray().FirstOrDefault(a =>
                a.GetProperty("name").GetString() == UpdateRules.ChecksumAsset);
            if (sums.ValueKind == JsonValueKind.Undefined)
            {
                // A newer Windows build with no checksum yet — a half-finished upload. Do not fail the
                // whole check on it (that would be a red warning for a release that is still landing);
                // skip it and take the last good Windows release, which we update to next time anyway.
                Log.Warn("update", $"release {version} has a Windows build but no checksum yet — skipping it");
                continue;
            }

            var sumsText = System.Text.Encoding.UTF8.GetString(
                await DownloadAssetAsync(sums.GetProperty("id").GetInt64(), ct).ConfigureAwait(false));

            var sha = UpdateRules.Sha256For(sumsText);
            if (sha == null)
            {
                Log.Warn("update", $"release {version} published a checksum file with no line for our zip — skipping it");
                continue;
            }

            return new Release(version, app.GetProperty("id").GetInt64(), sha);
        }

        // Everything the page held is newer than us but none of it is a Windows build with a checksum.
        // We are as current as a Windows build can be; not a failure.
        return null;
    }

    private async Task<byte[]> DownloadAssetAsync(long id, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/assets/{id}";

        for (var hop = 0; hop < 5; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

            using var response = await SendAsync(request, ct).ConfigureAwait(false);

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

            return await ReadBodyAsync(response, ct).ConfigureAwait(false);
        }

        throw new UpdaterException("GitHub redirected the download too many times.");
    }

    // Not HttpClient.Timeout (which bounds the whole operation, so a big update on a slow line fails
    // every launch) and not a total per-request cap (a mid-body stall on flaky wifi would still hang
    // for the whole cap). A genuine INACTIVITY budget: as long as bytes keep arriving, the download
    // has as long as it needs; the moment it goes quiet this long, it is a failure the app reports.
    private static readonly TimeSpan Inactivity = TimeSpan.FromMinutes(2);

    /// <summary>Send a request, bounding only the wait for the response *headers* (see SendAsync).</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Inactivity);
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new UpdaterException("GitHub did not respond in time.");
        }
    }

    /// <summary>
    /// Read the whole body, streaming, resetting the inactivity clock on every chunk that arrives. This
    /// is the fix for the download that would otherwise hang forever: `ResponseHeadersRead` means the
    /// body is read *here*, not inside <see cref="SendAsync"/>, so it must carry its own timeout — and a
    /// per-read one, so a slow-but-progressing download is never mistaken for a stalled one.
    /// </summary>
    private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(Inactivity);

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, idle.Token).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                idle.CancelAfter(Inactivity); // progress: give it the full budget again
            }

            return buffer.ToArray();
        }
        catch (OperationCanceledException) when (idle.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new UpdaterException("The download stalled and was abandoned.");
        }
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

    private sealed record Release(string Version, long AssetId, string Sha256);
}

public sealed class UpdaterException : Exception
{
    public UpdaterException(string message)
        : base(message)
    {
    }
}
