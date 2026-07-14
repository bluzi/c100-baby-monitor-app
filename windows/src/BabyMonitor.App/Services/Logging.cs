using System.Diagnostics;
using System.Globalization;
using System.Text;
using CoreLog = BabyMonitor.Core.Logging.Log;

namespace BabyMonitor.App.Services;

/// <summary>
/// The app's log — the same shape as the phone's logcat and the Mac's os_log: one subsystem, a tag
/// per component in the message, so a night can be reconstructed the same way on any of them.
///
/// It goes to a file, because on Windows there is nothing to `adb logcat` and a field issue at 3am
/// has to leave a trace somebody can read the next morning:
///
///     %LOCALAPPDATA%\BabyMonitor\babymonitor.log
///
/// The file is capped and rolled once, so a monitor left running for a month cannot fill a disk —
/// a full disk is a stopped monitor.
/// </summary>
public static class Logging
{
    private const long MaxBytes = 8 * 1024 * 1024;

    private static readonly object Lock = new();
    private static readonly Stopwatch Since = Stopwatch.StartNew();
    private static StreamWriter? _writer;

    /// <summary>Wire the shared core's logging into this one, so the protocol layer lands in the same file.</summary>
    public static void Install()
    {
        AppPaths.EnsureRoot();
        Open();
        CoreLog.Install((level, tag, message, error) =>
        {
            var text = error != null ? $"{message} — {error.Message}" : message;
            Write(level.ToString().ToUpperInvariant()[0], tag, text);
        });
        Log.Info("app", $"log opened at {AppPaths.LogFile}");
    }

    private static void Open()
    {
        try
        {
            if (File.Exists(AppPaths.LogFile) && new FileInfo(AppPaths.LogFile).Length > MaxBytes)
            {
                File.Move(AppPaths.LogFile, AppPaths.LogFile + ".1", overwrite: true);
            }

            _writer = new StreamWriter(
                new FileStream(AppPaths.LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                Encoding.UTF8)
            {
                AutoFlush = true, // a crash must not take the line that explains it with it
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"could not open the log file: {e.Message}");
        }
    }

    private static void Write(char level, string tag, string message)
    {
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTime.Now:HH:mm:ss.fff} {Since.Elapsed.TotalSeconds,8:F2} {level} [{tag}] {message}");
        Debug.WriteLine(line);
        lock (Lock)
        {
            try
            {
                _writer?.WriteLine(line);
            }
            catch (IOException)
            {
                // A log that cannot be written must never take the monitor down with it.
            }
        }
    }

    /// <summary>The shell's own logging, in the same file and the same shape as the core's.</summary>
    public static class Log
    {
        public static void Debug(string tag, string message) => Write('D', tag, message);

        public static void Info(string tag, string message) => Write('I', tag, message);

        public static void Warn(string tag, string message, Exception? error = null) =>
            Write('W', tag, error == null ? message : $"{message} — {error.Message}");

        public static void Error(string tag, string message, Exception? error = null) =>
            Write('E', tag, error == null ? message : $"{message} — {error}");
    }
}
