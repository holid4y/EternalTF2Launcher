using LauncherTF2.Core;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace LauncherTF2.Services;

public record UpdateInfo(string LatestVersion, string HtmlUrl);

public class UpdateCheckerService
{
    private const string ApiUrl =
        "https://api.github.com/repos/UnusualHatter/EternalTF2Launcher/releases/latest";

    private static readonly HttpClient _http = AppHttpClient.Instance;

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);

    public async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            var json = await _http.GetStringAsync(ApiUrl);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString() ?? string.Empty;
            var htmlUrl = root.GetProperty("html_url").GetString() ?? string.Empty;

            // Strip any non-numeric prefix: "Alpha-v1.2.0" → "1.2.0"
            var versionStr = new string(tagName.SkipWhile(c => !char.IsDigit(c)).ToArray());

            // Normalise to 4 parts so Version comparison works correctly:
            // "1.0.0" → "1.0.0.0" matches an assembly version of 1.0.0.0
            if (versionStr.Count(c => c == '.') == 2)
                versionStr += ".0";

            if (!Version.TryParse(versionStr, out var latest))
            {
                Logger.LogWarning($"[Update] Could not parse version from tag '{tagName}'");
                return null;
            }

            var current = CurrentVersion;
            Logger.LogInfo($"[Update] Current: {current}  Latest: {latest}");

            return latest > current ? new UpdateInfo(tagName, htmlUrl) : null;
        }
        catch (Exception ex)
        {
            Logger.LogWarning("[Update] Check failed", ex);
            return null;
        }
    }
}
