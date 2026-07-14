namespace BabyMonitor.Core.Logging;

/// <summary>
/// Pure logging facade, so the protocol layer can log while staying free of any Windows API.
/// The app installs a sink at start-up; tests leave the default no-op sink.
///
/// One tag per subsystem ("login", "cloud", "cs2", "miss", "engine", "ui", "audio", "video",
/// "update", "app"), exactly as on the phone and the Mac, so a night reads the same on any of them.
///
/// Never log secrets — password, passToken, serviceToken, ssecurity. Log ids, ips, statuses and
/// error messages.
/// </summary>
public static class Log
{
    public enum Level
    {
        Debug,
        Info,
        Warn,
        Error,
    }

    public delegate void Sink(Level level, string tag, string message, Exception? error);

    private static volatile Sink _sink = (_, _, _, _) => { };

    public static void Install(Sink sink) => _sink = sink;

    public static void D(string tag, string message) => _sink(Level.Debug, tag, message, null);

    public static void I(string tag, string message) => _sink(Level.Info, tag, message, null);

    public static void W(string tag, string message, Exception? error = null) =>
        _sink(Level.Warn, tag, message, error);

    public static void E(string tag, string message, Exception? error = null) =>
        _sink(Level.Error, tag, message, error);
}
