using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace ZFTP.Core;

/// <summary>
/// Small, dependency-free application logger. The log intentionally lives beside
/// the user's ZFTP configuration so it is easy to find and attach to a bug report.
/// </summary>
public static class AppLog
{
    private const long MaxLogBytes = 5 * 1024 * 1024;
    private static readonly object Sync = new();
    private static bool _initialized;

    public static string LogPath => Path.Combine(ProfileStore.FolderPath, "zftp.log");
    private static string PreviousLogPath => Path.Combine(ProfileStore.FolderPath, "zftp.previous.log");

    public static void Initialize()
    {
        lock (Sync)
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                Directory.CreateDirectory(ProfileStore.FolderPath);
                RotateIfNeeded();

                var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
                WriteUnsafe("INFO", "App",
                    $"ZFTP starting. Version={version}; OS={RuntimeInformation.OSDescription}; " +
                    $"ProcessArch={RuntimeInformation.ProcessArchitecture}; OSArch={RuntimeInformation.OSArchitecture}; " +
                    $"BaseDirectory={AppContext.BaseDirectory}");
            }
            catch
            {
                // Logging must never stop the application from starting.
            }
        }
    }

    public static void Info(string component, string message) => Write("INFO", component, message);
    public static void Warn(string component, string message) => Write("WARN", component, message);

    public static void Error(string component, string message, Exception? exception = null)
    {
        var detail = exception == null ? message : $"{message}{Environment.NewLine}{exception}";
        Write("ERROR", component, detail);
    }

    private static void Write(string level, string component, string message)
    {
        lock (Sync)
        {
            try
            {
                if (!_initialized)
                {
                    Directory.CreateDirectory(ProfileStore.FolderPath);
                    RotateIfNeeded();
                    _initialized = true;
                }

                WriteUnsafe(level, component, message);
            }
            catch
            {
                // Best effort by design. A full/locked disk must not break mounts.
            }
        }
    }

    private static void WriteUnsafe(string level, string component, string message)
    {
        var normalized = (message ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
        var thread = Environment.CurrentManagedThreadId;
        var sb = new StringBuilder();

        foreach (var line in lines)
            sb.Append('[').Append(timestamp).Append("] [").Append(level).Append("] [")
              .Append(component).Append("] [T").Append(thread).Append("] ")
              .AppendLine(line);

        File.AppendAllText(LogPath, sb.ToString(), Encoding.UTF8);
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (!info.Exists || info.Length < MaxLogBytes) return;

            if (File.Exists(PreviousLogPath)) File.Delete(PreviousLogPath);
            File.Move(LogPath, PreviousLogPath);
        }
        catch
        {
            // Continue appending to the current log if rotation is unavailable.
        }
    }
}
