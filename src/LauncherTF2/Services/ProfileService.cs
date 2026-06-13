using LauncherTF2.Core;
using LauncherTF2.Models;
using LauncherTF2.Models.Settings;
using System.IO;
using System.Text.Json;

namespace LauncherTF2.Services;

public class ProfileService
{
    private readonly Dictionary<string, SettingItem> _schemaIndex;
    private readonly List<Profile> _builtInProfiles = new();
    private readonly List<Profile> _userProfiles = new();
    private readonly string _userProfilesDir;

    public ProfileService()
    {
        _schemaIndex = SettingsSchema
            .Build(new SettingsModel())
            .SelectMany(cat => cat.Items)
            .GroupBy(item => item.PropertyName)
            .ToDictionary(g => g.Key, g => g.First());

        _userProfilesDir = GamePaths.UserProfilesPath;

        var settingsPath = GamePaths.SettingsPath;
        if (File.Exists(settingsPath) && !Directory.Exists(_userProfilesDir))
        {
            IsFirstRunMigration = true;
            Directory.CreateDirectory(_userProfilesDir);
        }
    }

    public bool IsFirstRunMigration { get; }

    public void LoadAllProfiles()
    {
        _builtInProfiles.Clear();
        _userProfiles.Clear();

        var assembly = typeof(ProfileService).Assembly;
        var resourceNames = assembly.GetManifestResourceNames().Where(n => n.EndsWith(".json") && n.Contains(".Resources.Profiles."));
        
        foreach (var resourceName in resourceNames)
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    using var reader = new StreamReader(stream);
                    var json = reader.ReadToEnd();
                    var profile = JsonSerializer.Deserialize<Profile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (profile != null)
                    {
                        profile.IsUserCreated = false;
                        _builtInProfiles.Add(profile);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Profile] Error loading built-in profile from resource '{resourceName}': {ex.Message}");
            }
        }

        if (Directory.Exists(_userProfilesDir))
        {
            foreach (var file in Directory.GetFiles(_userProfilesDir, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var profile = JsonSerializer.Deserialize<Profile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (profile != null)
                    {
                        profile.IsUserCreated = true;
                        _userProfiles.Add(profile);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[Profile] Error loading user profile '{file}': {ex.Message}");
                }
            }
        }
    }

    public IReadOnlyList<Profile> GetAllProfiles() => _builtInProfiles.Concat(_userProfiles).ToList();
    public IReadOnlyList<Profile> GetBuiltInProfiles() => _builtInProfiles;
    public IReadOnlyList<Profile> GetUserProfiles() => _userProfiles;

    public void ApplyProfile(Profile profile, SettingsModel target)
    {
        var snapshot = SnapshotModel(target);

        try
        {
            foreach (var (key, value) in profile.Settings)
            {
                if (value is null) continue;

                if (_schemaIndex.TryGetValue(key, out var item))
                {
                    item.SetValue(target, value);
                    continue;
                }

                // Fall back to reflection for direct-UI properties (Fov, DxLevel, etc.) and legacy keys.
                if (TrySetByReflection(target, key, value)) continue;

                Logger.LogWarning($"[Profile] Unknown property '{key}' in profile '{profile.Name}', skipping.");
            }

            if (!string.IsNullOrWhiteSpace(profile.LaunchOptions))
                ApplyLaunchOptions(profile.LaunchOptions, target);
        }
        catch (Exception ex)
        {
            Logger.LogError($"[Profile] Apply failed: {ex.Message}. Restoring previous settings.");
            RestoreSnapshot(target, snapshot);
            throw;
        }
    }

    private static bool TrySetByReflection(SettingsModel target, string key, object? value)
    {
        var prop = typeof(SettingsModel).GetProperty(key);
        if (prop == null || !prop.CanWrite) return false;

        try
        {
            object? parsed;
            if (value is JsonElement el)
            {
                parsed = JsonSerializer.Deserialize(el.GetRawText(), prop.PropertyType);
            }
            else
            {
                parsed = Convert.ChangeType(value, prop.PropertyType, System.Globalization.CultureInfo.InvariantCulture);
            }
            prop.SetValue(target, parsed);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogDebug($"[Profile] Reflection set failed for '{key}': {ex.Message}");
            return false;
        }
    }

    // Handles -dxlevel, -novid, -console; other flags survive in LaunchArgs as custom user flags.
    private static void ApplyLaunchOptions(string launchOptions, SettingsModel target)
    {
        var tokens = launchOptions.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i++)
        {
            switch (tokens[i].ToLowerInvariant())
            {
                case "-dxlevel" when i + 1 < tokens.Length:
                    if (int.TryParse(tokens[++i], out var dxLevel))
                        target.DxLevel = dxLevel;
                    break;
                case "-novid":
                    target.SkipIntro = true;
                    break;
                case "-console":
                    // Not a model bool — add directly to LaunchArgs; SyncLaunchOptions preserves it.
                    if (target.LaunchArgs?.Contains("-console") != true)
                        target.LaunchArgs = (target.LaunchArgs ?? "").TrimEnd() + " -console";
                    break;
            }
        }
    }

    public Profile? DetectCurrentProfile(SettingsModel current)
    {
        // Alphabetical order is stable regardless of save timestamps.
        var userMatch = GetUserProfiles()
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(p => ProfileMatches(p, current));

        if (userMatch != null) return userMatch;

        return GetBuiltInProfiles()
            .OrderBy(p => p.Id)
            .FirstOrDefault(p => ProfileMatches(p, current));
    }

    public bool ProfileMatches(Profile profile, SettingsModel current)
    {
        foreach (var (key, value) in profile.Settings)
        {
            if (value is null) continue;

            var prop = _schemaIndex.TryGetValue(key, out var item)
                ? typeof(SettingsModel).GetProperty(item.PropertyName)
                : typeof(SettingsModel).GetProperty(key);
            if (prop == null) continue;

            var currentVal = prop.GetValue(current);
            object? profileVal;

            if (value is JsonElement el)
            {
                try { profileVal = JsonSerializer.Deserialize(el.GetRawText(), prop.PropertyType); }
                catch { continue; }
            }
            else
            {
                profileVal = value;
            }

            if (!Equals(currentVal, profileVal)) return false;
        }
        return true;
    }

    public Profile CreateUserProfile(
        string name,
        string? description,
        SettingsModel target,
        string? steamPath = null,
        string? explicitUserContent = null)
    {
        var snapshot = SnapshotModel(target);

        string? userContent = explicitUserContent;
        if (userContent == null && !string.IsNullOrEmpty(steamPath))
        {
            var cfgPath = Path.Combine(steamPath, "cfg", "autoexec.cfg");
            var raw = AutoexecWriter.ExtractUserContent(cfgPath);
            userContent = string.IsNullOrWhiteSpace(raw) ? null : raw;
        }

        var profile = new Profile
        {
            Name            = name,
            Description     = description,
            IsUserCreated   = true,
            Category        = "user",
            Settings        = snapshot,
            UserAutoexecContent = userContent,
        };
        SaveUserProfile(profile);
        return profile;
    }

    public Profile UpdateUserProfile(Profile existing, SettingsModel target, string? steamPath = null)
    {
        existing.Settings = SnapshotModel(target);

        if (!string.IsNullOrEmpty(steamPath))
        {
            var cfgPath = Path.Combine(steamPath, "cfg", "autoexec.cfg");
            var raw = AutoexecWriter.ExtractUserContent(cfgPath);
            existing.UserAutoexecContent = string.IsNullOrWhiteSpace(raw) ? null : raw;
        }

        SaveUserProfile(existing);
        Logger.LogInfo($"[Profile] Updated user profile '{existing.Name}'");
        return existing;
    }

    public void SaveUserProfile(Profile profile)
    {
        Directory.CreateDirectory(_userProfilesDir);
        profile.LastModified = DateTime.UtcNow;
        var path = Path.Combine(_userProfilesDir, $"{profile.Id}.json");
        var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);

        var existing = _userProfiles.FirstOrDefault(p => p.Id == profile.Id);
        if (existing != null) _userProfiles.Remove(existing);
        _userProfiles.Add(profile);
    }

    public void RenameUserProfile(string profileId, string newName)
    {
        var profile = _userProfiles.FirstOrDefault(p => p.Id == profileId);
        if (profile != null)
        {
            profile.Name = newName;
            SaveUserProfile(profile);
        }
    }

    public void DeleteUserProfile(string profileId)
    {
        var profile = _userProfiles.FirstOrDefault(p => p.Id == profileId);
        if (profile != null)
        {
            _userProfiles.Remove(profile);
            var path = Path.Combine(_userProfilesDir, $"{profileId}.json");
            if (File.Exists(path)) File.Delete(path);
        }
    }

    public void ExportUserProfile(string profileId, string destinationPath)
    {
        var profile = _userProfiles.FirstOrDefault(p => p.Id == profileId);
        if (profile != null)
        {
            var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(destinationPath, json);
        }
    }

    public Profile ImportUserProfile(string sourcePath, Action<string>? onWarning = null)
    {
        var json = File.ReadAllText(sourcePath);
        var profile = JsonSerializer.Deserialize<Profile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Failed to deserialize profile.");

        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new InvalidOperationException("Profile must have a name.");

        var unknownKeys = profile.Settings.Keys
            .Where(k => !_schemaIndex.ContainsKey(k))
            .ToList();

        if (unknownKeys.Count > 0)
        {
            var pct = unknownKeys.Count * 100 / profile.Settings.Count;
            Logger.LogWarning($"[Profile] Import: {unknownKeys.Count} unknown keys ({pct}%): {string.Join(", ", unknownKeys)}");

            if (pct > 20)
                onWarning?.Invoke($"This profile may have been created with a different version of the launcher. {unknownKeys.Count} settings were not recognised and will be skipped.");
        }

        if (GetUserProfiles().Any(p => p.Id == profile.Id))
            profile.Id = Guid.NewGuid().ToString("N")[..8];

        profile.IsUserCreated = true;
        SaveUserProfile(profile);
        return profile;
    }

    private Dictionary<string, object?> SnapshotModel(SettingsModel target)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var key in _schemaIndex.Keys)
        {
            var item = _schemaIndex[key];
            dict[key] = item.GetValue(target);
        }
        return dict;
    }

    private void RestoreSnapshot(SettingsModel target, Dictionary<string, object?> snapshot)
    {
        foreach (var (key, value) in snapshot)
        {
            if (_schemaIndex.TryGetValue(key, out var item))
            {
                item.SetValue(target, value);
            }
        }
    }
}
