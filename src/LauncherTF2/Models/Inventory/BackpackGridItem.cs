using System.Windows.Media.Imaging;
using LauncherTF2.Core;

namespace LauncherTF2.Models.Inventory;

/// <summary>
/// View-model for a single inventory grid card. Fields cover everything the UI
/// renders without reaching back into the Steam description payload.
/// </summary>
public class BackpackGridItem : ViewModelBase
{
    private BitmapImage? _image;
    private string _pricePureLabel = string.Empty;

    public string ItemKey { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? ImageUrl { get; init; }
    public string BorderColorHex { get; init; } = "#444444";
    public bool IsEquipped { get; init; }
    public string QualityName { get; init; } = "Unknown";

    /// <summary>Raw Steam-supplied type string (e.g. "Level 5 Primary weapon").</summary>
    public string TypeRaw { get; init; } = "Unknown";

    /// <summary>Categorical type used by filters (e.g. "Primary", "Cosmetic").</summary>
    public string ItemType { get; init; } = "Other";

    /// <summary>Equip slot (e.g. "Primary", "Head"). Null when indeterminate.</summary>
    public string? Slot { get; init; }

    /// <summary>List of TF2 classes the item can equip on; "Multi-Class" if class-agnostic.</summary>
    public IReadOnlyList<string> Classes { get; init; } = Array.Empty<string>();

    public string ClassesLabel => Classes.Count == 0 ? string.Empty : string.Join(", ", Classes);

    public bool Tradable { get; init; }
    public string TradableLabel => Tradable ? "Tradable" : "Not tradable";

    public string? Rarity { get; init; }
    public string? UnusualEffect { get; init; }
    public string? Paint { get; init; }
    public string? KillstreakTier { get; init; }
    public string? KillstreakSheen { get; init; }
    public string? Killstreaker { get; init; }
    public string? Spell { get; init; }
    public string? CraftNumber { get; init; }
    public string? EquippedOn { get; init; }
    public string? Description { get; init; }

    public IReadOnlyList<ItemDescriptionLine> DescriptionLines =>
        _descriptionLines ??= ItemDescriptionParser.Parse(Description);
    private IReadOnlyList<ItemDescriptionLine>? _descriptionLines;

    public bool HasDescriptionLines => DescriptionLines.Count > 0;

    /// <summary>True when the item bears any visible accent (paint/sheen/spell/festive).</summary>
    public bool HasAccent =>
        !string.IsNullOrWhiteSpace(Paint) ||
        !string.IsNullOrWhiteSpace(KillstreakSheen) ||
        !string.IsNullOrWhiteSpace(Spell) ||
        !string.IsNullOrWhiteSpace(UnusualEffect);

    /// <summary>Lazy-loaded BitmapImage, populated by InventoryImageCache.</summary>
    public BitmapImage? Image
    {
        get => _image;
        set
        {
            if (SetProperty(ref _image, value))
                OnPropertyChanged(nameof(HasImage));
        }
    }

    public bool HasImage => _image != null;

    /// <summary>
    /// Compact pricing label shown as a small badge on the card.
    /// Empty string means "no price visible on card".
    /// </summary>
    public string PricePureLabel
    {
        get => _pricePureLabel;
        set => SetProperty(ref _pricePureLabel, value);
    }
}
