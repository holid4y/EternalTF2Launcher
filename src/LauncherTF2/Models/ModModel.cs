using LauncherTF2.Core;
using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;

namespace LauncherTF2.Models;

/// <summary>
/// Represents a locally installed mod (VPK file or folder).
/// </summary>
public class ModModel : ViewModelBase
{
    private string _name = string.Empty;
    private string _author = "Unknown";
    private string _description = string.Empty;
    private string _version = "1.0.0";
    private bool _isEnabled;
    private string _thumbnailPath = "";
    private string _modPath = string.Empty;
    private DateTime _lastModified;
    private ModType _modType = ModType.Unknown;
    private string? _loadError;
    private ObservableCollection<string> _categories = new();
    private long _sizeBytes;
    private bool _isEnriched;
    private BitmapImage? _thumbnailImage;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Author
    {
        get => _author;
        set => SetProperty(ref _author, value);
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public string Version
    {
        get => _version;
        set => SetProperty(ref _version, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public string ThumbnailPath
    {
        get => _thumbnailPath;
        set => SetProperty(ref _thumbnailPath, value);
    }

    public string ModPath
    {
        get => _modPath;
        set => SetProperty(ref _modPath, value);
    }

    public DateTime LastModified
    {
        get => _lastModified;
        set => SetProperty(ref _lastModified, value);
    }

    public ModType ModType
    {
        get => _modType;
        set => SetProperty(ref _modType, value);
    }

    public string? LoadError
    {
        get => _loadError;
        set => SetProperty(ref _loadError, value);
    }

    public ObservableCollection<string> Categories
    {
        get => _categories;
        set => SetProperty(ref _categories, value);
    }

    /// <summary>
    /// Total size of the mod in bytes (file or folder).
    /// </summary>
    public long SizeBytes
    {
        get => _sizeBytes;
        set => SetProperty(ref _sizeBytes, value);
    }

    /// <summary>
    /// True once GameBanana metadata (thumbnail + author) has been applied.
    /// Used by the UI to show a subtle indicator or animate the card.
    /// </summary>
    public bool IsEnriched
    {
        get => _isEnriched;
        set => SetProperty(ref _isEnriched, value);
    }

    /// <summary>
    /// Pre-built BitmapImage set by the enrichment service after downloading
    /// the GameBanana thumbnail. Using BitmapImage directly avoids WPF's
    /// string-to-ImageSource TypeConverter URI caching issues.
    /// Null until enrichment completes.
    /// </summary>
    public BitmapImage? ThumbnailImage
    {
        get => _thumbnailImage;
        set => SetProperty(ref _thumbnailImage, value);
    }

    /// <summary>
    /// Human-readable label for the mod type (VPK, Folder, etc).
    /// </summary>
    public string TypeLabel => ModType switch
    {
        ModType.Vpk => "VPK",
        ModType.Folder => "Folder",
        _ => "Custom"
    };

    /// <summary>
    /// Human-readable file size string.
    /// </summary>
    public string SizeLabel
    {
        get
        {
            if (SizeBytes <= 0) return "—";
            if (SizeBytes < 1024) return $"{SizeBytes} B";
            if (SizeBytes < 1024 * 1024) return $"{SizeBytes / 1024.0:F1} KB";
            if (SizeBytes < 1024 * 1024 * 1024) return $"{SizeBytes / (1024.0 * 1024):F1} MB";
            return $"{SizeBytes / (1024.0 * 1024 * 1024):F2} GB";
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_loadError);
}

public enum ModType
{
    Unknown,
    Vpk,
    Folder,
    Plugin,
    Custom
}
