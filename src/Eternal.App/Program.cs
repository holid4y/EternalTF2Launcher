using Avalonia;
using Eternal.Core;
using Eternal.Core.Logging;
using Eternal.Core.Platform;

namespace Eternal.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var paths = AppPaths.Detect();
        try
        {
            paths.EnsureCreated();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Eternal Launcher can't create its folders: {ex.Message}");
        }

        var settings = LauncherSettingsStore.Peek(paths);
        Log.Initialize(paths.LogsDir, Enum.TryParse<LogLevel>(settings.LogLevel, true, out var level) ? level : LogLevel.Info, settings.ClearLogsOnStart);
        Log.Info($"Eternal Launcher {typeof(Program).Assembly.GetName().Version?.ToString(3)} on {Environment.OSVersion} ({(paths.IsPortable ? "portable" : "installed")})");

        using var instance = SingleInstance.TryClaim();
        if (instance is null)
        {
            Log.Info("Another instance is running; asked it to come to the front.");
            return 0;
        }

        // Held only so the Windows installer can tell the launcher is running.
        using var installerMutex = OperatingSystem.IsWindows() ? new Mutex(false, "ProjectEternalLauncher_Mutex") : null;

        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashReport.Write(paths, e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // Avalonia probes optional desktop services over DBus (input methods,
            // the global menu) and leaves the failures unobserved.
            if (e.Exception.InnerExceptions.All(x => x.GetType().FullName?.StartsWith("Tmds.DBus", StringComparison.Ordinal) == true))
                Log.Debug($"Desktop service unavailable: {e.Exception.InnerException?.Message}");
            else
                Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        try
        {
            App.Paths = paths;
            App.Instance = instance;
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            CrashReport.Write(paths, ex);
            return 1;
        }
        finally
        {
            Log.Shutdown();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions { WmClass = "eternal-launcher" })
            .LogToTrace();
}

internal static class CrashReport
{
    public static void Write(AppPaths paths, Exception? ex)
    {
        Log.Error("Fatal error", ex);
        try
        {
            File.WriteAllText(Path.Combine(paths.LogsDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt"), ex?.ToString() ?? "unknown error");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(ex);
        }
    }
}
