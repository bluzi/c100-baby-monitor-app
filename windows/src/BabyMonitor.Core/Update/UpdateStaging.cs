using System.IO.Compression;
using System.Security.Cryptography;
using BabyMonitor.Core.Logging;

namespace BabyMonitor.Core.Update;

/// <summary>A verified update, on disk, waiting for the next launch (UPD-7, UPD-10).</summary>
public sealed record StagedUpdate(string Version, string ZipPath);

/// <summary>
/// Where a downloaded update waits, and what it takes to trust it later.
///
/// **The failure this class exists to prevent.** The obvious design — extract the new app into a
/// folder, and at the next launch run the exe you find there — has a hole a power cut walks straight
/// through. Extraction is not atomic: kill it halfway and `BabyMonitor.exe` is on disk while half the
/// runtime is not. The next launch finds an exe, hands over to it, and exits; the exe dies instantly
/// for want of its runtime. Nothing is running. And it happens again at *every* launch after that,
/// including the one after each reboot. A baby monitor that is permanently gone, that relaunches into
/// nothing, and that never says why.
///
/// So what waits on disk is **the verified zip, not an extracted tree**, written under a temporary
/// name and *renamed* into place — a rename is atomic, so a half-written download is never visible as
/// a staged update. The tree is produced fresh, from that zip, in the moment before it is used, and
/// the zip's digest is checked **again** first: it was verified when it was downloaded, but it has
/// been sitting on disk since, and the one component allowed to replace the running code does not
/// take that on trust. (The Mac's updater re-checks its code signature at the same point, for the
/// same reason.)
/// </summary>
public sealed class UpdateStaging
{
    private const string ZipExtension = ".zip";
    private const string DigestExtension = ".sha256";
    private const string PendingSuffix = ".incoming";

    private readonly string _root;

    public UpdateStaging(string root) => _root = root;

    /// <summary>
    /// Put a downloaded, checksum-verified zip where the next launch will find it.
    ///
    /// Written as `<version>.zip.incoming` and renamed to `<version>.zip` only once every byte is
    /// down. A crash before the rename leaves rubbish that <see cref="Find"/> ignores and
    /// <see cref="Clean"/> sweeps; it can never leave a *half* update that looks whole.
    /// </summary>
    public StagedUpdate Stage(byte[] zip, string version, string sha256)
    {
        var digest = Digest(zip);
        if (!string.Equals(digest, sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException("The downloaded update did not match its checksum and was discarded.");
        }

        Directory.CreateDirectory(_root);

        var zipPath = Path.Combine(_root, version + ZipExtension);
        var pending = zipPath + PendingSuffix;

        File.WriteAllBytes(pending, zip);
        File.Move(pending, zipPath, overwrite: true);
        File.WriteAllText(Path.Combine(_root, version + DigestExtension), digest);

        Log.I("update", $"staged {version} — it takes over at the next launch");
        return new StagedUpdate(version, zipPath);
    }

    /// <summary>
    /// The newest staged update that is genuinely newer than us, if any.
    ///
    /// Cheap on purpose — it runs at every launch, and hashing tens of megabytes to answer "is there
    /// anything to do?" would delay the monitor for nothing. The hashing happens in
    /// <see cref="Open"/>, which only runs when the answer is yes.
    /// </summary>
    public StagedUpdate? Find(string currentVersion)
    {
        try
        {
            if (!Directory.Exists(_root))
            {
                return null;
            }

            StagedUpdate? best = null;
            foreach (var zip in Directory.GetFiles(_root, "*" + ZipExtension))
            {
                var version = Path.GetFileNameWithoutExtension(zip);
                if (!UpdateRules.IsNewer(version, currentVersion) ||
                    !File.Exists(Path.Combine(_root, version + DigestExtension)) ||
                    (best != null && !UpdateRules.IsNewer(version, best.Version)))
                {
                    continue;
                }

                best = new StagedUpdate(version, zip);
            }

            return best;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.W("update", $"could not look for a staged update: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Re-verify the staged zip and lay it out as a runnable app, returning the directory the new
    /// `BabyMonitor.exe` lives in.
    ///
    /// Everything is extracted under a temporary name and renamed into place, so an interrupted
    /// extraction is never mistaken for a finished one — and the zip's digest is re-checked before a
    /// byte of it is trusted. A staged update that no longer matches its digest is **deleted**, not
    /// run: bit-rot, a truncated write, or anything else that got at it since it was downloaded ends
    /// here rather than in a monitor that will not start tonight.
    /// </summary>
    public string Open(StagedUpdate staged)
    {
        var expected = File.ReadAllText(Path.Combine(_root, staged.Version + DigestExtension)).Trim();
        var zip = File.ReadAllBytes(staged.ZipPath);

        if (!string.Equals(Digest(zip), expected, StringComparison.OrdinalIgnoreCase))
        {
            Discard(staged.Version);
            throw new UpdateException(
                $"The staged update {staged.Version} no longer matches its checksum — it was discarded.");
        }

        var final = Path.Combine(_root, staged.Version);
        var pending = final + PendingSuffix;

        Delete(pending);
        Delete(final);

        Directory.CreateDirectory(pending);
        ZipFile.ExtractToDirectory(staged.ZipPath, pending, overwriteFiles: true);

        // The zip holds the app at its root or one folder down; either way, find the exe and treat
        // its directory as the app. If there isn't one, this is not an app and it never becomes one.
        var exe = Directory.GetFiles(pending, "BabyMonitor.exe", SearchOption.AllDirectories).FirstOrDefault();
        if (exe == null)
        {
            Delete(pending);
            Discard(staged.Version);
            throw new UpdateException($"The staged update {staged.Version} has no BabyMonitor.exe in it.");
        }

        Directory.Move(pending, final);
        return Path.GetDirectoryName(Path.Combine(final, Path.GetRelativePath(pending, exe)))!;
    }

    /// <summary>
    /// Throw away everything this version has already outgrown — extracted trees, zips, digests, and
    /// any `.incoming` rubbish a crash left behind. A monitor kept running for a year would otherwise
    /// accumulate a year of copies of itself, and a full disk is a stopped monitor.
    /// </summary>
    public void Clean(string currentVersion)
    {
        try
        {
            if (!Directory.Exists(_root))
            {
                return;
            }

            foreach (var path in Directory.GetFileSystemEntries(_root))
            {
                var name = Path.GetFileName(path);
                var version = VersionOf(name);

                // Anything still ahead of us is waiting to be installed. Everything else is disk —
                // including any `.incoming` rubbish, whatever version it claims, since a half-written
                // thing is never a real staged update.
                if (UpdateRules.IsNewer(version, currentVersion) &&
                    !name.EndsWith(PendingSuffix, StringComparison.Ordinal))
                {
                    continue;
                }

                Delete(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.W("update", $"could not tidy the staging folder: {e.Message}");
        }
    }

    /// <summary>
    /// Replace the installed app with the one at <paramref name="from"/>, atomically enough that an
    /// interrupted swap cannot leave a monitor that will not start.
    ///
    /// The old directory is *renamed* aside (an atomic move), the new tree is copied into a now-empty
    /// install directory, and only then is the old one deleted. The copy — rather than a second rename —
    /// is deliberate: staging and the install may sit on different volumes, where a rename would fail.
    /// What matters for safety is the rename-aside: it means the old app is never overwritten in place,
    /// so if the copy is interrupted, the install directory is not a mixture of two builds with no way
    /// back — the rollback deletes the half-copy and renames the old one back. Starting from an empty
    /// directory also removes files the new version dropped, which copying over a live install would
    /// leave behind.
    /// </summary>
    public static void Swap(string from, string to)
    {
        // Validate the source *before* touching the installed app. If the new build's tree is not
        // there (a cleaner got it, a disk gave up), there is nothing to swap in — and we must not have
        // moved the old app aside, or the guard below would be racing an install directory that
        // CopyTree has already half-recreated.
        if (!Directory.Exists(from))
        {
            throw new UpdateException($"the new version is not on disk at {from}");
        }

        var aside = to.TrimEnd(Path.DirectorySeparatorChar) + ".old";
        Delete(aside);

        var moved = false;
        try
        {
            if (Directory.Exists(to))
            {
                Directory.Move(to, aside);
                moved = true;
            }

            CopyTree(from, to);
        }
        catch (Exception)
        {
            // Put the old app back. An update that cannot be applied must never cost a night of
            // monitoring — it costs a version, and nothing else.
            if (moved)
            {
                try
                {
                    Delete(to); // clear whatever half-copy is there, so the rename lands
                    Directory.Move(aside, to);
                    Log.W("update", "the swap failed — the previous version was put back");
                }
                catch (Exception e)
                {
                    Log.E("update", $"the swap failed AND the previous version could not be restored: {e.Message}", e);
                }
            }

            throw;
        }

        Delete(aside);
    }

    /// <summary>
    /// The version a staging entry names, whatever form it takes: `0.1.43.zip`, `0.1.43.sha256`,
    /// `0.1.43` (an extracted tree), `0.1.43.incoming`, `0.1.43.zip.incoming`. NOT
    /// <c>Path.GetFileNameWithoutExtension</c>, which treats the last dotted segment of a bare version
    /// as an extension and turns `0.1.43` into `0.1` — wrong on exactly the strings this deals in. We
    /// strip the suffixes we actually append, and nothing else.
    /// </summary>
    private static string VersionOf(string name)
    {
        if (name.EndsWith(PendingSuffix, StringComparison.Ordinal))
        {
            name = name[..^PendingSuffix.Length];
        }

        if (name.EndsWith(ZipExtension, StringComparison.Ordinal))
        {
            name = name[..^ZipExtension.Length];
        }
        else if (name.EndsWith(DigestExtension, StringComparison.Ordinal))
        {
            name = name[..^DigestExtension.Length];
        }

        return name;
    }

    private static string Digest(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var source in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }
    }

    private static void Delete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.W("update", $"could not remove {path}: {e.Message}");
        }
    }

    /// <summary>
    /// Forget a staged version — its zip, its digest, and any extracted tree. Called when the swap into
    /// it failed: deleting at least the zip and digest makes <see cref="Find"/> stop offering it, which
    /// is what stops the app relaunching into the same doomed swap at every launch and never reaching
    /// the monitor. The extracted tree may be locked (the failed swap could be running from it) — that
    /// delete is allowed to fail; losing the digest alone is enough.
    /// </summary>
    public void Discard(string version)
    {
        Delete(Path.Combine(_root, version + ZipExtension));
        Delete(Path.Combine(_root, version + DigestExtension));
        Delete(Path.Combine(_root, version));
    }
}

public sealed class UpdateException : Exception
{
    public UpdateException(string message)
        : base(message)
    {
    }
}
