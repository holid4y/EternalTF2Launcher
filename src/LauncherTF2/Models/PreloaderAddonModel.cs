using LauncherTF2.Core;

namespace LauncherTF2.Models;

/// <summary>
/// One mod in the Casual Preloader engine library, as shown in the
/// "Casual Preload" panel of the Mods tab.
/// </summary>
public class PreloaderAddonModel : ViewModelBase
{
    private bool _isEnabled;

    /// <summary>Folder name in the engine library — the identifier passed to CLI commands.</summary>
    public string Name { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Type { get; init; } = "Unknown";

    /// <summary>Particle mods have no enable toggle — they are always applied when the preload is installed.</summary>
    public bool IsParticleMod { get; init; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    /// <summary>
    /// Forces the bound toggle to re-read <see cref="IsEnabled"/> even when the
    /// value didn't change. Used to snap the checkbox back after a failed
    /// enable/disable, since the OneWay binding won't re-assert an unchanged value.
    /// </summary>
    public void RaiseEnabledChanged() => OnPropertyChanged(nameof(IsEnabled));

    public string TypeLabel => IsParticleMod ? "PARTICLES" : Type.ToUpperInvariant();

    /// <summary>Read-only compatibility verdict from the engine `compat` scan.</summary>
    public enum CompatStatus { Compatible, Warning, Incompatible }

    private CompatStatus _compat = CompatStatus.Compatible;
    public CompatStatus Compat
    {
        get => _compat;
        set
        {
            if (SetProperty(ref _compat, value))
                OnPropertyChanged(nameof(HasCompatIssue));
        }
    }

    /// <summary>Human-readable reason(s) for the compatibility verdict (tooltip).</summary>
    private string? _compatMessage;
    public string? CompatMessage
    {
        get => _compatMessage;
        set => SetProperty(ref _compatMessage, value);
    }

    public bool HasCompatIssue => _compat != CompatStatus.Compatible;

    private string? _thumbnailPath;

    /// <summary>
    /// Absolute path of the user-chosen card thumbnail, or null for the themed
    /// placeholder. Resolved/written by the ViewModel via <c>ThumbnailStore</c>
    /// (launcher AppData) — never a file inside the engine's mod library.
    /// </summary>
    public string? ThumbnailPath
    {
        get => _thumbnailPath;
        set => SetProperty(ref _thumbnailPath, value);
    }
}
