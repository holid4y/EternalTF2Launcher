using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Eternal.App.Services;
using Eternal.Core.GameBanana;
using Eternal.Core.Logging;
using Eternal.Core.Mods;
using Eternal.Core.Platform;

namespace Eternal.App.ViewModels;

public enum FileStep { Waiting, Downloading, Installing, Done, Failed }

public sealed partial class FileRow(SubmissionFile file, bool installed) : ObservableObject
{
    [GeneratedRegex("(?<=[_-])")]
    private static partial Regex Words();

    public SubmissionFile File { get; } = file;
    public string Name => File.Name;

    /// <summary>The name cut after each underscore and hyphen, so a long file name wraps between its words.</summary>
    public string[] NameParts => Words().Split(File.Name);

    public string Size => Format.Size(File.Size);
    public string Added => File.Added == DateTimeOffset.UnixEpoch ? "" : File.Added.LocalDateTime.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
    public string Downloads => File.Downloads > 0 ? Format.Plural(File.Downloads, "download", "downloads") : "";
    public string? Description => File.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(File.Description);
    public bool CanInstall => File.CanInstall;

    /// <summary>Why a file can't be installed, in the player's words; empty when it can.</summary>
    public string Blocker => File.Scan switch
    {
        FileScan.Flagged => "GameBanana's virus scan flagged this file",
        FileScan.Pending => "GameBanana is still scanning this file",
        _ when !GameBananaClient.IsInstallable(File.Name) => "Not a ZIP, RAR, 7z or VPK",
        _ when File.Size > GameBananaClient.MaxDownloadBytes => "Too large to download",
        _ => "",
    };

    [ObservableProperty] private bool _picked;
    [ObservableProperty] private bool _installed = installed;
    [ObservableProperty] private FileStep _step;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = "";

    public bool Busy => Step is FileStep.Downloading or FileStep.Installing;
    public bool Failed => Step == FileStep.Failed;

    partial void OnStepChanged(FileStep value)
    {
        OnPropertyChanged(nameof(Busy));
        OnPropertyChanged(nameof(Failed));
    }
}

/// <summary>One GameBanana submission: its pictures, description and files, and installing the files the player picks.</summary>
public sealed partial class SubmissionViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly ModsViewModel _mods;
    private readonly GameBananaClient _client;
    private readonly CancellationTokenSource _closed = new();
    private CancellationTokenSource? _install;

    public SubmissionViewModel(Submission submission, AppServices services, ModsViewModel mods, GameBananaClient client)
    {
        Summary = submission;
        _services = services;
        _mods = mods;
        _client = client;
        _ = LoadAsync();
    }

    public Submission Summary { get; }
    public string Name => Summary.Name;
    public string Author => Summary.Author.Length > 0 ? $"by {Summary.Author}" : "";
    public string Category => Summary.Category;
    public string Likes => Format.Count(Summary.Likes);
    public string Views => Format.Count(Summary.Views);
    public string Updated => MainViewModel.Ago(Summary.Updated);
    public bool CasualReady => Summary.CasualReady;
    public bool Obsolete => Summary.Obsolete;

    public ObservableCollection<string> Screenshots { get; } = [];
    public ObservableCollection<FileRow> Files { get; } = [];

    [ObservableProperty] private bool _loading = true;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string? _shown;
    [ObservableProperty] private string _downloads = "";
    [ObservableProperty] private string? _version;
    [ObservableProperty] private string? _audioPreview;
    [ObservableProperty] private bool _installing;

    public bool HasScreenshots => Screenshots.Count > 1;
    public bool HasText => Text.Length > 0;
    public bool HasFiles => Files.Count > 0;
    public bool NoFiles => !Loading && Error is null && Files.Count == 0;
    public bool CanListen => AudioPreview is not null;
    public bool CanInstall => !Installing && Files.Any(f => f.Picked && f.CanInstall);

    public string InstallLabel
    {
        get
        {
            var picked = Files.Where(f => f.Picked && f.CanInstall).ToList();
            var verb = picked.Count > 0 && picked.All(f => f.Installed) ? "Reinstall" : "Install";
            return picked.Count switch
            {
                0 => "Pick a file to install",
                1 => $"{verb} · {Format.Size(picked[0].File.Size)}",
                var n => $"{verb} {n} files · {Format.Size(picked.Sum(f => f.File.Size))}",
            };
        }
    }

    public string FilesHint => Files.Count(f => f.CanInstall) > 1
        ? "Uploaders often post variants as separate files. Pick the ones you want; the newest is picked for you."
        : "";

    private async Task LoadAsync()
    {
        try
        {
            var details = await _client.DetailsAsync(Summary.Section, Summary.Id, _closed.Token);
            Text = details.Text;
            Downloads = details.Downloads > 0 ? Format.Count(details.Downloads) : "";
            Version = details.Version;
            AudioPreview = details.AudioPreview;
            foreach (var shot in details.Screenshots)
                Screenshots.Add(shot);
            Shown = Screenshots.FirstOrDefault() ?? Summary.Thumbnail;

            var installed = _mods.InstalledSources();
            var newest = details.Files.Where(f => f.CanInstall).MaxBy(f => f.Added);
            foreach (var file in details.Files.OrderByDescending(f => f.Added))
            {
                var row = new FileRow(file, installed.Contains(Summary.SourcePrefix + file.Id)) { Picked = file == newest };
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(FileRow.Picked))
                        RefreshInstallState();
                };
                Files.Add(row);
            }
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested)
        {
        }
        catch (GameBananaException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Loading = false;
            OnPropertyChanged(nameof(HasScreenshots));
            OnPropertyChanged(nameof(HasText));
            OnPropertyChanged(nameof(HasFiles));
            OnPropertyChanged(nameof(NoFiles));
            OnPropertyChanged(nameof(CanListen));
            OnPropertyChanged(nameof(FilesHint));
            RefreshInstallState();
        }
    }

    partial void OnInstallingChanged(bool value) => RefreshInstallState();

    private void RefreshInstallState()
    {
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(InstallLabel));
        InstallCommand.NotifyCanExecuteChanged();
    }

    internal void RefreshInstalled(IReadOnlySet<string> sources)
    {
        foreach (var row in Files)
            row.Installed = sources.Contains(Summary.SourcePrefix + row.File.Id);
        RefreshInstallState();
    }

    /// <summary>Stops loading the page. An install already running carries on and reports back with a notification.</summary>
    internal void Close() => _closed.Cancel();

    [RelayCommand]
    private void Show(string? screenshot)
    {
        if (screenshot is not null && Screenshots.Contains(screenshot))
            Shown = screenshot;
    }

    [RelayCommand]
    private void OpenPage() => Shell.OpenUrl(Summary.Url);

    [RelayCommand]
    private void Listen()
    {
        if (GameBananaClient.IsGameBananaUrl(AudioPreview))
            Shell.OpenUrl(AudioPreview);
    }

    [RelayCommand]
    private void Cancel() => _install?.Cancel();

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        var rows = Files.Where(f => f.Picked && f.CanInstall).ToList();
        if (rows.Count == 0 || !await _mods.BeginImportAsync())
            return;

        var downloads = Path.Combine(_services.Paths.ImportStagingDir, "downloads");
        var cancel = _install = new CancellationTokenSource();
        Installing = true;
        foreach (var row in rows)
        {
            row.Step = FileStep.Waiting;
            row.Status = "Waiting";
            row.Progress = 0;
        }
        var installed = new List<string>();
        try
        {
            foreach (var row in rows)
            {
                if (cancel.IsCancellationRequested)
                {
                    row.Step = FileStep.Failed;
                    row.Status = "Cancelled";
                    continue;
                }
                installed.AddRange(await InstallFileAsync(row, downloads, cancel.Token));
            }
        }
        finally
        {
            Installing = false;
            _mods.EndImport();
            cancel.Dispose();
            _install = null;
        }

        if (installed.Count > 0)
            _mods.Notify($"Installed {string.Join(", ", installed)}. {(installed.Count == 1 ? "It's" : "They're")} switched on, at the top of your mod list.");
        else if (_closed.IsCancellationRequested && rows.FirstOrDefault(r => r.Failed) is { } failed)
            _mods.Notify($"{Summary.Name} wasn't installed. {failed.Status}", isError: true);
    }

    private async Task<IReadOnlyList<string>> InstallFileAsync(FileRow row, string downloads, CancellationToken cancellation)
    {
        var folder = Path.Combine(downloads, row.File.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            row.Step = FileStep.Downloading;
            row.Status = "Downloading";
            var path = await _client.DownloadAsync(row.File, downloads, new Progress<double>(p =>
            {
                row.Progress = p * 100;
                row.Status = $"Downloading {p:P0}";
            }), cancellation);

            row.Step = FileStep.Installing;
            row.Status = "Installing";
            var result = await _mods.ImportOneAsync(path, Summary.SourcePrefix + row.File.Id, Summary.Name, cancellation);
            if (result.WasCancelled)
            {
                row.Step = FileStep.Failed;
                row.Status = "Not installed: you closed the choice of what to install";
                return [];
            }
            if (result.Imported.Count == 0)
            {
                row.Step = FileStep.Failed;
                row.Status = result.Problems.FirstOrDefault() ?? "Nothing in it could be installed";
                return [];
            }
            row.Step = FileStep.Done;
            row.Installed = true;
            row.Status = result.Problems.Count > 0 ? $"Installed, but: {string.Join(" ", result.Problems)}" : $"Installed as {string.Join(", ", result.Imported)}";
            return result.Imported;
        }
        catch (OperationCanceledException)
        {
            row.Step = FileStep.Failed;
            row.Status = "Cancelled";
            return [];
        }
        catch (GameBananaException ex)
        {
            row.Step = FileStep.Failed;
            row.Status = ex.Message;
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Installing {row.File.Name} failed: {ex.Message}");
            row.Step = FileStep.Failed;
            row.Status = $"Couldn't install: {ex.Message}";
            return [];
        }
        finally
        {
            ArchiveIngest.TryDelete(folder);
        }
    }
}
