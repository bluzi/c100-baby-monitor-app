using System.IO.Compression;
using System.Security.Cryptography;
using BabyMonitor.Core.Update;
using Xunit;

namespace BabyMonitor.Core.Tests;

/// <summary>
/// The updater, which is the one component allowed to replace the running code — and therefore the
/// one that can take the monitor down at the exact moment it is needed.
///
/// These run the real <see cref="UpdateRules"/> and the real <see cref="UpdateStaging"/>, on a real
/// filesystem, on whatever machine you are sitting at: none of it needs Windows, which is precisely
/// why none of it is allowed to live where only Windows could test it. The Windows-only halves — the
/// handover to the new process, and the setup — are checklist steps D10, D10a and D11.
/// </summary>
public class UpdateChainTest : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bm-update-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    // --- which release, and which file in it (UPD-3) --------------------------

    [Theory(DisplayName = "UPD-3 the updater takes the zip — never the setup, never the Mac's or the phone's")]
    [InlineData("babymonitor-v0.1.42-windows.zip", true)]
    [InlineData("babymonitor-v0.1.42-windows-setup.exe", false)] // the first install, and nothing else
    [InlineData("babymonitor-v0.1.42-macos.zip", false)]
    [InlineData("babymonitor-v0.1.42.apk", false)]
    [InlineData("checksums-windows.txt", false)]
    public void ItPicksItsOwnAsset(string assetName, bool isOurs) =>
        Assert.Equal(isOurs, UpdateRules.IsOurAsset(assetName));

    [Fact(DisplayName = "UPD-3 the checksum comes from our line of our checksum file")]
    public void ItReadsItsOwnChecksum()
    {
        // The Mac's job writes `checksums.txt` at the same moment; reading it would be a race whose
        // loser never updates again.
        Assert.NotEqual("checksums.txt", UpdateRules.ChecksumAsset);

        // And the setup's line sits right beside ours. Taking the wrong one means every download
        // "fails its checksum" and is thrown away — silently, and for good.
        const string file = "9f2c1b  babymonitor-v0.1.42-windows-setup.exe\r\n" +
                            "ab3f07  babymonitor-v0.1.42-windows.zip\r\n";

        Assert.Equal("ab3f07", UpdateRules.Sha256For(file));
        Assert.Null(UpdateRules.Sha256For("9f2c1b  babymonitor-v0.1.42-windows-setup.exe"));
    }

    [Theory(DisplayName = "UPD-10 only a newer version takes over — never an equal one, or the app hands over to itself forever")]
    [InlineData("0.1.43", "0.1.42", true)]
    [InlineData("0.1.42", "0.1.42", false)]  // the version we already are: applying it would loop, every launch
    [InlineData("0.1.41", "0.1.42", false)]  // older: a downgrade is never an update
    [InlineData("0.1.100", "0.1.99", true)]  // not a string compare: "100" sorts before "99"
    [InlineData("0.2.0", "0.1.99", true)]
    public void OnlyNewerVersionsAreApplied(string candidate, string current, bool expected) =>
        Assert.Equal(expected, UpdateRules.IsNewer(candidate, current));

    [Fact(DisplayName = "UPD-3 a dev build never checks — it would treat every release as an update")]
    public void ADevBuildDoesNotCheck()
    {
        Assert.False(UpdateRules.CanCheck("0.0.0"));
        Assert.True(UpdateRules.CanCheck("0.1.42"));
    }

    // --- what waits on disk, and what it takes to trust it (UPD-10) -----------

    [Fact(DisplayName = "UPD-10 a verified update waits on disk and opens at the next launch")]
    public void AStagedUpdateIsFoundAndOpened()
    {
        var staging = new UpdateStaging(_root);
        var zip = AppZip("0.1.43");

        staging.Stage(zip, "0.1.43", Sha(zip));

        var found = staging.Find("0.1.42");
        Assert.NotNull(found);
        Assert.Equal("0.1.43", found!.Version);

        var appDir = staging.Open(found);
        Assert.True(File.Exists(Path.Combine(appDir, "BabyMonitor.exe")));
        Assert.True(File.Exists(Path.Combine(appDir, "runtime.dll")));
    }

    [Fact(DisplayName = "UPD-3 a download that does not match its checksum is discarded, not staged")]
    public void ABadDownloadIsNeverStaged()
    {
        var staging = new UpdateStaging(_root);

        Assert.Throws<UpdateException>(() => staging.Stage(AppZip("0.1.43"), "0.1.43", Sha([1, 2, 3])));
        Assert.Null(staging.Find("0.1.42"));
    }

    [Fact(DisplayName = "UPD-10 a staged update that rotted on disk is discarded rather than run")]
    public void ARottedStagedUpdateIsDiscarded()
    {
        // It was verified when it was downloaded. It has been sitting on a disk since — through a
        // power cut, an antivirus, a bad sector. The one component allowed to replace the running code
        // does not take that on trust.
        var staging = new UpdateStaging(_root);
        var zip = AppZip("0.1.43");
        var staged = staging.Stage(zip, "0.1.43", Sha(zip));

        File.WriteAllBytes(staged.ZipPath, [0xDE, 0xAD, 0xBE, 0xEF]);

        Assert.Throws<UpdateException>(() => staging.Open(staged));
        Assert.Null(staging.Find("0.1.42")); // and it is gone, so the next launch does not trip over it
    }

    [Fact(DisplayName = "UPD-10 a half-written download is never visible as a staged update")]
    public void APartialDownloadIsNotAnUpdate()
    {
        // The power cut mid-write. What is on disk is rubbish under a temporary name; what `Find`
        // looks for is the finished article. Without the rename-into-place, the next launch would hand
        // the monitor over to a broken build — and do it again after every reboot, forever.
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "0.1.43.zip.incoming"), [0x50, 0x4B]);

        var staging = new UpdateStaging(_root);
        Assert.Null(staging.Find("0.1.42"));

        // And the same for a zip with no digest beside it — it was never vouched for.
        File.WriteAllBytes(Path.Combine(_root, "0.1.44.zip"), AppZip("0.1.44"));
        Assert.Null(staging.Find("0.1.42"));
    }

    [Fact(DisplayName = "UPD-10 an update with no app in it never becomes one")]
    public void AZipWithNoAppIsRejected()
    {
        var staging = new UpdateStaging(_root);
        var zip = ZipOf(("readme.txt", "not an app"));
        var staged = staging.Stage(zip, "0.1.43", Sha(zip));

        Assert.Throws<UpdateException>(() => staging.Open(staged));
        Assert.Null(staging.Find("0.1.42"));
    }

    [Fact(DisplayName = "UPD-10 the staging folder is tidied once we ARE that version — a full disk is a stopped monitor")]
    public void StagingIsTidiedOnceApplied()
    {
        var staging = new UpdateStaging(_root);
        foreach (var version in new[] { "0.1.41", "0.1.42", "0.1.43" })
        {
            var zip = AppZip(version);
            staging.Stage(zip, version, Sha(zip));
        }

        Directory.CreateDirectory(Path.Combine(_root, "0.1.40.zip.incoming")); // crash rubbish

        staging.Clean("0.1.42"); // we are 0.1.42 now

        Assert.False(File.Exists(Path.Combine(_root, "0.1.41.zip")));
        Assert.False(File.Exists(Path.Combine(_root, "0.1.42.zip")));
        Assert.False(Directory.Exists(Path.Combine(_root, "0.1.40.zip.incoming")));
        Assert.True(File.Exists(Path.Combine(_root, "0.1.43.zip"))); // still ahead of us: it waits
        Assert.Equal("0.1.43", staging.Find("0.1.42")?.Version);
    }

    [Fact(DisplayName = "UPD-10 Clean sweeps an extracted tree too — a bare dotted version is not misread")]
    public void CleanRemovesTheExtractedTree()
    {
        // Open leaves an extracted directory named for the version — a bare `0.1.43`, whose last
        // segment a naive parse would read as a file extension (`0.1.43` -> `0.1`) and mis-file. It
        // must be recognised as 0.1.43 and, once we ARE 0.1.43, swept with the zip and the digest —
        // otherwise a whole app tree is left on disk after every update.
        var staging = new UpdateStaging(_root);
        var zip = AppZip("0.1.43");
        var staged = staging.Stage(zip, "0.1.43", Sha(zip));
        staging.Open(staged); // extracts the tree into <root>/0.1.43

        Assert.True(Directory.Exists(Path.Combine(_root, "0.1.43"))); // the tree is there

        staging.Clean("0.1.43"); // we are 0.1.43 now — every trace of it is just disk

        Assert.False(Directory.Exists(Path.Combine(_root, "0.1.43")));
        Assert.False(File.Exists(Path.Combine(_root, "0.1.43.zip")));
        Assert.False(File.Exists(Path.Combine(_root, "0.1.43.sha256")));
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    // --- the swap itself (UPD-10) --------------------------------------------

    [Fact(DisplayName = "UPD-10 the swap replaces the install whole — including removing what the new version dropped")]
    public void TheSwapReplacesTheInstall()
    {
        var install = Path.Combine(_root, "install");
        var fresh = Path.Combine(_root, "fresh");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(fresh);
        File.WriteAllText(Path.Combine(install, "BabyMonitor.exe"), "old");
        File.WriteAllText(Path.Combine(install, "dropped.dll"), "a file the new version no longer ships");
        File.WriteAllText(Path.Combine(fresh, "BabyMonitor.exe"), "new");

        UpdateStaging.Swap(fresh, install);

        Assert.Equal("new", File.ReadAllText(Path.Combine(install, "BabyMonitor.exe")));
        Assert.False(File.Exists(Path.Combine(install, "dropped.dll")));
        Assert.False(Directory.Exists(install + ".old")); // and it tidied up after itself
    }

    [Fact(DisplayName = "UPD-10 a swap that fails puts the old app back — an update never costs a night of monitoring")]
    public void AFailedSwapRollsBack()
    {
        var install = Path.Combine(_root, "install");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "BabyMonitor.exe"), "old");

        // The new build's directory is not there (a cleaner got it, a disk gave up). The old app must
        // still be on disk and still runnable when this returns.
        Assert.ThrowsAny<Exception>(() => UpdateStaging.Swap(Path.Combine(_root, "gone"), install));

        Assert.True(File.Exists(Path.Combine(install, "BabyMonitor.exe")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(install, "BabyMonitor.exe")));
    }

    [Fact(DisplayName = "UPD-10 discarding a failed update stops it being retried — the loop that never monitors")]
    public void DiscardStopsAFailedUpdateRepeating()
    {
        // When a swap fails, the failed version must be forgotten, or the next launch finds it, tries
        // the same swap, fails, relaunches — forever, never reaching the monitor. After Discard, Find
        // offers nothing, so the app simply monitors on the version it has.
        var staging = new UpdateStaging(_root);
        var zip = AppZip("0.1.43");
        staging.Stage(zip, "0.1.43", Sha(zip));
        Assert.NotNull(staging.Find("0.1.42"));

        staging.Discard("0.1.43");

        Assert.Null(staging.Find("0.1.42"));
    }

    // --- fixtures -------------------------------------------------------------

    /// <summary>A zip shaped like the real one: the app at the root, with a runtime beside it.</summary>
    private static byte[] AppZip(string version) => ZipOf(
        ("BabyMonitor.exe", $"the app, version {version}"),
        ("runtime.dll", "the runtime it cannot start without"));

    private static byte[] ZipOf(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
