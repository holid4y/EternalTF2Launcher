using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Eternal.App.ViewModels;

namespace Eternal.App.Views;

public static class Converters
{
    public static readonly IValueConverter IsPreparing =
        new FuncValueConverter<PlayState, bool>(state => state == PlayState.Preparing);

    public static readonly IValueConverter Percent =
        new FuncValueConverter<double, string>(value => value.ToString("0", CultureInfo.InvariantCulture) + "%");

    public static readonly IValueConverter BackgroundModeName =
        new FuncValueConverter<Eternal.Core.BackgroundMode, string>(mode => mode switch
        {
            Eternal.Core.BackgroundMode.Screenshots => "My TF2 screenshots",
            Eternal.Core.BackgroundMode.Picture => "A picture I choose",
            _ => "None",
        });

    public static readonly IValueConverter TabTitle =
        new FuncValueConverter<string, string>(id => id switch
        {
            "home" => "Home",
            "mods" => "Mods",
            "backpack" => "Backpack",
            "config" => "Config",
            _ => id ?? "",
        });
}
