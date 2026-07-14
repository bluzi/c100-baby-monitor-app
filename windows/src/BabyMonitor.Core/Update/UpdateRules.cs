using System.Globalization;

namespace BabyMonitor.Core.Update;

/// <summary>
/// The decisions the updater makes, taken out of the shell so they can be *executed* by the suite
/// rather than paraphrased by it.
///
/// This is the one component allowed to replace the running code. A rule it gets wrong is not a
/// cosmetic bug: pick the wrong asset, read the wrong line of the checksum file, or compare versions
/// as strings, and the app either installs the wrong thing or silently stops updating — and an app
/// that has silently stopped updating looks exactly like an app that is up to date, for months
/// (UPD-4). None of these decisions needs Windows, so none of them is allowed to hide there.
/// </summary>
public static class UpdateRules
{
    /// <summary>What the updater consumes, forever after the first install (UPD-3).</summary>
    public const string AssetSuffix = "-windows.zip";

    /// <summary>
    /// Ours, and deliberately not the Mac's `checksums.txt`: the two release jobs run at the same
    /// time, and one shared file would be a race whose loser publishes a build with no checksum —
    /// which this updater would then refuse, forever.
    /// </summary>
    public const string ChecksumAsset = "checksums-windows.txt";

    /// <summary>
    /// The zip is ours; the setup is not. The setup exists for the *first install only* — an updater
    /// that downloaded it would be handing a parent an installer to click through at 3am.
    /// </summary>
    public static bool IsOurAsset(string assetName) =>
        assetName.EndsWith(AssetSuffix, StringComparison.Ordinal);

    /// <summary>
    /// The digest of *our* asset, out of a checksum file that also lists the setup's. Take the wrong
    /// line and every download "fails its checksum" and is discarded — silently, and for good.
    /// </summary>
    public static string? Sha256For(string checksumFile, string assetSuffix = AssetSuffix)
    {
        foreach (var raw in checksumFile.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.Contains(assetSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            // `Get-FileHash` writes "<HASH>  <name>"; two spaces, CRLF, and the setup's line sits
            // right beside ours.
            var sha = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrEmpty(sha))
            {
                return sha;
            }
        }

        return null;
    }

    /// <summary>
    /// Numeric, component by component — never a string compare. `0.1.100` is newer than `0.1.99`,
    /// and alphabetically it is not. Equal is **not** newer: a staged copy of the version we already
    /// are must not be installable, or the app hands over to itself at every launch, forever.
    /// </summary>
    public static bool IsNewer(string candidate, string current)
    {
        var a = Parts(candidate);
        var b = Parts(current);
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

    /// <summary>
    /// A dev build reports `0.0.0`, which is older than every release — so it would treat CI's latest
    /// as an update and cheerfully copy it over the build directory you are working in. It does not
    /// check.
    /// </summary>
    public static bool CanCheck(string currentVersion) =>
        !string.IsNullOrEmpty(currentVersion) && currentVersion != "0.0.0";

    private static int[] Parts(string version) => version
        .Split('.')
        .Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0)
        .ToArray();
}
