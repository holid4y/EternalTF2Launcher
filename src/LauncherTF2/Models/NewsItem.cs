namespace LauncherTF2.Models;

public class NewsItem
{
    public string Title { get; set; } = "";
    public string Contents { get; set; } = "";
    public DateTime Date { get; set; }
    public string Url { get; set; } = "";
    public string FeedLabel { get; set; } = "";

    /// <summary>
    /// First image URL extracted from the article HTML, or null when the article
    /// has no inline images. The UI hides the image column when this is null so
    /// cards without images look clean rather than showing a broken placeholder.
    /// </summary>
    public string? ImageUrl { get; set; }

    public string DateFormatted => Date.ToString("MMM dd, yyyy");
}
