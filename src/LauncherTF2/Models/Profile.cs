using System.Text.Json.Serialization;

namespace LauncherTF2.Models;

public sealed class Profile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("isUserCreated")]
    public bool IsUserCreated { get; set; }

    [JsonPropertyName("settings")]
    public Dictionary<string, object?> Settings { get; set; } = new();

    [JsonPropertyName("launchOptions")]
    public string? LaunchOptions { get; set; }

    // User-owned autoexec content captured at save time. When non-null, replaces the current user section on apply.
    [JsonPropertyName("userContent")]
    public string? UserAutoexecContent { get; set; }

    [JsonPropertyName("lastModified")]
    public DateTime LastModified { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    public override string ToString() => Name;
}
