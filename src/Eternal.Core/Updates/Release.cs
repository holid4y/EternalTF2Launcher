namespace Eternal.Core.Updates;

/// <summary>How this copy was installed, which decides how it can update itself.</summary>
public enum InstallKind
{
    /// <summary>Put in place by install.sh; an update runs the new package's install.sh.</summary>
    LinuxInstall,
    /// <summary>Installed by the Windows setup; an update runs the new setup silently.</summary>
    WindowsInstall,
    /// <summary>The portable zip with portable.txt; an update unpacks over it.</summary>
    WindowsPortable,
    /// <summary>A build run from somewhere else (a source checkout, an unpacked archive); it only says an update exists.</summary>
    Unmanaged,
}

public sealed record ReleaseAsset(string Name, string Url, long Size, string? Sha256);

public sealed record Release(Version Version, string PageUrl, string Notes, IReadOnlyList<ReleaseAsset> Assets)
{
    public string VersionText => Version.ToString(3);

    /// <summary>The file this install updates from, or null when there is none it can verify.</summary>
    public ReleaseAsset? AssetFor(InstallKind kind)
    {
        var name = kind switch
        {
            InstallKind.LinuxInstall => $"EternalLauncher-{VersionText}-linux-x64.tar.gz",
            InstallKind.WindowsInstall => $"EternalLauncher-{VersionText}-win-x64-setup.exe",
            InstallKind.WindowsPortable => $"EternalLauncher-{VersionText}-win-x64-portable.zip",
            _ => null,
        };
        return name is null ? null : Assets.FirstOrDefault(a => a.Name == name && a.Sha256 is not null);
    }
}

public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);
