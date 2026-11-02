using System.Diagnostics;
using System.IO.Compression;
using Eternal.Core.Logging;

namespace Eternal.Core.Updates;

/// <summary>A downloaded update, unpacked and checked, waiting for the launcher to close.</summary>
public sealed record PreparedUpdate(InstallKind Kind, Version Version, string Payload, string WorkDir);

/// <summary>
/// Unpacking happens while the launcher runs, so its problems show in the app; a
/// script installs once the launcher has exited and leaves the result for the next start.
/// </summary>
public static class UpdateInstaller
{
    private const string ResultFile = "result.txt";

    public static InstallKind Detect(bool isPortable) => Detect(
        AppContext.BaseDirectory,
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
        OperatingSystem.IsWindows(),
        isPortable);

    public static InstallKind Detect(string baseDirectory, string dataHome, bool isWindows, bool isPortable)
    {
        var here = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        if (isWindows)
        {
            if (isPortable)
                return InstallKind.WindowsPortable;
            return File.Exists(Path.Combine(here, "unins000.exe")) ? InstallKind.WindowsInstall : InstallKind.Unmanaged;
        }
        if (OperatingSystem.IsMacOS() || dataHome.Length == 0)
            return InstallKind.Unmanaged;
        var installed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(dataHome, "eternal-launcher", "app")));
        return string.Equals(here, installed, StringComparison.Ordinal) ? InstallKind.LinuxInstall : InstallKind.Unmanaged;
    }

    /// <summary>Unpacks <paramref name="download"/> and finds what installs it.</summary>
    public static PreparedUpdate Prepare(InstallKind kind, Version version, string download)
    {
        var work = Path.GetDirectoryName(download)!;
        if (kind == InstallKind.WindowsInstall)
            return new PreparedUpdate(kind, version, download, work);

        var unpacked = Path.Combine(work, "unpacked");
        if (Directory.Exists(unpacked))
            Directory.Delete(unpacked, recursive: true);
        Directory.CreateDirectory(unpacked);
        try
        {
            if (kind == InstallKind.LinuxInstall)
                Untar(download, unpacked);
            else if (kind == InstallKind.WindowsPortable)
                ZipFile.ExtractToDirectory(download, unpacked);
            else
                throw new UpdateException("This copy of the launcher wasn't installed in a way it can update itself.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new UpdateException($"The update couldn't be unpacked: {ex.Message}", ex);
        }

        var marker = kind == InstallKind.LinuxInstall ? "install.sh" : "EternalLauncher.exe";
        var payload = Directory.EnumerateFiles(unpacked, marker, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2 })
            .FirstOrDefault();
        if (payload is null)
            throw new UpdateException("The update package doesn't look like a launcher build, so it wasn't installed.");
        return new PreparedUpdate(kind, version, kind == InstallKind.LinuxInstall ? payload : Path.GetDirectoryName(payload)!, work);
    }

    /// <summary>
    /// Starts the script that installs <paramref name="update"/> once this process
    /// has exited; with <paramref name="reopen"/> it starts the launcher again afterwards.
    /// </summary>
    public static void Launch(PreparedUpdate update, bool reopen)
    {
        var self = Environment.ProcessPath ?? throw new UpdateException("The launcher couldn't find its own program file.");
        Launch(update, Environment.ProcessId, reopen ? self : null);
        Log.Info($"Update to {update.Version.ToString(3)} will install when the launcher closes");
    }

    internal static void Launch(PreparedUpdate update, int waitFor, string? reopen)
    {
        var pid = waitFor.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var result = Path.Combine(update.WorkDir, ResultFile);
        File.Delete(result);
        ProcessStartInfo start;
        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(update.WorkDir, "apply.ps1");
            File.WriteAllText(script, WindowsScript);
            start = new ProcessStartInfo("powershell.exe") { CreateNoWindow = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
                         "-Id", pid, "-Kind", update.Kind == InstallKind.WindowsInstall ? "setup" : "files", "-Payload", update.Payload,
                         "-Target", AppContext.BaseDirectory, "-Result", result, "-Reopen", reopen ?? "" })
                start.ArgumentList.Add(arg);
        }
        else
        {
            var script = Path.Combine(update.WorkDir, "apply.sh");
            File.WriteAllText(script, LinuxScript);
            // setsid keeps the script alive after the launcher's session ends.
            start = File.Exists("/usr/bin/setsid") ? new ProcessStartInfo("/usr/bin/setsid") : new ProcessStartInfo("/bin/sh");
            if (start.FileName == "/usr/bin/setsid")
                start.ArgumentList.Add("/bin/sh");
            foreach (var arg in new[] { script, pid, update.Payload, result, reopen ?? "" })
                start.ArgumentList.Add(arg);
        }
        start.UseShellExecute = false;
        using var process = Process.Start(start) ?? throw new UpdateException("The update installer couldn't be started.");
    }

    /// <summary>How the last update went, once; null when none ran since.</summary>
    public static (Version Version, string? Problem)? TakeResult(string updatesDir)
    {
        if (!Directory.Exists(updatesDir))
            return null;
        foreach (var work in Directory.EnumerateDirectories(updatesDir))
        {
            var file = Path.Combine(work, ResultFile);
            if (!File.Exists(file) || !Version.TryParse(Path.GetFileName(work), out var version))
                continue;
            var text = File.ReadAllText(file).Trim();
            return (version, text == "ok" ? null : Problem(text));
        }
        return null;
    }

    private static string Problem(string result) => result switch
    {
        "failed:still-running" => "the launcher didn't close in time",
        _ when result.StartsWith("failed:", StringComparison.Ordinal) => $"the installer stopped ({result[7..]})",
        _ => "the installer didn't finish",
    };

    private static void Untar(string archive, string into)
    {
        var tar = new ProcessStartInfo("tar") { UseShellExecute = false, RedirectStandardError = true };
        foreach (var arg in new[] { "-xzf", archive, "-C", into, "--no-same-owner" })
            tar.ArgumentList.Add(arg);
        using var process = Process.Start(tar) ?? throw new UpdateException("tar couldn't be started to unpack the update.");
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new UpdateException($"The update couldn't be unpacked: {error.Trim()}");
    }

    private const string LinuxScript = """
        #!/bin/sh
        # Installs a downloaded launcher update once the launcher has closed, then opens it again.
        # usage: apply.sh <launcher pid> <the new install.sh> <result file> [launcher to open]
        pid=$1 installer=$2 result=$3 reopen=${4:-}
        waited=0
        while kill -0 "$pid" 2>/dev/null; do
            if [ "$waited" -ge 120 ]; then echo "failed:still-running" > "$result"; exit 1; fi
            sleep 0.5
            waited=$((waited + 1))
        done
        if sh "$installer" > "$result.log" 2>&1; then echo ok > "$result"; else echo "failed:$?" > "$result"; fi
        if [ -n "$reopen" ] && [ -x "$reopen" ]; then nohup "$reopen" > /dev/null 2>&1 & fi
        exit 0

        """;

    private const string WindowsScript = """
        # Installs a downloaded launcher update once the launcher has closed, then opens it again.
        param([int]$Id, [string]$Kind, [string]$Payload, [string]$Target, [string]$Result, [string]$Reopen)
        $ErrorActionPreference = 'Stop'
        try {
            Wait-Process -Id $Id -Timeout 60 -ErrorAction SilentlyContinue
            if (Get-Process -Id $Id -ErrorAction SilentlyContinue) { Set-Content -Path $Result -Value 'failed:still-running'; exit 1 }
            if ($Kind -eq 'setup') {
                $setup = Start-Process -FilePath $Payload -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
                if ($setup.ExitCode -ne 0) { Set-Content -Path $Result -Value "failed:$($setup.ExitCode)"; exit 1 }
            } else {
                Copy-Item -Path (Join-Path $Payload '*') -Destination $Target -Recurse -Force
            }
            Set-Content -Path $Result -Value 'ok'
        } catch {
            Set-Content -Path $Result -Value 'failed:copy'
            exit 1
        } finally {
            if ($Reopen -and (Test-Path -LiteralPath $Reopen)) { Start-Process -FilePath $Reopen }
        }

        """;
}
