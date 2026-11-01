using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Eternal.Core.Engine;
using Eternal.Core.Mods;

namespace Eternal.App.ViewModels;

public sealed partial class PartChoice(ImportPart part, string alternatives, bool picked) : ObservableObject
{
    public ImportPart Part { get; } = part;
    public string Name => Part.Name;
    public string Alternatives { get; } = alternatives;
    public bool HasAlternatives => Alternatives.Length > 0;

    public string Detail => $"{Part.Kind switch { "vpk" => "VPK", "loose" => "Loose files", _ => "Folder" }} · {Format.Plural(Part.Files, "file", "files")} · {Format.Size(Part.Size)}";

    [ObservableProperty] private bool _picked = picked;
}

/// <summary>Asks which parts of a download to install when it holds more than one mod.</summary>
public sealed partial class ImportChoiceViewModel : ObservableObject
{
    private readonly TaskCompletionSource<IReadOnlyList<string>?> _result = new();

    public ImportChoiceViewModel(ImportPlan plan)
    {
        var names = plan.Parts.GroupBy(p => p.Source).ToDictionary(g => g.Key, g => g.First().Name);
        var picked = ImportPlan.DefaultChoice(plan.Parts).ToHashSet();
        Parts = plan.Parts.Select(p => new PartChoice(p,
            Names(p.Conflicts.Select(c => names.GetValueOrDefault(c)).OfType<string>().ToList()),
            picked.Contains(p.Source))).ToArray();
        foreach (var part in Parts)
            part.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CanInstall));
        Title = $"What to install from {Path.GetFileName(plan.Source)}";
        Message = Parts.Any(p => p.HasAlternatives)
            ? $"It holds {Parts.Count} mods. Some change the same files, so only one of those can show in game; the biggest of each is picked."
            : $"It holds {Parts.Count} mods. Untick any you don't want.";
    }

    private static string Names(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => $"{names[0]}, {names[1]} and {names.Count - 2} more",
    };

    public string Title { get; }
    public string Message { get; }
    public IReadOnlyList<PartChoice> Parts { get; }
    public bool CanInstall => Parts.Any(p => p.Picked);
    public Task<IReadOnlyList<string>?> Result => _result.Task;

    [RelayCommand]
    private void Install()
    {
        if (CanInstall)
            _result.TrySetResult(Parts.Where(p => p.Picked).Select(p => p.Part.Source).ToArray());
    }

    [RelayCommand]
    private void Cancel() => _result.TrySetResult(null);

    [RelayCommand]
    private void PickAll()
    {
        foreach (var part in Parts)
            part.Picked = true;
    }

    [RelayCommand]
    private void PickNone()
    {
        foreach (var part in Parts)
            part.Picked = false;
    }
}
