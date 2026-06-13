using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherTF2.Core;
using LauncherTF2.ViewModels;

namespace LauncherTF2.Views;

public partial class ModsView : UserControl
{
    public ModsView()
    {
        InitializeComponent();
    }

    private async void UserControl_Drop(object sender, DragEventArgs e)
    {
        try
        {
            ResetDropVisual();

            if (e.Data.GetDataPresent(DataFormats.FileDrop) && DataContext is ModsViewModel viewModel)
            {
                var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (paths != null && paths.Length > 0)
                    await viewModel.HandleDropAsync(paths);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("[ModsView] Unhandled error while processing root-level drop", ex);
        }
    }

    private void UserControl_DragEnter(object sender, DragEventArgs e) => SetCopyEffectIfFiles(e);
    private void UserControl_DragOver(object sender, DragEventArgs e) => SetCopyEffectIfFiles(e);

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        try
        {
            ResetDropVisual();

            if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
                DataContext is ModsViewModel viewModel)
            {
                var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (paths != null && paths.Length > 0)
                    await viewModel.HandleDropAsync(paths);
            }
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Logger.LogError("[ModsView] Unhandled error while processing drop-zone drop", ex);
        }
    }

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && sender is Border border)
        {
            border.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "AccentBrush");
            border.Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0x6B, 0x00));
        }
        SetCopyEffectIfFiles(e);
    }

    private void DropZone_DragOver(object sender, DragEventArgs e) => SetCopyEffectIfFiles(e);

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        // WPF fires DragLeave even when moving between child elements — bounds-check before resetting.
        if (sender is not Border border) return;
        var pos = e.GetPosition(border);
        if (pos.X >= 0 && pos.Y >= 0 && pos.X <= border.ActualWidth && pos.Y <= border.ActualHeight)
            return;

        ResetDropVisual();
    }

    private static void SetCopyEffectIfFiles(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void ResetDropVisual()
    {
        if (DropZone == null) return;

        // Use SetResourceReference to restore theme-tracked brushes; assigning a static
        // SolidColorBrush would permanently override the DynamicResource binding.
        DropZone.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "SurfaceInsetBrush");
        DropZone.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "BorderBrush");
    }
}
