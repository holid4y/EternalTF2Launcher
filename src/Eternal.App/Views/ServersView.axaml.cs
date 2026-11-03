using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Eternal.App.ViewModels;

namespace Eternal.App.Views;

/// <summary>
/// The list re-sorts while servers answer, and each re-sort resets it. Only a
/// pick by the player changes the selection, so a reset never empties the pane.
/// </summary>
public partial class ServersView : UserControl
{
    public ServersView()
    {
        InitializeComponent();
        ServerList.SelectionChanged += OnSelectionChanged;
        ServerList.DoubleTapped += OnDoubleTapped;
        ServerList.KeyDown += OnKeyDown;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is ServerRow row && DataContext is ServersViewModel vm && vm.Selected != row)
            vm.Selected = row;
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is ServerRow row && DataContext is ServersViewModel vm)
            vm.JoinCommand.Execute(row);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is ServersViewModel { Selected: { } row } vm)
        {
            vm.JoinCommand.Execute(row);
            e.Handled = true;
        }
    }
}
