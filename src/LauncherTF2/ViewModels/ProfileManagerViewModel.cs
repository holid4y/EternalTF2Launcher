using LauncherTF2.Models;
using LauncherTF2.Services;
using System.Collections.ObjectModel;
using System.Windows.Input;
using LauncherTF2.Core;

namespace LauncherTF2.ViewModels;

public class ProfileManagerViewModel : ViewModelBase
{
    private readonly ProfileService _profileService;
    private readonly SettingsModel _currentSettings;

    public ObservableCollection<Profile> BuiltInProfiles { get; } = new();
    public ObservableCollection<Profile> UserProfiles { get; } = new();

    private Profile? _selectedProfile;
    public Profile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
                OnPropertyChanged(nameof(IsSelectedProfileApplied));
        }
    }

    private string? _appliedProfileId;
    public string? AppliedProfileId
    {
        get => _appliedProfileId;
        private set
        {
            if (SetProperty(ref _appliedProfileId, value))
                OnPropertyChanged(nameof(IsSelectedProfileApplied));
        }
    }

    public bool IsSelectedProfileApplied =>
        SelectedProfile != null && SelectedProfile.Id == AppliedProfileId;

    public event EventHandler? ProfileApplied;

    /// <summary>
    /// The last profile that was applied via the Apply button inside this dialog.
    /// Null when no profile has been applied this session. The caller
    /// (<see cref="SettingsViewModel"/>) reads this after the dialog closes to
    /// know whether a user-section override should be written to the autoexec.
    /// </summary>
    public Profile? AppliedProfile { get; private set; }

    public ICommand ApplyProfileCommand { get; }
    public ICommand SaveCurrentAsProfileCommand { get; }
    public ICommand DeleteProfileCommand { get; }
    public ICommand RenameProfileCommand { get; }
    public ICommand DuplicateProfileCommand { get; }
    public ICommand NewProfileCommand { get; }
    public ICommand ExportProfileCommand { get; }
    public ICommand ImportProfileCommand { get; }

    public ProfileManagerViewModel(ProfileService profileService, SettingsModel currentSettings)
    {
        _profileService = profileService;
        _currentSettings = currentSettings;

        RefreshProfiles();

        ApplyProfileCommand = new RelayCommand(o => ApplySelectedProfile(), o => SelectedProfile != null);
        
        SaveCurrentAsProfileCommand = new RelayCommand(o =>
        {
            var dialog = new Views.InputDialog("Save Current Settings", "Profile Name:", "My Profile", true)
            {
                Owner = System.Windows.Application.Current.Windows.OfType<System.Windows.Window>().FirstOrDefault(x => x.IsActive)
            };
            
            if (dialog.ShowDialog() == true)
            {
                // Pass steamPath so the profile captures the full user section.
                var p = _profileService.CreateUserProfile(
                    dialog.InputText, dialog.DescriptionText, _currentSettings,
                    steamPath: _currentSettings.SteamPath);
                RefreshProfiles();
                SelectedProfile = p;
            }
        });

        DeleteProfileCommand = new RelayCommand(o =>
        {
            if (SelectedProfile != null && SelectedProfile.IsUserCreated)
            {
                if (Views.MessageDialog.ShowConfirm("Delete Profile", $"Are you sure you want to delete '{SelectedProfile.Name}'?", "Delete"))
                {
                    _profileService.DeleteUserProfile(SelectedProfile.Id);
                    RefreshProfiles();
                }
            }
        }, o => SelectedProfile != null && SelectedProfile.IsUserCreated);

        RenameProfileCommand = new RelayCommand(o =>
        {
            if (SelectedProfile != null && SelectedProfile.IsUserCreated)
            {
                var dialog = new Views.InputDialog("Rename Profile", "New Name:", SelectedProfile.Name)
                {
                    Owner = System.Windows.Application.Current.Windows.OfType<System.Windows.Window>().FirstOrDefault(x => x.IsActive)
                };
                if (dialog.ShowDialog() == true)
                {
                    _profileService.RenameUserProfile(SelectedProfile.Id, dialog.InputText);
                    RefreshProfiles();
                }
            }
        }, o => SelectedProfile != null && SelectedProfile.IsUserCreated);

        DuplicateProfileCommand = new RelayCommand(o =>
        {
            if (SelectedProfile != null)
            {
                var dialog = new Views.InputDialog("Duplicate Profile", "Profile Name:", SelectedProfile.Name + " (Copy)")
                {
                    Owner = System.Windows.Application.Current.Windows.OfType<System.Windows.Window>().FirstOrDefault(x => x.IsActive)
                };
                if (dialog.ShowDialog() == true)
                {
                    // Apply the original's settings to a fresh model, then save.
                    // Pass explicitUserContent so the duplicate inherits the source
                    // profile's stored user section rather than the current file content.
                    var tempModel = new SettingsModel();
                    _profileService.ApplyProfile(SelectedProfile, tempModel);
                    var p = _profileService.CreateUserProfile(
                        dialog.InputText, SelectedProfile.Description, tempModel,
                        explicitUserContent: SelectedProfile.UserAutoexecContent);
                    RefreshProfiles();
                    SelectedProfile = p;
                }
            }
        }, o => SelectedProfile != null);

        NewProfileCommand = new RelayCommand(o =>
        {
            var dialog = new Views.InputDialog("New Profile", "Profile Name:", "New Profile", true)
            {
                Owner = System.Windows.Application.Current.Windows.OfType<System.Windows.Window>().FirstOrDefault(x => x.IsActive)
            };
            if (dialog.ShowDialog() == true)
            {
                var p = _profileService.CreateUserProfile(dialog.InputText, dialog.DescriptionText, new SettingsModel());
                RefreshProfiles();
                SelectedProfile = p;
            }
        });

        ExportProfileCommand = new RelayCommand(o =>
        {
            if (SelectedProfile != null && SelectedProfile.IsUserCreated)
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Export Profile",
                    Filter = "JSON Files|*.json",
                    FileName = $"{SelectedProfile.Id}.json"
                };
                if (dialog.ShowDialog() == true)
                {
                    try
                    {
                        _profileService.ExportUserProfile(SelectedProfile.Id, dialog.FileName);
                        Views.MessageDialog.ShowSuccess(
                            "Profile Exported",
                            $"'{SelectedProfile.Name}' was saved to:\n{System.IO.Path.GetFileName(dialog.FileName)}");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"[Profile] Export failed: {ex.Message}");
                        Views.MessageDialog.ShowError("Export Failed", $"Could not export the profile:\n{ex.Message}");
                    }
                }
            }
        }, o => SelectedProfile != null && SelectedProfile.IsUserCreated);

        ImportProfileCommand = new RelayCommand(o =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import Profile",
                Filter = "JSON Files|*.json"
            };
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var p = _profileService.ImportUserProfile(dialog.FileName,
                        msg => Views.MessageDialog.ShowError("Profile Import Warning", msg));
                    RefreshProfiles();
                    SelectedProfile = p;
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[Profile] Import failed: {ex.Message}");
                    Views.MessageDialog.ShowError("Import Failed", $"Could not import the profile:\n{ex.Message}");
                }
            }
        });
    }

    private void RefreshProfiles()
    {
        BuiltInProfiles.Clear();
        foreach (var p in _profileService.GetBuiltInProfiles()) BuiltInProfiles.Add(p);

        UserProfiles.Clear();
        foreach (var p in _profileService.GetUserProfiles()) UserProfiles.Add(p);
    }

    private void ApplySelectedProfile()
    {
        if (SelectedProfile == null) return;
        try
        {
            _profileService.ApplyProfile(SelectedProfile, _currentSettings);
            AppliedProfileId = SelectedProfile.Id;
            // Store the applied profile so SettingsViewModel can read its user content
            // after the dialog closes and route it to the autoexec writer.
            AppliedProfile = SelectedProfile;
            Logger.LogInfo($"[Profile] Applied '{SelectedProfile.Name}'.");
            ProfileApplied?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Logger.LogError($"[Profile] Apply failed: {ex.Message}");
            Views.MessageDialog.ShowError("Apply Failed", $"Could not apply the profile:\n{ex.Message}");
        }
    }
}
