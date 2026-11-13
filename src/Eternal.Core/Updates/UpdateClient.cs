using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Eternal.Core.Logging;

namespace Eternal.Core.Updates;

/// <summary>
/// The launcher's releases on GitHub. A file is only offered when GitHub lists a
/// SHA-256 for it, and it is only kept once its size and hash match; every hop
/// of a download has to stay on github.com over https.
/// </summary>
public sealed partial class UpdateClient(HttpClient http)
{
    public const string ReleasesPage = "https://github.com/holid4y/EternalTF2Launcher/releases";
    private const string Latest = "https://api.github.com/repos/holid4y/EternalTF2Launcher/releases/latest";
    private const long MaxDownloadBytes = 1L << 30;
    private const int MaxJsonBytes = 1 << 20;
    private const int MaxRedirects = 5;
    private const int MaxNotesChars = 2000;
    private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(60);

    public static UpdateClient Shared { get; } = new(Http.NoRedirects);

    /// <summary>The latest release when it is newer than <paramref name="current"/>; prereleases never count.</summary>
    public async Task<Release?> NewerThanAsync(Version current, CancellationToken cancellation = default)
    {
        string json;
        try
        {
            using var response = await GetAsync(new Uri(Latest), cancellation).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new UpdateException("GitHub's limit on update checks from your connection was reached. The launcher tries again later.");
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new UpdateException("There's no release on GitHub yet.");
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"GitHub answered the update check with an error ({(int)response.StatusCode}). The launcher tries again later.");
            json = await ReadAsync(response.Content, cancellation).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!cancellation.IsCancellationRequested)
        {
            throw new UpdateException("GitHub took too long to answer the update check. The launcher tries again later.", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new UpdateException("GitHub couldn't be reached to check for updates. Check your connection.", ex);
        }

        try
        {
            var release = Parse(json);
            return release is not null && Normal(release.Version) > Normal(current) ? release : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new UpdateException("GitHub sent an update answer the launcher couldn't read. It tries again later.", ex);
        }
    }

    /// <summary>Downloads into <paramref name="folder"/>; the file only appears there once it matches GitHub's size and SHA-256.</summary>
    public async Task<string> DownloadAsync(ReleaseAsset asset, string folder, IProgress<double>? progress, CancellationToken cancellation = default)
    {
        if (asset.Sha256 is null)
            throw new UpdateException("GitHub lists no checksum for this update, so it won't be installed automatically.");
        var target = Path.Combine(folder, Path.GetFileName(asset.Name));
        var partial = target + ".part";
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        try
        {
            Directory.CreateDirectory(folder);
            stall.CancelAfter(StallLimit);
            using var response = await GetAsync(new Uri(asset.Url), stall.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"GitHub answered the update download with an error ({(int)response.StatusCode}). It's tried again later.");
            if (response.Content.Headers.ContentLength > asset.Size)
                throw new UpdateException("The update is bigger than GitHub says it is, so it wasn't downloaded.");

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            long read = 0;
            long reported = 0;
            try
            {
                await using var input = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
                await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                int n;
                while ((n = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
                {
                    stall.CancelAfter(StallLimit);
                    read += n;
                    if (read > asset.Size)
                        throw new UpdateException("The update came in bigger than GitHub says it is, so the download was stopped.");
                    sha.AppendData(buffer, 0, n);
                    await output.WriteAsync(buffer.AsMemory(0, n), stall.Token).ConfigureAwait(false);
                    if (read - reported >= asset.Size / 100 || read == asset.Size)
                    {
                        reported = read;
                        progress?.Report((double)read / asset.Size);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (read != asset.Size)
                throw new UpdateException("The update download stopped early. It's tried again later.");
            if (!Convert.ToHexString(sha.GetHashAndReset()).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("The update arrived damaged (its checksum doesn't match GitHub's), so it was thrown away.");
            File.Move(partial, target, overwrite: true);
            Log.Info($"Downloaded {asset.Name} ({read} bytes)");
            return target;
        }
        catch (OperationCanceledException ex) when (!cancellation.IsCancellationRequested)
        {
            throw new UpdateException("The update download stalled for a minute, so it was stopped.", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or HttpIOException)
        {
            throw new UpdateException("The update download lost its connection to GitHub.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException($"The update couldn't be saved: {ex.Message}", ex);
        }
        finally
        {
            if (File.Exists(partial))
                File.Delete(partial);
        }
    }

    public static bool IsGitHubUrl(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
        && (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellation)
    {
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if ((int)response.StatusCode is < 300 or > 399 || response.Headers.Location is not { } location)
                return response;
            response.Dispose();
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
            if (!IsGitHubUrl(uri))
                throw new UpdateException("The update download was sent somewhere other than GitHub, so it was stopped.");
            if (hop == MaxRedirects)
                throw new UpdateException("GitHub kept redirecting the update download. It's tried again later.");
        }
    }

    private static async Task<string> ReadAsync(HttpContent content, CancellationToken cancellation)
    {
        if (content.Headers.ContentLength > MaxJsonBytes)
            throw new UpdateException("GitHub sent a much longer update answer than expected. It's tried again later.");
        await using var input = await content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var json = new MemoryStream();
        var buffer = new byte[16384];
        int n;
        while ((n = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
        {
            if (json.Length + n > MaxJsonBytes)
                throw new UpdateException("GitHub sent a much longer update answer than expected. It's tried again later.");
            json.Write(buffer, 0, n);
        }
        return Encoding.UTF8.GetString(json.GetBuffer(), 0, (int)json.Length);
    }

    internal static Release? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Bool(root, "prerelease") || Bool(root, "draft"))
            return null;
        if (!Version.TryParse(Text(root, "tag_name")?.TrimStart('v', 'V'), out var version) || version.Major < 0)
            return null;
        var page = Text(root, "html_url") is { } url && Uri.TryCreate(url, UriKind.Absolute, out var pageUri) && IsGitHubUrl(pageUri)
            ? url
            : ReleasesPage;
        var assets = root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(Asset).OfType<ReleaseAsset>().ToArray()
            : [];
        return new Release(version, page, Notes(Text(root, "body")), assets);
    }

    private static ReleaseAsset? Asset(JsonElement a)
    {
        if (Text(a, "name") is not { Length: > 0 } name || name.Contains('/') || name.Contains('\\')
            || Text(a, "browser_download_url") is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsGitHubUrl(uri)
            || !a.TryGetProperty("size", out var size) || !size.TryGetInt64(out var bytes) || bytes is <= 0 or > MaxDownloadBytes)
            return null;
        var digest = Text(a, "digest") is { } d && Sha256Digest().Match(d) is { Success: true } m ? m.Groups[1].Value : null;
        return new ReleaseAsset(name, url, bytes, digest);
    }

    private static string Notes(string? body)
    {
        var text = (body ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        return text.Length > MaxNotesChars ? text[..MaxNotesChars].TrimEnd() + "…" : text;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static Version Normal(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    [GeneratedRegex("^sha256:([0-9a-fA-F]{64})$")]
    private static partial Regex Sha256Digest();
}
