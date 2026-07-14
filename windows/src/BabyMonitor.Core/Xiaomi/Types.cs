namespace BabyMonitor.Core.Xiaomi;

public class XiaomiException : Exception
{
    public XiaomiException(string message, Exception? cause = null)
        : base(message, cause)
    {
    }
}

/// <summary>
/// AUTH-8 / BG-8: the stored session is gone and cannot be refreshed — only the user can fix this.
/// Distinct from every other failure precisely because retrying it forever is pointless: the app
/// must say so instead of looping "connection lost" all night.
/// </summary>
public sealed class AuthExpiredException : XiaomiException
{
    public AuthExpiredException(string message, Exception? cause = null)
        : base(message, cause)
    {
    }
}

/// <summary>
/// LIVE-12: the selected camera speaks a protocol (or sends audio in a format) we do not support —
/// retrying can never fix it, so the engine must say so instead of looping "connection lost".
/// </summary>
public sealed class UnsupportedCameraException : XiaomiException
{
    public UnsupportedCameraException(string message)
        : base(message)
    {
    }
}

public sealed record Session(
    string UserId,
    string CUserId,
    string PassToken,
    string ServiceToken,
    byte[] Ssecurity,
    string Region)
{
    public bool Equals(Session? other) =>
        other is not null &&
        UserId == other.UserId &&
        CUserId == other.CUserId &&
        PassToken == other.PassToken &&
        ServiceToken == other.ServiceToken &&
        Ssecurity.AsSpan().SequenceEqual(other.Ssecurity) &&
        Region == other.Region;

    public override int GetHashCode() => HashCode.Combine(UserId, ServiceToken);
}

public sealed record Device(string Did, string Name, string Model, string Mac, string Ip);

/// <summary>PROTO-24: the camera's night-vision mode (Camera Control siid 2 / piid 3 on the C100).</summary>
public enum NightVisionMode
{
    On = 0,
    Off = 1,
    Auto = 2,
}

public sealed record MissVendor(int Vendor, string? Uid, string DevicePublicHex, string Sign);

/// <summary>What one step of signing in produced: a session, or a question for the user.</summary>
public abstract record LoginResult
{
    public sealed record Ok(Session Session) : LoginResult;

    public sealed record Captcha(
        byte[] Image,
        string ContentType,
        Func<string, Task<LoginResult>> Submit) : LoginResult;

    public sealed record TwoFactor(
        string Channel, // "phone" | "email"
        string MaskedTarget,
        Func<string, Task<LoginResult>> Submit) : LoginResult;
}

/// <summary>One media frame off the camera.</summary>
public abstract record Frame(long Pts, long Sequence, long Flags, byte[] Data)
{
    public sealed record Video(
        string Codec, // "h264" | "h265"
        long Pts,
        long Sequence,
        long Flags,
        byte[] Data) : Frame(Pts, Sequence, Flags, Data);

    public sealed record Audio(
        string Codec, // "opus" | "pcma" | "pcmu" | "pcm"
        int SampleRate,
        long Pts,
        long Sequence,
        long Flags,
        byte[] Data) : Frame(Pts, Sequence, Flags, Data);
}

/// <summary>The small facts about Mi devices that everything else asks about.</summary>
public static class Mi
{
    public static readonly IReadOnlyList<string> Regions = new[] { "cn", "de", "us", "ru", "sg", "i2" };

    /// <summary>PROTO-11: a camera is a device whose model contains ".camera.".</summary>
    public static bool IsCamera(string model) => model.Contains(".camera.", StringComparison.Ordinal);

    public static NightVisionMode? NightVisionFromValue(int value) =>
        Enum.IsDefined(typeof(NightVisionMode), value) ? (NightVisionMode)value : null;

    public static string VendorName(int id) => id switch
    {
        1 => "tutk",
        3 => "agora",
        4 => "cs2",
        6 => "mtp",
        _ => id.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
