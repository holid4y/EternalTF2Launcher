using System.Windows;
using System.Windows.Controls;
using LauncherTF2.Models;

namespace LauncherTF2.Views;

public partial class ProfileManagerView : Window
{
    private bool _isUpdatingSelection;
    private readonly ViewModels.ProfileManagerViewModel _viewModel;

    public ProfileManagerView(ViewModels.ProfileManagerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;

        // Mirror VM selection changes into the two ListBoxes.
        _viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(_viewModel.SelectedProfile))
                SyncSelection(_viewModel.SelectedProfile);
        };

        // Close after Apply so the user immediately sees the updated settings tab.
        _viewModel.ProfileApplied += (s, e) => Close();

        Loaded += (s, e) => SyncSelection(_viewModel.SelectedProfile);
    }

    private void SyncSelection(Profile? p)
    {
        _isUpdatingSelection = true;

        if (p == null)
        {
            UserProfilesListBox.SelectedItem = null;
            BuiltInProfilesListBox.SelectedItem = null;
        }
        else if (p.IsUserCreated)
        {
            UserProfilesListBox.SelectedItem = p;
            BuiltInProfilesListBox.SelectedItem = null;
        }
        else
        {
            BuiltInProfilesListBox.SelectedItem = p;
            UserProfilesListBox.SelectedItem = null;
        }

        _isUpdatingSelection = false;
    }

    private void UserProfilesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelection) return;
        if (UserProfilesListBox.SelectedItem is Profile p)
            _viewModel.SelectedProfile = p;
    }

    private void BuiltInProfilesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelection) return;
        if (BuiltInProfilesListBox.SelectedItem is Profile p)
            _viewModel.SelectedProfile = p;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
