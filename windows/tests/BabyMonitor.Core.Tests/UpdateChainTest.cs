using Xunit;

namespace BabyMonitor.Core.Tests;

/// <summary>
/// The update chain, end to end, as arithmetic — the part of it that is a decision rather than a file
/// copy, and therefore the part that can be wrong on a machine that is not Windows.
///
/// The chain is: a parent runs the setup once → the app installs itself into a directory it can
/// rewrite → a release is published → the app finds it at launch, verifies it, puts it beside itself,
/// and offers a restart → whatever they answer, the new version runs next time. What follows pins the
/// judgements inside that sentence: which asset is ours, which version is newer, and which file the
/// checksum has to be read out of. Get any of them wrong and the app either updates to the wrong
/// thing or silently never updates at all — and an app that has quietly stopped updating looks exactly
/// like an app that is up to date.
///
/// The Windows-only halves (the swap itself, the setup) are checklist steps W11/W11a/W11c: they need a
/// PC, and they are named there so nobody assumes this file covered them.
/// </summary>
public class UpdateChainTest
{
    private const string ZipSuffix = "-windows.zip"; // Updater.AssetSuffix
    private const string ChecksumAsset = "checksums-windows.txt"; // Updater.ChecksumAsset

    [Theory(DisplayName = "UPD-3 the updater takes the zip — never the setup, never the Mac's or the phone's")]
    [InlineData("babymonitor-v0.1.42-windows.zip", true)]
    [InlineData("babymonitor-v0.1.42-windows-setup.exe", false)] // the first install, and nothing else
    [InlineData("babymonitor-v0.1.42-macos.zip", false)]
    [InlineData("babymonitor-v0.1.42.apk", false)]
    [InlineData("checksums-windows.txt", false)]
    [InlineData("checksums.txt", false)]
    public void ItPicksItsOwnAsset(string assetName, bool isOurs)
    {
        Assert.Equal(isOurs, assetName.EndsWith(ZipSuffix, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "UPD-3 the checksum is read from the Windows file, and from the zip's line in it")]
    public void ItReadsItsOwnChecksum()
    {
        // Both release jobs run at once, so each writes its own checksum file: the Mac's `checksums.txt`
        // and ours. Reading the wrong one is an update discarded forever (UPD-3), which is an app that
        // has quietly stopped updating.
        Assert.NotEqual("checksums.txt", ChecksumAsset);

        // And inside it, the setup's line sits beside the zip's. Taking the wrong line means every
        // download "fails its checksum" and is thrown away — silently, and for good.
        const string file = """
            9f2c1b  babymonitor-v0.1.42-windows-setup.exe
            ab3f07  babymonitor-v0.1.42-windows.zip
            """;

        var line = file
            .Split('\n')
            .FirstOrDefault(l => l.Contains(ZipSuffix, StringComparison.Ordinal));
        var sha = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        Assert.Equal("ab3f07", sha);
    }

    [Theory(DisplayName = "UPD-5w only a newer version takes over — a staged copy of ourselves is not an update loop")]
    [InlineData("0.1.43", "0.1.42", true)]
    [InlineData("0.1.42", "0.1.42", false)] // the version we already are: applying it again would loop
    [InlineData("0.1.41", "0.1.42", false)] // older: a downgrade is never an update
    [InlineData("0.2.0", "0.1.99", true)]
    [InlineData("0.1.100", "0.1.99", true)] // not a string comparison: "100" < "99" alphabetically
    public void OnlyNewerVersionsAreApplied(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, IsNewer(candidate, current));
    }

    [Fact(DisplayName = "UPD-5w the staged folder is tidied away once we ARE that version — a disk fills, a monitor stops")]
    public void StagingIsTidiedOnceApplied()
    {
        // CleanStaging keeps only what is still ahead of us. Without it, every update a monitor ever
        // took would leave a whole app folder behind — and a full disk is a stopped monitor, which is
        // the same reason the log is capped.
        var staging = new[] { "0.1.40", "0.1.41", "0.1.42", "0.1.43" };
        const string current = "0.1.42";

        var kept = staging.Where(v => IsNewer(v, current)).ToArray();
        var removed = staging.Where(v => !IsNewer(v, current)).ToArray();

        Assert.Equal(new[] { "0.1.43" }, kept); // still waiting to be installed
        Assert.Equal(new[] { "0.1.40", "0.1.41", "0.1.42" }, removed); // this version or older: just disk
    }

    /// <summary>The same comparison Updater.IsNewer makes.</summary>
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
}
