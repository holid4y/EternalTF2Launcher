namespace Eternal.Core.GameBanana;

/// <summary>GameBanana keeps mods and sounds as separate submission types, each with its own ids and categories.</summary>
public enum Section { Mods, Sounds }

public enum BrowseSort { MostDownloaded, MostLiked, MostViewed, RecentlyUpdated, Newest, Name }

public sealed record Category(long Id, string Name, int ItemCount, int ChildCount);

public sealed record BrowseQuery(Section Section, long? CategoryId = null, string Search = "", BrowseSort Sort = BrowseSort.MostDownloaded, int Page = 1);

public sealed record BrowsePage(IReadOnlyList<Submission> Items, int Total, int Page, int PageCount);

public sealed record Submission(
    long Id,
    Section Section,
    string Name,
    string Author,
    string? Thumbnail,
    string Category,
    DateTimeOffset Updated,
    int Likes,
    int Views,
    bool CasualReady,
    bool Obsolete)
{
    public string Url => $"https://gamebanana.com/{(Section == Section.Sounds ? "sounds" : "mods")}/{Id}";

    /// <summary>What <see cref="Mods.ModEntry.Source"/> records for mods installed from this submission.</summary>
    public string SourcePrefix => Section == Section.Sounds ? $"gamebanana-sound:{Id}:" : $"gamebanana:{Id}:";
}

public sealed record SubmissionDetails(
    Submission Summary,
    string Text,
    IReadOnlyList<string> Screenshots,
    string? AudioPreview,
    int Downloads,
    string? Version,
    IReadOnlyList<SubmissionFile> Files);

public enum FileScan { Clean, Pending, Flagged }

public sealed record SubmissionFile(long Id, string Name, long Size, string? Description, DateTimeOffset Added, int Downloads, string? Md5, FileScan Scan)
{
    public string DownloadUrl => $"https://gamebanana.com/dl/{Id}";
    public bool CanInstall => Scan == FileScan.Clean && GameBananaClient.IsInstallable(Name) && Size <= GameBananaClient.MaxDownloadBytes;
}
