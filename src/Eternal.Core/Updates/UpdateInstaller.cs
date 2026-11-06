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
            if (File.Exists(Path.Combine(here, "unins000.exe")))
                return InstallKind.WindowsInstall;
            return LooksLikeRelease(here, "EternalLauncher.exe") ? InstallKind.WindowsLoose : InstallKind.Unmanaged;
        }
        if (OperatingSystem.IsMacOS() || dataHome.Length == 0)
            return InstallKind.Unmanaged;
        var installed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(dataHome, "eternal-launcher", "app")));
        if (string.Equals(here, installed, StringComparison.Ordinal))
            return InstallKind.LinuxInstall;
        return LooksLikeRelease(here, "EternalLauncher") ? InstallKind.LinuxLoose : InstallKind.Unmanaged;
    }

    /// <summary>A release archive unpacked into its own folder: the launcher executable plus the bundled engine.</summary>
    private static bool LooksLikeRelease(string dir, string executable) =>
        File.Exists(Path.Combine(dir, executable))
        && Directory.Exists(Path.Combine(dir, "engine"))
        && (File.Exists(Path.Combine(dir, "engine", "run.py")) || File.Exists(Path.Combine(dir, "engine", "eternal-engine")));

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
            if (kind is InstallKind.LinuxInstall or InstallKind.LinuxLoose)
                Untar(download, unpacked);
            else if (kind is InstallKind.WindowsPortable or InstallKind.WindowsLoose)
                ZipFile.ExtractToDirectory(download, unpacked);
            else
                throw new UpdateException("This copy of the launcher wasn't installed in a way it can update itself.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new UpdateException($"The update couldn't be unpacked: {ex.Message}", ex);
        }

        var marker = kind switch
        {
            InstallKind.LinuxInstall => "install.sh",
            InstallKind.LinuxLoose => "EternalLauncher",
            _ => "EternalLauncher.exe",
        };
        var payload = Directory.EnumerateFiles(unpacked, marker, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2 })
            .FirstOrDefault();
        if (payload is null)
            throw new UpdateException("The update package doesn't look like a launcher build, so it wasn't installed.");
        return new PreparedUpdate(kind, version, kind is InstallKind.LinuxInstall ? payload : Path.GetDirectoryName(payload)!, work);
    }

    /// <summary>
    /// Starts the script that installs <paramref name="update"/> once this process
    /// has exited; with <paramref name="reopen"/> it starts the launcher again afterwards.
    /// </summary>
    public static void Launch(PreparedUpdate update, bool reopen)
    {
        var self = Environment.ProcessPath ?? throw new UpdateException("The launcher couldn't find its own program file.");
        Launch(update, Environment.ProcessId, reopen ? self : null, AppContext.BaseDirectory);
        Log.Info($"Update to {update.Version.ToString(3)} will install when the launcher closes");
    }

    public static void MarkReopen(PreparedUpdate update)
    {
        var self = Environment.ProcessPath ?? throw new UpdateException("The launcher couldn't find its own program file.");
        File.WriteAllText(Path.Combine(update.WorkDir, ReopenFile), self);
    }

    private const string ReopenFile = "reopen.txt";

    internal static void Launch(PreparedUpdate update, int waitFor, string? reopen, string target)
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
                         "-Target", target, "-Result", result, "-Reopen", reopen ?? "" })
                start.ArgumentList.Add(arg);
        }
        else
        {
            var script = Path.Combine(update.WorkDir, "apply.sh");
            File.WriteAllText(script, LinuxScript.ReplaceLineEndings("\n"));
            start = File.Exists("/usr/bin/setsid") ? new ProcessStartInfo("/usr/bin/setsid") : new ProcessStartInfo("/bin/sh");
            if (start.FileName == "/usr/bin/setsid")
                start.ArgumentList.Add("/bin/sh");
            var kind = update.Kind == InstallKind.LinuxInstall ? "install" : "files";
            foreach (var arg in new[] { script, pid, kind, update.Payload, target, result, reopen ?? "" })
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
        # Installs a downloaded launcher update once the launcher has closed, then opens it again if asked.
        # usage: apply.sh <launcher pid> <install|files> <install.sh or new app folder> <app folder> <result file> [launcher to open]
        pid=$1 kind=$2 payload=$3 target=$4 result=$5 reopen=${6:-}
        waited=0
        while kill -0 "$pid" 2>/dev/null; do
            if [ "$waited" -ge 480 ]; then echo "failed:still-running" > "$result"; exit 1; fi
            sleep 0.5
            waited=$((waited + 1))
        done
        if [ "$kind" = files ]; then
            if cp -R "$payload/." "$target/" > "$result.log" 2>&1; then echo ok > "$result"; else echo "failed:$?" > "$result"; exit 1; fi
        else
            if sh "$payload" > "$result.log" 2>&1; then echo ok > "$result"; else echo "failed:$?" > "$result"; exit 1; fi
        fi
        if [ -z "$reopen" ]; then
            marker="$(dirname "$result")/reopen.txt"
            [ -f "$marker" ] && reopen=$(cat "$marker")
        fi
        if [ -n "$reopen" ] && [ -x "$reopen" ]; then nohup "$reopen" > /dev/null 2>&1 & fi
        exit 0

        """;

    private const string WindowsScript = """
        # Installs a downloaded launcher update once the launcher has closed, then opens it again.
        param([int]$Id, [string]$Kind, [string]$Payload, [string]$Target, [string]$Result, [string]$Reopen)
        $ErrorActionPreference = 'Stop'
        $ok = $false
        try {
            Wait-Process -Id $Id -Timeout 240 -ErrorAction SilentlyContinue
            if (Get-Process -Id $Id -ErrorAction SilentlyContinue) { Set-Content -Path $Result -Value 'failed:still-running'; exit 1 }
            if ($Kind -eq 'setup') {
                $setup = Start-Process -FilePath $Payload -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
                if ($setup.ExitCode -ne 0) { Set-Content -Path $Result -Value "failed:$($setup.ExitCode)"; exit 1 }
            } else {
                Copy-Item -Path (Join-Path $Payload '*') -Destination $Target -Recurse -Force
            }
            Set-Content -Path $Result -Value 'ok'
            $ok = $true
        } catch {
            Set-Content -Path $Result -Value 'failed:copy'
            exit 1
        } finally {
            if ($ok) {
                if (-not $Reopen) {
                    $marker = Join-Path (Split-Path $Result) 'reopen.txt'
                    if (Test-Path -LiteralPath $marker) { $Reopen = (Get-Content -LiteralPath $marker -Raw).Trim() }
                }
                if ($Reopen -and (Test-Path -LiteralPath $Reopen)) { Start-Process -FilePath $Reopen }
            }
        }

        """;
}
