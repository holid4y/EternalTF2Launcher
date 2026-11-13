using System.Net;
using System.Security.Cryptography;
using System.Text;
using Eternal.Core.Platform;
using Eternal.Core.Updates;

namespace Eternal.Core.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("eternal-update-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static string ReleaseJson(string tag, byte[] linux, string? digest = null, bool prerelease = false) => $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://github.com/holid4y/EternalTF2Launcher/releases/tag/{{tag}}",
          "prerelease": {{(prerelease ? "true" : "false")}},
          "body": "Custom robots now load on Linux.\r\n\r\nImports keep loose models.",
          "assets": [
            {
              "name": "EternalLauncher-{{tag.TrimStart('v')}}-linux-x64.tar.gz",
              "browser_download_url": "https://github.com/holid4y/EternalTF2Launcher/releases/download/{{tag}}/EternalLauncher-{{tag.TrimStart('v')}}-linux-x64.tar.gz",
              "size": {{linux.Length}},
              "digest": "{{digest ?? "sha256:" + Sha(linux)}}"
            },
            {
              "name": "EternalLauncher-{{tag.TrimStart('v')}}-win-x64-setup.exe",
              "browser_download_url": "https://github.com/holid4y/EternalTF2Launcher/releases/download/{{tag}}/EternalLauncher-{{tag.TrimStart('v')}}-win-x64-setup.exe",
              "size": 10,
              "digest": "sha256:{{new string('a', 64)}}"
            },
            {
              "name": "EternalLauncher-{{tag.TrimStart('v')}}-win-x64.zip",
              "browser_download_url": "https://github.com/holid4y/EternalTF2Launcher/releases/download/{{tag}}/EternalLauncher-{{tag.TrimStart('v')}}-win-x64.zip",
              "size": 20,
              "digest": "sha256:{{new string('b', 64)}}"
            },
            {
              "name": "EternalLauncher-{{tag.TrimStart('v')}}-win-x64-portable.zip",
              "browser_download_url": "https://github.com/holid4y/EternalTF2Launcher/releases/download/{{tag}}/EternalLauncher-{{tag.TrimStart('v')}}-win-x64-portable.zip",
              "size": 21,
              "digest": "sha256:{{new string('c', 64)}}"
            }
          ]
        }
        """;

    private static UpdateClient Serving(Func<HttpRequestMessage, HttpResponseMessage> answer, List<string>? requested = null) =>
        new(new HttpClient(new Handler(answer, requested ?? [])));

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    [Fact]
    public async Task A_newer_release_is_offered_with_the_file_for_this_install()
    {
        var linux = Encoding.UTF8.GetBytes("tarball");

        var release = await Serving(_ => Ok(ReleaseJson("v2.2.0", linux))).NewerThanAsync(new Version(2, 1, 1));

        Assert.NotNull(release);
        Assert.Equal(new Version(2, 2, 0), release.Version);
        Assert.Equal("EternalLauncher-2.2.0-linux-x64.tar.gz", release.AssetFor(InstallKind.LinuxInstall)?.Name);
        Assert.Equal("EternalLauncher-2.2.0-linux-x64.tar.gz", release.AssetFor(InstallKind.LinuxLoose)?.Name);
        Assert.Equal("EternalLauncher-2.2.0-win-x64-setup.exe", release.AssetFor(InstallKind.WindowsInstall)?.Name);
        Assert.Equal("EternalLauncher-2.2.0-win-x64.zip", release.AssetFor(InstallKind.WindowsLoose)?.Name);
        Assert.Equal("EternalLauncher-2.2.0-win-x64-portable.zip", release.AssetFor(InstallKind.WindowsPortable)?.Name);
        Assert.Null(release.AssetFor(InstallKind.Unmanaged));
        Assert.Equal("Custom robots now load on Linux.\n\nImports keep loose models.", release.Notes);
    }

    [Theory]
    [InlineData("v2.1.1")]
    [InlineData("v2.0.9")]
    [InlineData("nightly")]
    public async Task The_same_an_older_or_an_unreadable_version_is_no_update(string tag)
    {
        var release = await Serving(_ => Ok(ReleaseJson(tag, [1]))).NewerThanAsync(new Version(2, 1, 1));

        Assert.Null(release);
    }

    [Fact]
    public async Task A_prerelease_is_no_update()
    {
        Assert.Null(await Serving(_ => Ok(ReleaseJson("v9.0.0", [1], prerelease: true))).NewerThanAsync(new Version(2, 1, 1)));
    }

    [Fact]
    public async Task A_file_without_a_checksum_is_not_offered()
    {
        var release = await Serving(_ => Ok(ReleaseJson("v2.2.0", [1], digest: ""))).NewerThanAsync(new Version(2, 1, 1));

        Assert.NotNull(release);
        Assert.Null(release.AssetFor(InstallKind.LinuxInstall));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "limit")]
    [InlineData(HttpStatusCode.TooManyRequests, "limit")]
    [InlineData(HttpStatusCode.NotFound, "no release")]
    [InlineData(HttpStatusCode.BadGateway, "GitHub")]
    public async Task Each_failed_check_says_what_went_wrong(HttpStatusCode status, string expected)
    {
        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            Serving(_ => new HttpResponseMessage(status)).NewerThanAsync(new Version(2, 1, 1)));

        Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Being_offline_and_a_garbled_answer_read_differently()
    {
        var offline = await Assert.ThrowsAsync<UpdateException>(() =>
            Serving(_ => throw new HttpRequestException("no route")).NewerThanAsync(new Version(2, 1, 1)));
        var garbled = await Assert.ThrowsAsync<UpdateException>(() =>
            Serving(_ => Ok("{ not json")).NewerThanAsync(new Version(2, 1, 1)));

        Assert.NotEqual(offline.Message, garbled.Message);
        Assert.Contains("connection", offline.Message, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Release> ReleaseWith(byte[] linux) =>
        (await Serving(_ => Ok(ReleaseJson("v2.2.0", linux))).NewerThanAsync(new Version(2, 1, 1)))!;

    [Fact]
    public async Task A_download_follows_github_redirects_and_is_checked()
    {
        var linux = Encoding.UTF8.GetBytes("tarball bytes");
        var asset = (await ReleaseWith(linux)).AssetFor(InstallKind.LinuxInstall)!;
        var requested = new List<string>();
        var client = Serving(request => request.RequestUri!.Host == "github.com"
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://release-assets.githubusercontent.com/a/b?sig=1") } }
            : Ok(linux), requested);

        var path = await client.DownloadAsync(asset, _dir, null);

        Assert.Equal(linux, await File.ReadAllBytesAsync(path));
        Assert.Equal(asset.Name, Path.GetFileName(path));
        Assert.Equal(["github.com", "release-assets.githubusercontent.com"], requested.Select(u => new Uri(u).Host));
    }

    [Fact]
    public async Task A_redirect_away_from_github_is_never_followed()
    {
        var asset = (await ReleaseWith([1])).AssetFor(InstallKind.LinuxInstall)!;
        var requested = new List<string>();
        var client = Serving(_ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://evil.example/x") } }, requested);

        var ex = await Assert.ThrowsAsync<UpdateException>(() => client.DownloadAsync(asset, _dir, null));

        Assert.Single(requested);
        Assert.Contains("GitHub", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_damaged_download_is_thrown_away()
    {
        var asset = (await ReleaseWith(Encoding.UTF8.GetBytes("the real file"))).AssetFor(InstallKind.LinuxInstall)!;

        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            Serving(_ => Ok(Encoding.UTF8.GetBytes("a bad file!!!"))).DownloadAsync(asset, _dir, null));

        Assert.Contains("checksum", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_cut_off_download_says_so()
    {
        var asset = (await ReleaseWith(Encoding.UTF8.GetBytes("the whole file"))).AssetFor(InstallKind.LinuxInstall)!;

        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            Serving(_ => Ok(Encoding.UTF8.GetBytes("the"))).DownloadAsync(asset, _dir, null));

        Assert.Contains("stopped early", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_download_the_connection_drops_is_not_blamed_on_saving()
    {
        var asset = (await ReleaseWith(new byte[4096])).AssetFor(InstallKind.LinuxInstall)!;

        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            Serving(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Dropped()) }).DownloadAsync(asset, _dir, null));

        Assert.Contains("connection", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("saved", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories));
    }

    /// <summary>A response body whose connection closes before the announced length arrived.</summary>
    private sealed class Dropped : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_download_bigger_than_announced_is_cut_off()
    {
        var asset = (await ReleaseWith(Encoding.UTF8.GetBytes("short"))).AssetFor(InstallKind.LinuxInstall)!;

        await Assert.ThrowsAsync<UpdateException>(() => Serving(_ => Ok(new byte[4096])).DownloadAsync(asset, _dir, null));

        Assert.Empty(Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("https://github.com/holid4y/EternalTF2Launcher/releases/download/v1/a.tar.gz", true)]
    [InlineData("https://release-assets.githubusercontent.com/x", true)]
    [InlineData("https://objects.githubusercontent.com/x", true)]
    [InlineData("http://github.com/x", false)]
    [InlineData("https://github.com.evil.example/x", false)]
    [InlineData("https://evilgithubusercontent.com/x", false)]
    [InlineData("https://github.com:8443/x", false)]
    public void Only_github_over_https_is_trusted(string url, bool trusted)
    {
        Assert.Equal(trusted, UpdateClient.IsGitHubUrl(new Uri(url)));
    }

    [Fact]
    public void A_linux_install_is_the_folder_install_sh_puts_it_in()
    {
        var data = Path.Combine(_dir, "share");
        var app = Path.Combine(data, "eternal-launcher", "app");

        Assert.Equal(InstallKind.LinuxInstall, UpdateInstaller.Detect(app + "/", data, isWindows: false, isPortable: false));
        Assert.Equal(InstallKind.Unmanaged, UpdateInstaller.Detect(Path.Combine(_dir, "Downloads", "EternalLauncher"), data, isWindows: false, isPortable: false));

        var loose = Directory.CreateDirectory(Path.Combine(_dir, "loose")).FullName;
        File.WriteAllText(Path.Combine(loose, "EternalLauncher"), "");
        Directory.CreateDirectory(Path.Combine(loose, "engine"));
        File.WriteAllText(Path.Combine(loose, "engine", "run.py"), "");
        Assert.Equal(InstallKind.LinuxLoose, UpdateInstaller.Detect(loose, data, isWindows: false, isPortable: false));

        var build = Directory.CreateDirectory(Path.Combine(_dir, "build")).FullName;
        File.WriteAllText(Path.Combine(build, "EternalLauncher"), "");
        Assert.Equal(InstallKind.Unmanaged, UpdateInstaller.Detect(build, data, isWindows: false, isPortable: false));
    }

    [Fact]
    public void A_windows_install_is_told_apart_from_a_portable_or_unpacked_copy()
    {
        var installed = Directory.CreateDirectory(Path.Combine(_dir, "installed")).FullName;
        File.WriteAllText(Path.Combine(installed, "unins000.exe"), "");
        var unpacked = Directory.CreateDirectory(Path.Combine(_dir, "unpacked")).FullName;

        Assert.Equal(InstallKind.WindowsInstall, UpdateInstaller.Detect(installed, "", isWindows: true, isPortable: false));
        Assert.Equal(InstallKind.WindowsPortable, UpdateInstaller.Detect(unpacked, "", isWindows: true, isPortable: true));
        Assert.Equal(InstallKind.Unmanaged, UpdateInstaller.Detect(unpacked, "", isWindows: true, isPortable: false));

        File.WriteAllText(Path.Combine(unpacked, "EternalLauncher.exe"), "");
        Assert.Equal(InstallKind.Unmanaged, UpdateInstaller.Detect(unpacked, "", isWindows: true, isPortable: false));
        Directory.CreateDirectory(Path.Combine(unpacked, "engine"));
        File.WriteAllText(Path.Combine(unpacked, "engine", "run.py"), "");
        Assert.Equal(InstallKind.WindowsLoose, UpdateInstaller.Detect(unpacked, "", isWindows: true, isPortable: false));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer, List<string> requested) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requested.Add(request.RequestUri!.AbsoluteUri);
            var response = answer(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}

public sealed class UpdateInstallTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("eternal-install-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static void Run(string file, params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private string Script(string name, string body)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        Run("chmod", "+x", path);
        return path;
    }

    private static async Task<string> ResultOf(PreparedUpdate update)
    {
        var file = Path.Combine(update.WorkDir, "result.txt");
        for (var i = 0; i < 100; i++)
        {
            if (File.Exists(file) && (await File.ReadAllTextAsync(file)).Trim() is { Length: > 0 } text and not "running")
                return text;
            await Task.Delay(100);
        }
        return "no result";
    }

    [Fact]
    public void A_linux_package_is_unpacked_and_its_install_script_found()
    {
        if (OperatingSystem.IsWindows())
            return;
        var package = Directory.CreateDirectory(Path.Combine(_dir, "package", "EternalLauncher")).FullName;
        File.WriteAllText(Path.Combine(package, "install.sh"), "#!/bin/sh\n");
        var work = Directory.CreateDirectory(Path.Combine(_dir, "updates", "2.2.0")).FullName;
        var tarball = Path.Combine(work, "EternalLauncher-2.2.0-linux-x64.tar.gz");
        Run("tar", "-czf", tarball, "-C", Path.Combine(_dir, "package"), "EternalLauncher");

        var update = UpdateInstaller.Prepare(InstallKind.LinuxInstall, new Version(2, 2, 0), tarball);

        Assert.Equal("install.sh", Path.GetFileName(update.Payload));
        Assert.True(File.Exists(update.Payload));
        Assert.Equal(work, update.WorkDir);
    }

    [Fact]
    public void Something_that_is_no_launcher_package_is_refused()
    {
        if (OperatingSystem.IsWindows())
            return;
        Directory.CreateDirectory(Path.Combine(_dir, "package", "junk"));
        var work = Directory.CreateDirectory(Path.Combine(_dir, "updates", "2.2.0")).FullName;
        var tarball = Path.Combine(work, "x.tar.gz");
        Run("tar", "-czf", tarball, "-C", Path.Combine(_dir, "package"), "junk");

        var ex = Assert.Throws<UpdateException>(() => UpdateInstaller.Prepare(InstallKind.LinuxInstall, new Version(2, 2, 0), tarball));

        Assert.Contains("doesn't look like", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_update_waits_for_the_launcher_installs_and_opens_it_again()
    {
        if (OperatingSystem.IsWindows())
            return;
        var work = Directory.CreateDirectory(Path.Combine(_dir, "updates", "2.2.0")).FullName;
        var installed = Path.Combine(_dir, "installed");
        var reopened = Path.Combine(_dir, "reopened");
        var installer = Script("install.sh", $"echo done > '{installed}'");
        var reopen = Script("launcher", $"echo opened > '{reopened}'");
        using var launcher = System.Diagnostics.Process.Start("sleep", "1");
        var update = new PreparedUpdate(InstallKind.LinuxInstall, new Version(2, 2, 0), installer, work);

        UpdateInstaller.Launch(update, launcher.Id, reopen, _dir);
        Assert.False(File.Exists(installed));
        Assert.DoesNotContain('\r', await File.ReadAllTextAsync(Path.Combine(work, "apply.sh")));

        Assert.Equal("ok", await ResultOf(update));
        Assert.True(launcher.HasExited);
        Assert.True(File.Exists(installed));
        for (var i = 0; i < 50 && !File.Exists(reopened); i++)
            await Task.Delay(100);
        Assert.True(File.Exists(reopened));
        Assert.Equal((new Version(2, 2, 0), (string?)null), UpdateInstaller.TakeResult(Path.Combine(_dir, "updates")));
    }

    [Fact]
    public async Task A_loose_linux_copy_is_updated_by_copying_the_new_files_over_it()
    {
        if (OperatingSystem.IsWindows())
            return;
        var work = Directory.CreateDirectory(Path.Combine(_dir, "updates", "2.2.0")).FullName;
        var payload = Directory.CreateDirectory(Path.Combine(_dir, "newapp")).FullName;
        File.WriteAllText(Path.Combine(payload, "EternalLauncher"), "new");
        File.WriteAllText(Path.Combine(payload, "engine.txt"), "new");
        var target = Directory.CreateDirectory(Path.Combine(_dir, "app")).FullName;
        File.WriteAllText(Path.Combine(target, "old.txt"), "old");
        var reopened = Path.Combine(_dir, "reopened");
        var reopen = Script("launcher", $"echo opened > '{reopened}'");
        using var launcher = System.Diagnostics.Process.Start("sleep", "1");
        var update = new PreparedUpdate(InstallKind.LinuxLoose, new Version(2, 2, 0), payload, work);

        UpdateInstaller.Launch(update, launcher.Id, reopen, target);

        Assert.Equal("ok", await ResultOf(update));
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(target, "EternalLauncher")));
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(target, "engine.txt")));
        Assert.True(File.Exists(Path.Combine(target, "old.txt")));

        for (var i = 0; i < 50 && !File.Exists(reopened); i++)
            await Task.Delay(100);
        Assert.True(File.Exists(reopened));
    }

    [Fact]
    public async Task A_copy_that_fails_halfway_puts_the_old_files_back_and_opens_the_old_version()
    {
        if (OperatingSystem.IsWindows())
            return;
        var work = Directory.CreateDirectory(Path.Combine(_dir, "updates", "2.2.0")).FullName;
        var payload = Directory.CreateDirectory(Path.Combine(_dir, "newapp")).FullName;
        File.WriteAllText(Path.Combine(payload, "EternalLauncher"), "new");
        File.WriteAllText(Path.Combine(payload, "engine"), "a file where the old copy has a folder");
        var target = Directory.CreateDirectory(Path.Combine(_dir, "app")).FullName;
        File.WriteAllText(Path.Combine(target, "EternalLauncher"), "old");
        Directory.CreateDirectory(Path.Combine(target, "engine"));
        var reopened = Path.Combine(_dir, "reopened");
        var reopen = Script("launcher", $"echo opened > '{reopened}'");
        using var launcher = System.Diagnostics.Process.Start("sleep", "1");
        var update = new PreparedUpdate(InstallKind.LinuxLoose, new Version(2, 2, 0), payload, work);

        UpdateInstaller.Launch(update, launcher.Id, reopen, target);

        Assert.StartsWith("failed", await ResultOf(update));
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(target, "EternalLauncher")));
        for (var i = 0; i < 50 && !File.Exists(reopened); i++)
            await Task.Delay(100);
        Assert.True(File.Exists(reopened));
    }

    [Fact]
    public void A_finished_download_is_picked_up_again_on_the_next_start()
    {
        if (OperatingSystem.IsWindows())
            return;
        var updates = Path.Combine(_dir, "updates");
        var work = Directory.CreateDirectory(Path.Combine(updates, "2.2.0")).FullName;
        var tarball = PackageIn(work);
        var update = UpdateInstaller.Prepare(InstallKind.LinuxInstall, new Version(2, 2, 0), tarball);
        UpdateInstaller.Remember(update, tarball);

        var resumed = UpdateInstaller.Resume(updates, InstallKind.LinuxInstall, new Version(2, 1, 0));

        Assert.NotNull(resumed);
        Assert.Equal(new Version(2, 2, 0), resumed.Version);
        Assert.True(File.Exists(resumed.Payload));
    }

    [Fact]
    public void A_download_that_changed_on_disk_is_not_picked_up()
    {
        if (OperatingSystem.IsWindows())
            return;
        var updates = Path.Combine(_dir, "updates");
        var work = Directory.CreateDirectory(Path.Combine(updates, "2.2.0")).FullName;
        var tarball = PackageIn(work);
        var update = UpdateInstaller.Prepare(InstallKind.LinuxInstall, new Version(2, 2, 0), tarball);
        UpdateInstaller.Remember(update, tarball);
        File.AppendAllText(tarball, "tampered");

        Assert.Null(UpdateInstaller.Resume(updates, InstallKind.LinuxInstall, new Version(2, 1, 0)));
    }

    [Fact]
    public void A_download_for_this_version_or_older_is_not_picked_up()
    {
        if (OperatingSystem.IsWindows())
            return;
        var updates = Path.Combine(_dir, "updates");
        var work = Directory.CreateDirectory(Path.Combine(updates, "2.2.0")).FullName;
        var tarball = PackageIn(work);
        UpdateInstaller.Remember(UpdateInstaller.Prepare(InstallKind.LinuxInstall, new Version(2, 2, 0), tarball), tarball);

        Assert.Null(UpdateInstaller.Resume(updates, InstallKind.LinuxInstall, new Version(2, 2, 0)));
    }

    private string PackageIn(string work)
    {
        var package = Directory.CreateDirectory(Path.Combine(_dir, "package", "EternalLauncher")).FullName;
        File.WriteAllText(Path.Combine(package, "install.sh"), "#!/bin/sh\n");
        var tarball = Path.Combine(work, "EternalLauncher-2.2.0-linux-x64.tar.gz");
        Run("tar", "-czf", tarball, "-C", Path.Combine(_dir, "package"), "EternalLauncher");
        return tarball;
    }

    [Fact]
    public async Task A_failed_install_is_reported_on_the_next_start()
    {
        if (OperatingSystem.IsWindows())
            return;
        var work = Directory.CreateDirectory(Path.Combine(_dir, "updates", "2.2.0")).FullName;
        var update = new PreparedUpdate(InstallKind.LinuxInstall, new Version(2, 2, 0), Script("install.sh", "exit 3"), work);

        UpdateInstaller.Launch(update, 999_999, null, _dir);

        Assert.Equal("failed:3", await ResultOf(update));
        var (version, problem) = UpdateInstaller.TakeResult(Path.Combine(_dir, "updates"))!.Value;
        Assert.Equal(new Version(2, 2, 0), version);
        Assert.Contains("3", problem);
    }

    [Fact]
    public async Task A_failed_install_still_opens_the_launcher_it_left_in_place()
    {
        if (OperatingSystem.IsWindows())
            return;
        var work = Directory.CreateDirectory(Path.Combine(_dir, "updates", "2.2.0")).FullName;
        var reopened = Path.Combine(_dir, "reopened");
        var installer = Script("install.sh", "exit 3");
        var reopen = Script("launcher", $"echo opened > '{reopened}'");
        var update = new PreparedUpdate(InstallKind.LinuxInstall, new Version(2, 2, 0), installer, work);

        UpdateInstaller.Launch(update, 999_999, reopen, _dir);

        Assert.Equal("failed:3", await ResultOf(update));
        for (var i = 0; i < 50 && !File.Exists(reopened); i++)
            await Task.Delay(100);
        Assert.True(File.Exists(reopened));
    }
}
