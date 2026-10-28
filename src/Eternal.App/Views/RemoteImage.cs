using Avalonia;
using Avalonia.Controls;
using Eternal.App.Services;

namespace Eternal.App.Views;

/// <summary>
/// Image.Source from an https URL, loaded off the UI thread through the
/// shared cache. Works with recycled list items: a late result for an old
/// URL is dropped.
/// </summary>
public static class RemoteImage
{
    public static ImageCache? Cache { get; set; }

    public static readonly AttachedProperty<string?> UrlProperty =
        AvaloniaProperty.RegisterAttached<Image, string?>("Url", typeof(RemoteImage));

    public static readonly AttachedProperty<int> DecodeWidthProperty =
        AvaloniaProperty.RegisterAttached<Image, int>("DecodeWidth", typeof(RemoteImage), 256);

    static RemoteImage()
    {
        UrlProperty.Changed.AddClassHandler<Image>(OnUrlChanged);
    }

    public static string? GetUrl(Image image) => image.GetValue(UrlProperty);
    public static void SetUrl(Image image, string? value) => image.SetValue(UrlProperty, value);
    public static int GetDecodeWidth(Image image) => image.GetValue(DecodeWidthProperty);
    public static void SetDecodeWidth(Image image, int value) => image.SetValue(DecodeWidthProperty, value);

    private static async void OnUrlChanged(Image image, AvaloniaPropertyChangedEventArgs e)
    {
        image.Source = null;
        if (e.NewValue is not string url || Cache is null)
            return;
        var bitmap = await Cache.GetAsync(url, GetDecodeWidth(image));
        if (GetUrl(image) == url)
            image.Source = bitmap;
    }
}
