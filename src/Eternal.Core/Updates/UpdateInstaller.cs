using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
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
    private const string PendingFile = "pending.txt";

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
        try
        {
            if (Directory.Exists(unpacked))
                Directory.Delete(unpacked, recursive: true);
            Directory.CreateDirectory(unpacked);
            if (kind is InstallKind.LinuxInstall or InstallKind.LinuxLoose)
                Untar(download, unpacked);
            else if (kind is InstallKind.WindowsPortable or InstallKind.WindowsLoose)
                ZipFile.ExtractToDirectory(download, unpacked);
            else
                throw new UpdateException("This copy of the launcher wasn't installed in a way it can update itself.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception)
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

    /// <summary>Notes the verified download, so the next start can install it even if this run ends without doing so.</summary>
    public static void Remember(PreparedUpdate update, string download) =>
        File.WriteAllLines(Path.Combine(update.WorkDir, PendingFile), [Path.GetFileName(download), Sha256Of(download)]);

    /// <summary>The newest remembered download that is still intact and newer than <paramref name="current"/>, unpacked again.</summary>
    public static PreparedUpdate? Resume(string updatesDir, InstallKind kind, Version current)
    {
        if (!Directory.Exists(updatesDir))
            return null;
        foreach (var work in Directory.EnumerateDirectories(updatesDir).OrderByDescending(d => d, StringComparer.Ordinal))
        {
            var pending = Path.Combine(work, PendingFile);
            if (!File.Exists(pending) || !Version.TryParse(Path.GetFileName(work), out var version) || version <= current)
                continue;
            try
            {
                var lines = File.ReadAllLines(pending);
                var download = Path.Combine(work, Path.GetFileName(lines[0]));
                if (lines.Length == 2 && File.Exists(download) && Sha256Of(download) == lines[1])
                    return Prepare(kind, version, download);
                Log.Warn($"The downloaded update to {version.ToString(3)} changed on disk, so it won't be installed.");
            }
            catch (Exception ex) when (ex is UpdateException or IOException or UnauthorizedAccessException or IndexOutOfRangeException)
            {
                Log.Warn($"The downloaded update to {version.ToString(3)} couldn't be picked up again: {ex.Message}");
            }
        }
        return null;
    }

    /// <summary>Whether this copy's own folder can take the new files.</summary>
    public static bool CanWriteTo(string folder)
    {
        var probe = Path.Combine(folder, $".eternal-write-test-{Environment.ProcessId}");
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Sha256Of(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    /// Starts the script that installs <paramref name="update"/> as this process exits;
    /// with <paramref name="reopen"/> it starts the launcher again afterwards.
    /// </summary>
    public static void Launch(PreparedUpdate update, bool reopen)
    {
        var self = Environment.ProcessPath ?? throw new UpdateException("The launcher couldn't find its own program file.");
        Launch(update, Environment.ProcessId, reopen ? self : null, AppContext.BaseDirectory);
        Log.Info($"Update to {update.Version.ToString(3)} starts installing as the launcher closes");
    }

    /// <summary>Writes the install script, so Restart can find out it can't install while the launcher is still open.</summary>
    public static string WriteScript(PreparedUpdate update)
    {
        var script = Path.Combine(update.WorkDir, OperatingSystem.IsWindows() ? "apply.ps1" : "apply.sh");
        // This file is checked out with CRLF on some machines, and sh reads "fi\r" as no fi.
        File.WriteAllText(script, OperatingSystem.IsWindows() ? WindowsScript : LinuxScript.ReplaceLineEndings("\n"));
        return script;
    }

    internal static void Launch(PreparedUpdate update, int waitFor, string? reopen, string target)
    {
        var pid = waitFor.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var result = Path.Combine(update.WorkDir, ResultFile);
        File.Delete(result);
        var script = WriteScript(update);
        ProcessStartInfo start;
        if (OperatingSystem.IsWindows())
        {
            start = new ProcessStartInfo("powershell.exe") { CreateNoWindow = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
                         "-Id", pid, "-Kind", update.Kind == InstallKind.WindowsInstall ? "setup" : "files", "-Payload", update.Payload,
                         "-Target", target, "-Result", result, "-Reopen", reopen ?? "" })
                start.ArgumentList.Add(arg);
        }
        else
        {
            // setsid keeps the script alive after the launcher's session ends.
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
        "running" => "the installer was cut off before it finished",
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
            if [ "$waited" -ge 240 ]; then echo "failed:still-running" > "$result"; exit 1; fi
            sleep 0.5
            waited=$((waited + 1))
        done
        echo running > "$result"
        if [ "$kind" = files ]; then
            saved="$(dirname "$result")/previous"
            rm -rf "$saved"
            mkdir -p "$saved"
            # Only what the package replaces is saved, so data kept next to the launcher stays out of it.
            if ! ( cd "$payload" && find . -type f ) | while IFS= read -r file; do
                if [ -f "$target/$file" ]; then
                    mkdir -p "$saved/$(dirname "$file")" && cp -p "$target/$file" "$saved/$file" || exit 1
                fi
            done; then
                echo "failed:backup" > "$result"
            elif cp -Rf "$payload/." "$target/" > "$result.log" 2>&1; then
                echo ok > "$result"
            else
                code=$?
                cp -Rf "$saved/." "$target/" >> "$result.log" 2>&1
                echo "failed:$code" > "$result"
            fi
        else
            if sh "$payload" > "$result.log" 2>&1; then echo ok > "$result"; else echo "failed:$?" > "$result"; fi
        fi
        if [ -n "$reopen" ] && [ -x "$reopen" ]; then nohup "$reopen" > /dev/null 2>&1 & fi
        exit 0

        """;

    private const string WindowsScript = """
        # Installs a downloaded launcher update once the launcher has closed, then opens it again if asked.
        param([int]$Id, [string]$Kind, [string]$Payload, [string]$Target, [string]$Result, [string]$Reopen)
        $ErrorActionPreference = 'Stop'

        function Get-RelativeFiles([string]$Root) {
            $skip = $Root.TrimEnd([IO.Path]::DirectorySeparatorChar).Length + 1
            Get-ChildItem -LiteralPath $Root -Recurse -Force -File | ForEach-Object { $_.FullName.Substring($skip) }
        }

        function Copy-OneFile([string]$From, [string]$To) {
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($To))
            # An antivirus can hold a file for a moment after the launcher closes.
            for ($attempt = 1; ; $attempt++) {
                try { [IO.File]::Copy($From, $To, $true); break }
                catch { if ($attempt -ge 10) { throw }; Start-Sleep -Seconds 1 }
            }
        }

        $state = 'failed:copy'
        $saved = [IO.Path]::Combine([IO.Path]::GetDirectoryName($Result), 'previous')
        $backedUp = $false
        try {
            Wait-Process -Id $Id -Timeout 120 -ErrorAction SilentlyContinue
            if (Get-Process -Id $Id -ErrorAction SilentlyContinue) {
                $state = 'failed:still-running'
            } elseif ($Kind -eq 'setup') {
                Set-Content -LiteralPath $Result -Value 'running'
                $info = New-Object Diagnostics.ProcessStartInfo $Payload, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
                $info.UseShellExecute = $true
                $setup = [Diagnostics.Process]::Start($info)
                $setup.WaitForExit()
                if ($setup.ExitCode -eq 0) { $state = 'ok' } else { $state = "failed:$($setup.ExitCode)" }
            } else {
                Set-Content -LiteralPath $Result -Value 'running'
                if (Test-Path -LiteralPath $saved) { Remove-Item -LiteralPath $saved -Recurse -Force }
                $files = @(Get-RelativeFiles $Payload)
                foreach ($file in $files) {
                    $current = [IO.Path]::Combine($Target, $file)
                    if ([IO.File]::Exists($current)) { Copy-OneFile $current ([IO.Path]::Combine($saved, $file)) }
                }
                $backedUp = $true
                foreach ($file in $files) { Copy-OneFile ([IO.Path]::Combine($Payload, $file)) ([IO.Path]::Combine($Target, $file)) }
                $state = 'ok'
            }
        } catch {
            if ($backedUp) {
                try {
                    foreach ($file in @(Get-RelativeFiles $saved)) { Copy-OneFile ([IO.Path]::Combine($saved, $file)) ([IO.Path]::Combine($Target, $file)) }
                } catch { $state = 'failed:rollback' }
            }
        } finally {
            Set-Content -LiteralPath $Result -Value $state
            if ($Reopen -and (Test-Path -LiteralPath $Reopen)) {
                $info = New-Object Diagnostics.ProcessStartInfo $Reopen
                $info.WorkingDirectory = [IO.Path]::GetDirectoryName($Reopen)
                $info.UseShellExecute = $true
                [void][Diagnostics.Process]::Start($info)
            }
        }

        """;
}
