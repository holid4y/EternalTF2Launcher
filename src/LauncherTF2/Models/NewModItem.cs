namespace LauncherTF2.Models;

public class NewModItem
{
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string? ThumbnailUrl { get; set; }
    public string ProfileUrl { get; set; } = "";
    public DateTime DateAdded { get; set; }

    public string DateFormatted => DateAdded.ToString("MMM dd");
}
