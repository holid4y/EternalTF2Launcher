using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LauncherTF2.Core;

public class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            return b ? Visibility.Collapsed : Visibility.Visible;
        }
        return Visibility.Visible;
    }

    // One-way converter — WPF may still call ConvertBack in some binding
    // paths; UnsetValue tells the engine to leave the target unchanged.
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}

public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value == null ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}

public class StringNullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is string text && !string.IsNullOrWhiteSpace(text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}

/// <summary>
/// Visible when the bound string has content; Collapsed otherwise. Mirrors
/// <see cref="StringNullOrEmptyToVisibilityConverter"/> for the opposite case so
/// XAML doesn't need a ConverterParameter trick.
/// </summary>
public class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is string text && !string.IsNullOrWhiteSpace(text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}

/// <summary>
/// Multi-binding equality: returns true when both bound values stringify to the
/// same thing. Used by the Personalization theme cards to highlight the active
/// theme without forcing UI state onto the ThemeDefinition model.
/// </summary>
public class EqualsMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values == null || values.Length < 2) return false;
        return string.Equals(values[0]?.ToString(), values[1]?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts a bool to a <see cref="GridLength"/> for direct binding to
/// <see cref="System.Windows.Controls.ColumnDefinition.Width"/> or
/// <see cref="System.Windows.Controls.RowDefinition.Height"/>.
/// Usage: ConverterParameter="264" → true = GridLength(264), false = GridLength(0).
/// This is more reliable than relying on Visibility=Collapsed to collapse Auto columns,
/// because Visibility on child elements can be slow to propagate through the Grid measure.
/// </summary>
public class BoolToGridLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b
            && parameter is string s
            && double.TryParse(s, System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out var w))
            return new GridLength(w);
        return new GridLength(0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}

/// <summary>
/// Loads an image file into a frozen, fully-decoded BitmapImage. Unlike the
/// default string→ImageSource conversion, this releases the file handle
/// immediately (OnLoad) so the file can be deleted/replaced while displayed —
/// required by the mod thumbnail store. Decodes at card size to save memory.
/// Returns null for missing/unreadable paths.
/// </summary>
public class PathToImageSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            return null;

        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelWidth = 480;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze(); // safe for IsAsync bindings (created off the UI thread)
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}

public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return !boolValue;
        }

        return true;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return !boolValue;
        }

        return false;
    }
}
