using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Eternal.App.ViewModels;

namespace Eternal.App.Views;

/// <summary>Drag-to-reorder for the load order list; everything else is bindings.</summary>
public partial class ModsView : UserControl
{
    private static readonly DataFormat<string> DragFormat = DataFormat.CreateStringApplicationFormat("eternal-mod-row");
    private ModRowViewModel? _dragging;

    public ModsView()
    {
        InitializeComponent();
        ModList.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        ModList.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        ModList.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private async void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Control source || source.FindAncestorOfType<Border>(includeSelf: true) is not { Name: "Grip" } grip)
            return;
        if (grip.DataContext is not ModRowViewModel row || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _dragging = row;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(DragFormat, row.Name));
        e.Handled = true;
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        _dragging = null;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DragFormat))
        {
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DragFormat) || _dragging is null || DataContext is not ModsViewModel vm)
            return;
        e.Handled = true;
        var target = (e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ModRowViewModel;
        if (target is null || target == _dragging)
            return;
        var order = vm.Rows.ToList();
        vm.MoveTo(_dragging, order.IndexOf(target));
    }
}
