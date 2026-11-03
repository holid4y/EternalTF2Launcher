using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Eternal.App.ViewModels;

namespace Eternal.App.Views;

/// <summary>Drag-to-reorder and card selection for the load order; everything else is bindings.</summary>
public partial class ModsView : UserControl
{
    private static readonly DataFormat<string> DragFormat = DataFormat.CreateStringApplicationFormat("eternal-mod-row");
    private const double DragThreshold = 6;

    private ModRowViewModel? _dragging;
    private ModRowViewModel? _pressed;
    private Point _pressedAt;
    private ModsViewModel? _vm;

    public ModsView()
    {
        InitializeComponent();
        ModList.AddHandler(PointerPressedEvent, OnGripPressed, RoutingStrategies.Tunnel);
        foreach (var target in new Control[] { ModList, ModGrid })
        {
            target.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            target.AddHandler(DragDrop.DropEvent, OnDrop);
        }
        ModGrid.AddHandler(PointerPressedEvent, OnCardPressed, RoutingStrategies.Tunnel);
        ModGrid.AddHandler(PointerMovedEvent, OnCardMoved, RoutingStrategies.Tunnel);
        ModGrid.AddHandler(PointerReleasedEvent, OnCardReleased, RoutingStrategies.Tunnel);
        ModGrid.AddHandler(KeyDownEvent, OnCardKey);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _vm = DataContext as ModsViewModel;
    }

    private static ModRowViewModel? CardAt(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("modcard"))?.DataContext as ModRowViewModel;

    private static bool OnToggle(object? source) => (source as Visual)?.FindAncestorOfType<ToggleSwitch>(includeSelf: true) is not null;

    private async void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Control source || source.FindAncestorOfType<Border>(includeSelf: true) is not { Name: "Grip" } grip)
            return;
        if (grip.DataContext is not ModRowViewModel row || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        e.Handled = true;
        await DragAsync(row, e);
    }

    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressed = null;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || OnToggle(e.Source) || CardAt(e.Source) is not { } row)
            return;
        _pressed = row;
        _pressedAt = e.GetPosition(this);
    }

    private async void OnCardMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is not { } row || _dragging is not null)
            return;
        var delta = e.GetPosition(this) - _pressedAt;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
            return;
        _pressed = null;
        await DragAsync(row, e);
    }

    private void OnCardReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressed is { } row && _vm is not null && CardAt(e.Source) == row)
            _vm.Selected = row;
        _pressed = null;
    }

    private void OnCardKey(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && CardAt(e.Source) is { } row && _vm is not null)
        {
            _vm.Selected = row;
            e.Handled = true;
        }
    }

    private async Task DragAsync(ModRowViewModel row, PointerEventArgs e)
    {
        _dragging = row;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(DragFormat, row.Name));
        try
        {
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        }
        finally
        {
            _dragging = null;
        }
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
        if (!e.DataTransfer.Contains(DragFormat) || _dragging is null || _vm is null)
            return;
        e.Handled = true;
        var target = (e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ModRowViewModel ?? CardAt(e.Source);
        if (target is null || target == _dragging)
            return;
        _vm.MoveTo(_dragging, _vm.Rows.IndexOf(target));
    }
}
