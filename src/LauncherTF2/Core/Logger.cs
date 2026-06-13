using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace LauncherTF2.Core;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

/// <summary>
/// Simple file-based logger with automatic rotation (10 MB limit, 10 archived files).
/// Writes to app_debug.log in the app's base directory.
/// </summary>
public static class Logger
{
    private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_debug.log");
    private static readonly string LogArchivePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
    private static readonly object _lock = new();
    private static readonly long MaxLogFileSize = 10 * 1024 * 1024; // 10 MB
    private static LogLevel _minimumLogLevel = LogLevel.Info;

    public static LogLevel MinimumLogLevel
    {
        get => _minimumLogLevel;
        set => _minimumLogLevel = value;
    }

    // Convenience methods for each log level
    public static void LogDebug(string message) => Log(LogLevel.Debug, message);
    public static void LogInfo(string message) => Log(LogLevel.Info, message);
    public static void LogWarning(string message) => Log(LogLevel.Warning, message);
    public static void LogWarning(string message, Exception? exception) => Log(LogLevel.Warning, message, exception);
    public static void LogError(string message, Exception? exception = null) => Log(LogLevel.Error, message, exception);

    /// <summary>
    /// Synchronous log entry — safe to call from any thread.
    /// Uses a lock to serialize file writes and log rotation.
    /// </summary>
    public static void Log(LogLevel level, string message, Exception? exception = null)
    {
        if (level < _minimumLogLevel)
            return;

        try
        {
            var logEntry = FormatLogEntry(level, message, exception);

            lock (_lock)
            {
                CheckAndRotateLog();
                File.AppendAllText(LogPath, logEntry);
            }

            System.Diagnostics.Debug.WriteLine(logEntry);
        }
        catch (Exception ex)
        {
            // Logger itself failed — write to debug output as last resort
            try
            {
                System.Diagnostics.Debug.WriteLine($"[Logger] Failed to write log entry: {ex.Message}");
            }
            catch
            {
                // Nothing left to try
            }
        }
    }

    /// <summary>
    /// Async log entry — offloads file I/O to a background thread.
    /// Useful for high-frequency callers that don't want to block.
    /// </summary>
    public static async Task LogAsync(LogLevel level, string message, Exception? exception = null)
    {
        if (level < _minimumLogLevel)
            return;

        try
        {
            var logEntry = FormatLogEntry(level, message, exception);

            await Task.Run(async () =>
            {
                // Rotation check still requires the lock
                lock (_lock)
                {
                    CheckAndRotateLog();
                }
                await File.AppendAllTextAsync(LogPath, logEntry).ConfigureAwait(false);
            }).ConfigureAwait(false);

            System.Diagnostics.Debug.WriteLine(logEntry);
        }
        catch (Exception ex)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[Logger] Async write failed: {ex.Message}");
            }
            catch
            {
                // Nothing left to try
            }
        }
    }

    /// <summary>
    /// Builds a formatted log line with timestamp, level, message, and optional exception details.
    /// </summary>
    private static string FormatLogEntry(LogLevel level, string message, Exception? exception)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var entry = new StringBuilder();
        entry.Append($"[{timestamp}] [{level,-7}] {message}");

        if (exception != null)
        {
            entry.AppendLine();
            entry.Append($"  Exception: {exception.GetType().Name}: {exception.Message}");
            if (exception.StackTrace != null)
            {
                entry.AppendLine();
                entry.Append($"  StackTrace: {exception.StackTrace}");
            }
            // Include inner exceptions for nested error chains
            if (exception.InnerException != null)
            {
                entry.AppendLine();
                entry.Append($"  InnerException: {exception.InnerException.GetType().Name}: {exception.InnerException.Message}");
            }
        }

        entry.AppendLine();
        return entry.ToString();
    }

    /// <summary>
    /// Rotates the log file when it exceeds 10 MB.
    /// Keeps the last 10 archived files and deletes older ones.
    /// Must be called while holding _lock.
    /// </summary>
    private static void CheckAndRotateLog()
    {
        try
        {
            if (!File.Exists(LogPath))
                return;

            var fileInfo = new FileInfo(LogPath);
            if (fileInfo.Length >= MaxLogFileSize)
            {
                if (!Directory.Exists(LogArchivePath))
                    Directory.CreateDirectory(LogArchivePath);

                var archiveName = $"app_debug_{DateTime.Now:yyyyMMdd_HHmmss}.log";
                var archivePath = Path.Combine(LogArchivePath, archiveName);
                File.Move(LogPath, archivePath);

                Logger.LogInfo($"[Logger] Log rotated — archived as {archiveName}");

                // Prune old archives, keeping only the 10 most recent
                var logFiles = Directory.GetFiles(LogArchivePath, "app_debug_*.log")
                    .OrderByDescending(f => f)
                    .Skip(10);

                foreach (var oldLog in logFiles)
                {
                    try { File.Delete(oldLog); }
                    catch { /* best-effort cleanup */ }
                }
            }
        }
        catch
        {
            // Rotation is non-critical — don't let it crash the app
        }
    }

    /// <summary>
    /// Sets the minimum log level and writes the first log entry.
    /// Called once during app startup in App.xaml.cs.
    /// </summary>
    public static void Initialize(LogLevel minimumLevel = LogLevel.Info)
    {
        _minimumLogLevel = minimumLevel;
        LogInfo($"[Logger] Initialized — minimum level: {minimumLevel}, path: {LogPath}");
    }
}
