using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Eternal.App.ViewModels;

namespace Eternal.App.Views;

/// <summary>
/// <c>views:Hold.WhileUsed="True"</c> keeps a notification up while the pointer is over
/// it or the keyboard is in it, by holding its <see cref="IHoldable"/> data context.
/// </summary>
public static class Hold
{
    public static readonly AttachedProperty<bool> WhileUsedProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("WhileUsed", typeof(Hold));

    static Hold()
    {
        WhileUsedProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is not true)
                return;
            // Read once the pointer and focus state has settled, not mid-event.
            void Later() => Dispatcher.UIThread.Post(() => Update(control));
            control.PointerEntered += (_, _) => Later();
            control.PointerExited += (_, _) => Later();
            control.GotFocus += (_, _) => Later();
            control.LostFocus += (_, _) => Later();
        });
    }

    public static bool GetWhileUsed(Control control) => control.GetValue(WhileUsedProperty);

    public static void SetWhileUsed(Control control, bool value) => control.SetValue(WhileUsedProperty, value);

    private static void Update(Control control)
    {
        if (control.DataContext is IHoldable held)
            held.Held = control.IsPointerOver || control.IsKeyboardFocusWithin;
    }
}
