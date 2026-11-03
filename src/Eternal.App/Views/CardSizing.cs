using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Eternal.App.ViewModels;

namespace Eternal.App.Views;

/// <summary>
/// Keeps an ItemsRepeater's UniformGridLayout at the card size chosen in
/// <see cref="ViewOptions"/>. Layouts can't hold bindings, so this does it for them.
/// </summary>
public static class CardSizing
{
    private static readonly ConditionalWeakTable<ItemsRepeater, PropertyChangedEventHandler> Handlers = new();

    public static readonly AttachedProperty<ViewOptions?> OptionsProperty =
        AvaloniaProperty.RegisterAttached<ItemsRepeater, ViewOptions?>("Options", typeof(CardSizing));

    static CardSizing() => OptionsProperty.Changed.AddClassHandler<ItemsRepeater>(OnOptionsChanged);

    public static ViewOptions? GetOptions(ItemsRepeater repeater) => repeater.GetValue(OptionsProperty);
    public static void SetOptions(ItemsRepeater repeater, ViewOptions? value) => repeater.SetValue(OptionsProperty, value);

    private static void OnOptionsChanged(ItemsRepeater repeater, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is ViewOptions old && Handlers.TryGetValue(repeater, out var handler))
        {
            old.PropertyChanged -= handler;
            Handlers.Remove(repeater);
        }
        if (e.NewValue is not ViewOptions options)
            return;
        PropertyChangedEventHandler update = (_, args) =>
        {
            if (args.PropertyName is nameof(ViewOptions.CardWidth) or nameof(ViewOptions.CardHeight))
                Apply(repeater, options);
        };
        options.PropertyChanged += update;
        Handlers.AddOrUpdate(repeater, update);
        Apply(repeater, options);
    }

    private static void Apply(ItemsRepeater repeater, ViewOptions options)
    {
        if (repeater.Layout is not UniformGridLayout layout)
            return;
        layout.MinItemWidth = options.CardWidth;
        layout.MinItemHeight = options.CardHeight;
    }
}
