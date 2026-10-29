using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Eternal.Core.Logging;

namespace Eternal.Core.Persistence;

public enum LoadState
{
    /// <summary>File missing: defaults were used.</summary>
    New,
    Loaded,
    /// <summary>Main file was damaged; the previous version (.bak) was used.</summary>
    RestoredFromBackup,
    /// <summary>Main file and backup were damaged; defaults were used and the bad file kept aside.</summary>
    Reset,
    /// <summary>File exists but could not be read. Saving is unsafe until it can be.</summary>
    Unreadable,
}

public sealed record LoadOutcome(LoadState State, string? Detail = null)
{
    public bool CanSave => State != LoadState.Unreadable;
}

public static class JsonFile
{
    public static T Load<T>(string path, JsonTypeInfo<T> typeInfo, Func<T> fallback, out LoadOutcome outcome) where T : class
    {
        var (value, error, unreadable) = TryRead(path, typeInfo);
        if (value is not null)
        {
            outcome = new LoadOutcome(LoadState.Loaded);
            return value;
        }

        if (unreadable)
        {
            outcome = new LoadOutcome(LoadState.Unreadable, error);
            Log.Warn($"{Path.GetFileName(path)} could not be read: {error}");
            return fallback();
        }

        if (error is null && !File.Exists(path + ".bak"))
        {
            outcome = new LoadOutcome(LoadState.New);
            return fallback();
        }

        var (backup, backupError, _) = TryRead(path + ".bak", typeInfo);
        var quarantined = error is not null ? Quarantine(path) : null;
        if (backup is not null)
        {
            outcome = new LoadOutcome(LoadState.RestoredFromBackup, error);
            Log.Warn($"{Path.GetFileName(path)} was damaged ({error}); restored the previous version.");
            return backup;
        }

        outcome = new LoadOutcome(LoadState.Reset, error ?? backupError);
        Log.Warn($"{Path.GetFileName(path)} was damaged and replaced with defaults. Kept the old file as {quarantined}.");
        return fallback();
    }

    public static void Save<T>(string path, T value, JsonTypeInfo<T> typeInfo) =>
        AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, typeInfo));

    private static (T? Value, string? Error, bool Unreadable) TryRead<T>(string path, JsonTypeInfo<T> typeInfo) where T : class
    {
        string? text;
        try
        {
            text = AtomicFile.ReadAllTextOrNull(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, ex.Message, true);
        }

        if (text is null)
            return (null, null, false);
        if (string.IsNullOrWhiteSpace(text))
            return (null, "file is empty", false);

        try
        {
            var value = JsonSerializer.Deserialize(text, typeInfo);
            return value is null ? (null, "file contains null", false) : (value, null, false);
        }
        catch (JsonException ex)
        {
            return (null, ex.Message, false);
        }
    }

    private static string? Quarantine(string path)
    {
        try
        {
            var target = $"{path}.corrupt-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
            File.Move(path, target, overwrite: true);
            return Path.GetFileName(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
