using System.Globalization;
using Avalonia;
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

    public static readonly IMultiValueConverter ItemColor = new FuncMultiValueConverter<object?, IBrush?>(values =>
    {
        var args = values.ToArray();
        var lightGround = args.ElementAtOrDefault(1) is ISolidColorBrush { Color: var ground } && Luminance(ground) > 0.5;
        if (args.ElementAtOrDefault(0) is not string hex || !Color.TryParse(hex, out var c))
            return Application.Current?.Resources.TryGetValue("MutedBrush", out var muted) == true ? muted as IBrush : null;
        var unique = hex.Equals("#7D6D00", StringComparison.OrdinalIgnoreCase);
        if (lightGround)
            return new SolidColorBrush(unique || Luminance(c) < 0.4 ? c : Scale(c, 0.55));
        if (unique)
            return new SolidColorBrush(Color.Parse("#FFD700"));
        return new SolidColorBrush(Luminance(c) >= 0.4 ? c : Color.FromRgb(Lift(c.R), Lift(c.G), Lift(c.B)));

        static byte Lift(byte v) => (byte)(v + (255 - v) * 0.35);
    });

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;

    private static Color Scale(Color c, double f) => Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

    public static readonly IValueConverter Bold =
        new FuncValueConverter<bool, FontWeight>(bold => bold ? FontWeight.Bold : FontWeight.Normal);

    public static readonly IValueConverter Dimmed =
        new FuncValueConverter<bool, double>(dim => dim ? 0.55 : 1);

    public static readonly IValueConverter TabTitle =
        new FuncValueConverter<string, string>(id => id is null ? "" : MainViewModel.TitleOf(id));
}
