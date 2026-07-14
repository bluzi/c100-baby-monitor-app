using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BabyMonitor.Core.Data;

namespace BabyMonitor.App.Services;

/// <summary>Where the app keeps everything: one folder under the user's local app data.</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BabyMonitor");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string LogFile => Path.Combine(Root, "babymonitor.log");

    public static string StagingRoot => Path.Combine(Root, "staging");

    public static void EnsureRoot() => Directory.CreateDirectory(Root);
}

/// <summary>
/// AUTH-5: where settings and the camera choice live — a plain JSON file. Not secrets: those go
/// through <see cref="DpapiSecretBox"/>.
///
/// Every write is atomic (write a temp file, then replace). A monitor that came back from an
/// overnight power cut to a half-written settings file, and therefore to a sign-in screen, would have
/// spent the rest of the night not monitoring.
/// </summary>
public sealed class JsonFileStore : IKeyValueStore
{
    private readonly object _lock = new();
    private readonly string _path;
    private JsonObject _values;

    public JsonFileStore(string? path = null)
    {
        _path = path ?? AppPaths.SettingsFile;
        AppPaths.EnsureRoot();
        _values = Load(_path);
    }

    public string? Get(string key)
    {
        lock (_lock)
        {
            return _values.TryGetPropertyValue(key, out var node) ? node?.GetValue<string>() : null;
        }
    }

    public void Put(string key, string value)
    {
        lock (_lock)
        {
            _values[key] = value;
            Save();
        }
    }

    public void Remove(string key)
    {
        lock (_lock)
        {
            _values.Remove(key);
            Save();
        }
    }

    private static JsonObject Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject()
                : new JsonObject();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Logging.Log.Warn("data", $"could not read {path} — starting from defaults: {e.Message}");
            return new JsonObject();
        }
    }

    private void Save()
    {
        try
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, _values.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Logging.Log.Error("data", $"could not save settings: {e.Message}", e);
        }
    }
}

/// <summary>
/// AUTH-6 / AUTH-6w: the Mi account token, encrypted with DPAPI to this Windows user on this machine.
///
/// DPAPI is the right primitive here for one reason above all: it keys on the *user*, not on the
/// binary. An update replaces every byte of the app and the session still opens — with no prompt, no
/// password, nothing for a parent to answer at 3am. (That is the exact failure the Mac's Keychain
/// story exists to avoid; on Windows it comes for free.)
///
/// It never throws. A blob that will not open — a roamed profile, a repaired install, a corrupted
/// file — is a lost session, and the user signs in again. It never falls back to plaintext.
/// </summary>
public sealed class DpapiSecretBox : ISecretBox
{
    /// <summary>Ties the blob to this app, so another app's DPAPI blob cannot be swapped in.</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("com.bluzi.babymonitor.session.v1");

    public string? Seal(string plain)
    {
        try
        {
            var sealed_ = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plain),
                Entropy,
                DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(sealed_);
        }
        catch (Exception e) when (e is CryptographicException or PlatformNotSupportedException)
        {
            Logging.Log.Error("data", $"DPAPI refused to seal the session: {e.Message}", e);
            return null; // AUTH-6: the caller drops the session rather than storing it in the clear
        }
    }

    public string? Open(string sealed_)
    {
        try
        {
            var plain = ProtectedData.Unprotect(
                Convert.FromBase64String(sealed_),
                Entropy,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or PlatformNotSupportedException)
        {
            // A refusal, a corrupted blob, a blob from another user — all the same answer: there is no
            // session. The user signs in again. Nothing crashes, and nothing falls back to plaintext.
            Logging.Log.Warn("data", $"the stored session could not be read back ({e.Message}) — signing in again");
            return null;
        }
    }
}

/// <summary>
/// UPD-3: the updater's GitHub token, in its own DPAPI blob. It is not the Mi session and must not
/// share its fate: signing out of Xiaomi should not stop the app updating itself.
/// </summary>
public static class UpdaterToken
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("com.bluzi.babymonitor.updater.v1");

    private static string File_ => Path.Combine(AppPaths.Root, "updater.token");

    public static string? Load()
    {
        try
        {
            if (!File.Exists(File_))
            {
                return null;
            }

            var plain = ProtectedData.Unprotect(
                File.ReadAllBytes(File_),
                Entropy,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            Logging.Log.Warn("update", $"the update token could not be read ({e.Message})");
            return null;
        }
    }

    public static void Save(string token)
    {
        try
        {
            AppPaths.EnsureRoot();
            File.WriteAllBytes(
                File_,
                ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            Logging.Log.Error("update", $"could not store the update token: {e.Message}", e);
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(File_))
            {
                File.Delete(File_);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Logging.Log.Warn("update", $"could not remove the update token: {e.Message}");
        }
    }
}
