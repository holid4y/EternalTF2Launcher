using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Eternal.Core.GameFiles;

namespace Eternal.App.ViewModels;

public sealed record FoundChoice(FoundAction Action, string Label);

public enum FoundAction { Keep, Import, SetAside }

public sealed partial class FoundRowViewModel : ObservableObject
{
    private static readonly FoundChoice Keep = new(FoundAction.Keep, "Keep as it is");
    private static readonly FoundChoice Import = new(FoundAction.Import, "Move into Eternal");
    private static readonly FoundChoice Aside = new(FoundAction.SetAside, "Put aside");

    public FoundRowViewModel(FoundItem item, bool canImport, Action changed)
    {
        Item = item;
        Choices = canImport && item.CanImport ? [Keep, Import, Aside] : [Keep, Aside];
        _choice = Keep;
        PropertyChanged += (_, _) => changed();
    }

    public FoundItem Item { get; }
    public IReadOnlyList<FoundChoice> Choices { get; }
    public string Name => Item.Name;

    public string Detail => $"{Item.Kind switch
    {
        FoundKind.Hud => "HUD",
        FoundKind.Sounds => "Hit or kill sound",
        FoundKind.Config => "Config",
        FoundKind.Mastercomfig => "mastercomfig",
        FoundKind.OldPreloader => "Replaced the first time you apply mods here",
        FoundKind.Other => "Other file",
        _ => Item.IsFolder ? "Mod folder" : "VPK",
    }} · {Format.Size(Item.Size)}";

    [ObservableProperty] private FoundChoice _choice;
}

public sealed record GameFolderChoices(IReadOnlyList<FoundRowViewModel> Rows, bool ManageAutoexec, bool ReadAutoexec);

/// <summary>Shows what is already in the game's custom and cfg folders and asks what to do with it before the launcher writes anything.</summary>
public sealed partial class GameFolderReviewViewModel : ObservableObject
{
    private readonly TaskCompletionSource<GameFolderChoices> _result = new();

    private readonly bool _canImport;

    public GameFolderReviewViewModel(string tfPath, IReadOnlyList<FoundItem> found, bool manageAutoexec, Eternal.Core.Games.GameDefinition game)
    {
        _canImport = game.Has(Eternal.Core.Games.GameFeatures.Mods);
        CustomLabel = $"In {game.ModDir}/custom";
        ConfigLabel = $"In {game.ModDir}/cfg";
        Help = (_canImport ? game == Eternal.Core.Games.GameCatalog.Tf2
                ? "Move into Eternal adds a mod to your list so it loads in Casual too. "
                : "Move into Eternal adds a mod to your list. "
            : "")
            + "Put aside moves it to a backup you can open from the Launcher tab. Nothing gets deleted.";
        Message = $"These are already in {tfPath}. Nothing has been touched, and only what you change here will be.";
        Mods = found.Where(f => f.Area == "custom").OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(Row).ToArray();
        Configs = found.Where(f => f.Area == "cfg").OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(Row).ToArray();
        HasAutoexec = found.Any(f => f.Area == "cfg" && f.Name.Equals("autoexec.cfg", StringComparison.OrdinalIgnoreCase));
        _manageAutoexec = manageAutoexec;
    }

    private FoundRowViewModel Row(FoundItem item) => new(item, _canImport, () => OnPropertyChanged(nameof(AnyChanged)));

    public string Message { get; }
    public string CustomLabel { get; }
    public string ConfigLabel { get; }
    public string Help { get; }
    public IReadOnlyList<FoundRowViewModel> Mods { get; }
    public IReadOnlyList<FoundRowViewModel> Configs { get; }
    public bool HasMods => Mods.Count > 0;
    public bool HasConfigs => Configs.Count > 0;
    public bool HasAutoexec { get; }
    public bool AnyChanged => Mods.Concat(Configs).Any(r => r.Choice.Action != FoundAction.Keep);
    public Task<GameFolderChoices> Result => _result.Task;

    [ObservableProperty] private bool _manageAutoexec;
    [ObservableProperty] private bool _readAutoexec;

    [RelayCommand]
    private void KeepEverything()
    {
        foreach (var row in Mods.Concat(Configs))
            row.Choice = row.Choices[0];
        Finish();
    }

    [RelayCommand]
    private void Apply() => Finish();

    private void Finish() => _result.TrySetResult(new GameFolderChoices([.. Mods, .. Configs], ManageAutoexec, ReadAutoexec && HasAutoexec));
}
