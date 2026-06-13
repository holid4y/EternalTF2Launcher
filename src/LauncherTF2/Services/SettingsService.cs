using LauncherTF2.Models;
using LauncherTF2.Core;
using System.IO;
using System.Text.Json;

namespace LauncherTF2.Services;

public class SettingsService
{
    private readonly string _settingsPath;
    private SettingsModel _currentSettings = new();
    private readonly object _lock = new();

    // Property names whose cvars appeared in the user-owned section of autoexec.cfg.
    // The writer force-emits these so the managed block wins the last-write-wins race.
    private IReadOnlySet<string>? _shadowedCvars;

    public SettingsService()
    {
        _settingsPath = GamePaths.SettingsPath;
        LoadSettings();
    }

    public void SetShadowedCvars(IReadOnlySet<string> shadowed)
    {
        lock (_lock)
        {
            _shadowedCvars = shadowed;
        }
    }

    // Used when applying a profile with stored UserAutoexecContent — cleared after the next save.
    public void SetPendingUserContent(string? content)
    {
        lock (_lock)
        {
            _pendingUserContent = content;
        }
    }

    private string? _pendingUserContent;

    public SettingsModel GetSettings()
    {
        lock (_lock)
        {
            return _currentSettings;
        }
    }

    public bool SaveSettings(SettingsModel settings)
    {
        lock (_lock)
        {
            try
            {
                _currentSettings = settings ?? throw new ArgumentNullException(nameof(settings));
                return SaveSettingsCore();
            }
            catch (Exception ex)
            {
                Logger.LogError("[Settings] Failed to save", ex);
                return false;
            }
        }
    }

    public bool ResetSettings()
    {
        lock (_lock)
        {
            try
            {
                _currentSettings = new SettingsModel();
                return SaveSettingsCore();
            }
            catch (Exception ex)
            {
                Logger.LogError("[Settings] Failed to reset", ex);
                return false;
            }
        }
    }

    // Must be called while _lock is held.
    private bool SaveSettingsCore()
    {
        ValidateSettings(_currentSettings);

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        string json = JsonSerializer.Serialize(_currentSettings, options);
        File.WriteAllText(_settingsPath, json);

        Logger.LogDebug("[Settings] Saved");

        if (!string.IsNullOrWhiteSpace(_currentSettings.SteamPath) && Directory.Exists(_currentSettings.SteamPath))
        {
            try
            {
                var userContentOverride = _pendingUserContent;
                _pendingUserContent = null;

                AutoexecWriter.WriteToAutoexec(
                    _currentSettings, _currentSettings.SteamPath, _shadowedCvars, userContentOverride);
                Logger.LogDebug("[AutoexecWriter] Managed block regenerated");
            }
            catch (Exception ex)
            {
                Logger.LogWarning("[AutoexecWriter] Failed to write autoexec.cfg", ex);
            }
        }
        else
        {
            Logger.LogWarning($"[Settings] Steam path not valid or doesn't exist: {_currentSettings.SteamPath}");
        }

        return true;
    }

    private void LoadSettings()
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
                MigrateLegacyFile(_settingsPath, "settings.json");

                if (File.Exists(_settingsPath))
                {
                    string fileContent = File.ReadAllText(_settingsPath);

                    if (!string.IsNullOrWhiteSpace(fileContent))
                    {
                        var options = new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        };

                        var loadedSettings = JsonSerializer.Deserialize<SettingsModel>(fileContent, options);

                        if (loadedSettings != null)
                        {
                            _currentSettings = loadedSettings;
                            ValidateSettings(_currentSettings);

                            // Auto-detect display resolution on first install only.
                            if (!_currentSettings.DisplayAutoDetected)
                            {
                                ApplyDetectedDisplay(_currentSettings);
                                _currentSettings.DisplayAutoDetected = true;
                                SaveSettingsCore();
                            }

                            Logger.LogInfo("[Settings] Loaded from settings.json");
                        }
                        else
                        {
                            Logger.LogWarning("[Settings] Deserialized settings were null, using defaults");
                            _currentSettings = new SettingsModel();
                        }
                    }
                    else
                    {
                        Logger.LogWarning("[Settings] settings.json is empty, using defaults");
                        _currentSettings = new SettingsModel();
                    }
                }
                else
                {
                    Logger.LogInfo("[Settings] settings.json not found, creating with defaults");
                    _currentSettings = new SettingsModel();
                    ApplyDetectedDisplay(_currentSettings);
                    _currentSettings.DisplayAutoDetected = true;
                    SaveSettingsCore();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("[Settings] Failed to load settings, using defaults", ex);
                _currentSettings = new SettingsModel();
            }
        }
    }

    private void ValidateSettings(SettingsModel settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SteamPath))
        {
            Logger.LogWarning("[Settings] Steam path is empty, using default");
            settings.SteamPath = GamePaths.DefaultTf2Path;
        }

        if (settings.Fov < 70) settings.Fov = 70;
        if (settings.Fov > 90) settings.Fov = 90;

        if (settings.ViewmodelFov < 50) settings.ViewmodelFov = 50;
        if (settings.ViewmodelFov > 120) settings.ViewmodelFov = 120;

        if (settings.MouseSensitivity < 0.1) settings.MouseSensitivity = 0.1;
        if (settings.MouseSensitivity > 30) settings.MouseSensitivity = 30;

        if (settings.Width < 640) settings.Width = 640;
        if (settings.Width > 7680) settings.Width = 7680;

        if (settings.Height < 480) settings.Height = 480;
        if (settings.Height > 4320) settings.Height = 4320;

        if (settings.Rate < 10000)  settings.Rate = 10000;
        if (settings.Rate > 786432) settings.Rate = 786432;

        if (settings.CmdRate < 10) settings.CmdRate = 10;
        if (settings.CmdRate > 128) settings.CmdRate = 128;

        if (settings.UpdateRate < 10) settings.UpdateRate = 10;
        if (settings.UpdateRate > 128) settings.UpdateRate = 128;

        if (settings.Interp < 0) settings.Interp = 0;
        if (settings.Interp > 0.5) settings.Interp = 0.5;
    }

    private static void ApplyDetectedDisplay(SettingsModel settings)
    {
        var info = DisplayDetectionService.GetPrimaryDisplay();
        if (info == null)
        {
            Logger.LogInfo("[Settings] Display auto-detect skipped — falling back to model defaults");
            return;
        }
        settings.Width = info.Value.Width;
        settings.Height = info.Value.Height;
        settings.RefreshRate = info.Value.RefreshRate;
        Logger.LogInfo($"[Settings] Auto-detected display: {settings.Width}×{settings.Height} @ {settings.RefreshRate}Hz");
    }

    public bool BackupSettings()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_settingsPath))
                {
                    Logger.LogWarning("[Settings] settings.json doesn't exist, nothing to backup");
                    return false;
                }

                var backupPath = Path.Combine(
                    Path.GetDirectoryName(_settingsPath) ?? AppDomain.CurrentDomain.BaseDirectory,
                    $"settings_backup_{DateTime.Now:yyyyMMdd_HHmmss}.json"
                );

                File.Copy(_settingsPath, backupPath);
                Logger.LogInfo($"[Settings] Backed up to: {backupPath}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError("[Settings] Failed to backup", ex);
                return false;
            }
        }
    }

    public bool RestoreSettings(string backupPath)
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(backupPath))
                {
                    Logger.LogError($"[Settings] Backup file not found: {backupPath}");
                    return false;
                }

                var fileContent = File.ReadAllText(backupPath);
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                var restoredSettings = JsonSerializer.Deserialize<SettingsModel>(fileContent, options);

                if (restoredSettings != null)
                {
                    ValidateSettings(restoredSettings);
                    _currentSettings = restoredSettings;
                    SaveSettingsCore();
                    Logger.LogInfo($"[Settings] Restored from: {backupPath}");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Settings] Failed to restore from: {backupPath}", ex);
                return false;
            }
        }
    }

    // Moves a file from its old exe-adjacent location to the new AppData path on first run.
    private static void MigrateLegacyFile(string newPath, string fileName)
    {
        if (File.Exists(newPath)) return;
        var legacy = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
        if (!File.Exists(legacy)) return;
        try
        {
            File.Move(legacy, newPath);
            Logger.LogInfo($"[Settings] Migrated {fileName} to AppData");
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[Settings] Could not migrate {fileName}: {ex.Message}");
        }
    }

    // ─────────── Launcher-specific configuration (separate from TF2 settings) ───────────

    private static readonly string LauncherConfigPath = GamePaths.LauncherConfigPath;

    public LauncherConfig GetLauncherConfig()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LauncherConfigPath)!);
            MigrateLegacyFile(LauncherConfigPath, "launcher_config.json");

            if (File.Exists(LauncherConfigPath))
            {
                var json = File.ReadAllText(LauncherConfigPath);
                return JsonSerializer.Deserialize<LauncherConfig>(json) ?? new LauncherConfig();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("[Settings] Failed to load launcher_config.json", ex);
        }
        return new LauncherConfig();
    }

    public void SaveLauncherConfig(LauncherConfig config)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LauncherConfigPath)!);
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(LauncherConfigPath, json);
        }
        catch (Exception ex)
        {
            Logger.LogError("[Settings] Failed to save launcher_config.json", ex);
        }
    }
}
