using System.Globalization;

namespace Eternal.App.ViewModels;

internal static class Format
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Count(long n) => n switch
    {
        >= 1_000_000 => (n / 1_000_000.0).ToString("0.#", Invariant) + "M",
        >= 10_000 => (n / 1000.0).ToString("0", Invariant) + "k",
        >= 1_000 => (n / 1000.0).ToString("0.#", Invariant) + "k",
        _ => n.ToString(Invariant),
    };

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", Invariant) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.#", Invariant) + " MB",
        >= 1L << 10 => (bytes / 1024.0).ToString("0", Invariant) + " KB",
        _ => bytes.ToString(Invariant) + " B",
    };

    public static string Plural(long n, string one, string many) => $"{n.ToString("N0", Invariant)} {(n == 1 ? one : many)}";
}
