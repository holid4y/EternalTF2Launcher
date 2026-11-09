using System.Text.RegularExpressions;
using Avalonia.Logging;
using Eternal.Core.Logging;

namespace Eternal.App.Services;

/// <summary>Puts Avalonia's own warnings and errors in the launcher log when log detail is set to Debug.</summary>
internal sealed partial class AvaloniaLog : ILogSink
{
    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex Placeholder();

    public bool IsEnabled(LogEventLevel level, string area) =>
        level >= LogEventLevel.Warning && Eternal.Core.Logging.Log.MinimumLevel == LogLevel.Debug;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
        Write(level, area, messageTemplate);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        var next = 0;
        Write(level, area, Placeholder().Replace(messageTemplate, m => next < propertyValues.Length ? propertyValues[next++]?.ToString() ?? "" : m.Value));
    }

    private static void Write(LogEventLevel level, string area, string message) =>
        Eternal.Core.Logging.Log.Debug($"Avalonia {level} [{area}] {message}");
}
