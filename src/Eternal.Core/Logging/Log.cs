using System.Globalization;
using System.Text;

namespace Eternal.Core.Logging;

public enum LogLevel { Debug, Info, Warning, Error }

/// <summary>File logger with size-based rotation. Never throws, so a broken log folder can't crash the launcher.</summary>
public static class Log
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static string? _path;

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public static void Initialize(string directory, LogLevel level, bool clearExisting = false)
    {
        lock (Gate)
        {
            MinimumLevel = level;
            try
            {
                Directory.CreateDirectory(directory);
                _path = Path.Combine(directory, "launcher.log");
                if (clearExisting)
                {
                    foreach (var old in Directory.EnumerateFiles(directory, "*.log*"))
                        TryDelete(old);
                }
                OpenWriter();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _writer = null;
            }
        }
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message, null);
    public static void Info(string message) => Write(LogLevel.Info, message, null);
    public static void Warn(string message, Exception? ex = null) => Write(LogLevel.Warning, message, ex);
    public static void Error(string message, Exception? ex = null) => Write(LogLevel.Error, message, ex);

    public static void Shutdown()
    {
        lock (Gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private static void Write(LogLevel level, string message, Exception? ex)
    {
        if (level < MinimumLevel)
            return;

        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(' ').Append(level switch { LogLevel.Debug => "DBG", LogLevel.Info => "INF", LogLevel.Warning => "WRN", _ => "ERR" })
            .Append(' ').Append(message);
        if (ex is not null)
            line.AppendLine().Append(ex);

        lock (Gate)
        {
            if (_writer is null)
                return;
            try
            {
                _writer.WriteLine(line.ToString());
                if (_writer.BaseStream.Length > MaxBytes)
                    Rotate();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                _writer = null;
            }
        }
    }

    private static void OpenWriter()
    {
        _writer?.Dispose();
        var stream = new FileStream(_path!, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    private static void Rotate()
    {
        _writer?.Dispose();
        _writer = null;
        for (var i = 2; i >= 1; i--)
        {
            var from = i == 1 ? _path! : $"{_path}.{i - 1}";
            if (File.Exists(from))
                File.Move(from, $"{_path}.{i}", overwrite: true);
        }
        OpenWriter();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
