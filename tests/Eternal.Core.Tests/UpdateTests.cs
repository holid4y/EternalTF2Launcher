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
        Assert.Equal("EternalLauncher-2.2.0-win-x64-setup.exe", release.AssetFor(InstallKind.WindowsInstall)?.Name);
        Assert.Null(release.AssetFor(InstallKind.WindowsPortable));
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
        for (var i = 0; i < 100 && !File.Exists(file); i++)
            await Task.Delay(100);
        return File.Exists(file) ? (await File.ReadAllTextAsync(file)).Trim() : "no result";
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

        UpdateInstaller.Launch(update, launcher.Id, reopen);
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
    public async Task A_failed_install_is_reported_on_the_next_start()
    {
        if (OperatingSystem.IsWindows())
            return;
        var work = Directory.CreateDirectory(Path.Combine(_dir, "updates", "2.2.0")).FullName;
        var update = new PreparedUpdate(InstallKind.LinuxInstall, new Version(2, 2, 0), Script("install.sh", "exit 3"), work);

        UpdateInstaller.Launch(update, 999_999, null);

        Assert.Equal("failed:3", await ResultOf(update));
        var (version, problem) = UpdateInstaller.TakeResult(Path.Combine(_dir, "updates"))!.Value;
        Assert.Equal(new Version(2, 2, 0), version);
        Assert.Contains("3", problem);
    }
}
