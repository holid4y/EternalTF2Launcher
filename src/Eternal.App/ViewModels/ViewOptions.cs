using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Eternal.Core;

namespace Eternal.App.ViewModels;

public sealed partial class ViewOptions : ObservableObject
{
    private readonly LauncherSettingsStore _store;
    private readonly Func<LauncherSettings, BrowseView> _pick;

    public ViewOptions(LauncherSettingsStore store, Func<LauncherSettings, BrowseView> pick, double textHeight, double thumbRatio = 9.0 / 16)
    {
        _store = store;
        _pick = pick;
        TextHeight = textHeight;
        ThumbRatio = thumbRatio;
        var saved = pick(store.Settings);
        _layout = saved.Layout;
        _cardWidth = saved.CardWidth;
    }

    public double MinWidth => BrowseView.MinCardWidth;
    public double MaxWidth => BrowseView.MaxCardWidth;
    public double TextHeight { get; }
    public double ThumbRatio { get; }

    [ObservableProperty] private ViewLayout _layout;
    [ObservableProperty] private double _cardWidth;

    public bool IsGrid => Layout == ViewLayout.Grid;
    public bool IsList => Layout == ViewLayout.List;
    public double ThumbHeight => Math.Round(CardWidth * ThumbRatio);
    public double CardHeight => ThumbHeight + TextHeight;

    [RelayCommand]
    private void ShowGrid() => Layout = ViewLayout.Grid;

    [RelayCommand]
    private void ShowList() => Layout = ViewLayout.List;

    partial void OnLayoutChanged(ViewLayout value)
    {
        OnPropertyChanged(nameof(IsGrid));
        OnPropertyChanged(nameof(IsList));
        _store.Update(s => _pick(s).Layout = value);
    }

    partial void OnCardWidthChanged(double value)
    {
        var clamped = Math.Round(Math.Clamp(value, MinWidth, MaxWidth));
        if (clamped != value)
        {
            CardWidth = clamped;
            return;
        }
        OnPropertyChanged(nameof(ThumbHeight));
        OnPropertyChanged(nameof(CardHeight));
        _store.Update(s => _pick(s).CardWidth = clamped);
    }
}
