using System.IO;
using System.Threading;
using System.Text.Json;
using System.Windows;
using LauncherTF2.Core;
using LauncherTF2.Models;

namespace LauncherTF2;

public partial class App : Application
{
    private const string UiMutexName = "ProjectEternalLauncher_Mutex";
    private Mutex? _mutex;

    public App()
    {
        // Single-instance guard. The launcher runs entirely unelevated and as a
        // single UI instance — there is no longer an elevated helper child.
        _mutex = new Mutex(false, UiMutexName);
        if (!_mutex.WaitOne(0, false))
        {
            Views.MessageDialog.ShowError(
                "Already Running",
                "Eternal TF2 Launcher is already running.\n\nClose the existing instance before opening another.");

            _mutex.Dispose();
            _mutex = null;
            Current.Shutdown();
            return;
        }

        var launcherConfig = TryLoadLauncherConfig();
        TryAutoClearLogs(launcherConfig);

        Logger.Initialize(ResolveStartupLogLevel(launcherConfig));

        ServiceLocator.Initialize();

        DispatcherUnhandledException += App_DispatcherUnhandledException;
        Exit += App_Exit;

        Logger.LogInfo("[App] Startup complete");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Restore the user's saved theme before the window paints — this avoids
        // a one-frame flash of the default Eternal palette on first render.
        ServiceLocator.Theme.LoadFromConfig();

        // StartupUri was removed from App.xaml so we create the window manually
        // after services are initialized.
        var mainWindow = new Views.MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.LogError("[App] Unhandled exception caught", e.Exception);

        string errorMsg = $"An unhandled exception occurred: {e.Exception.Message}\n\nStack Trace:\n{e.Exception.StackTrace}";
        Views.MessageDialog.ShowError("Error", errorMsg);

        try
        {
            var crashLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash_log.txt");
            File.WriteAllText(crashLogPath, $"{DateTime.Now}: {errorMsg}");
            Logger.LogInfo($"[App] Crash log written to: {crashLogPath}");
        }
        catch (Exception ex)
        {
            Logger.LogError("[App] Failed to write crash log", ex);
        }

        e.Handled = !IsFatalException(e.Exception);
    }

    private static bool IsFatalException(Exception ex)
    {
        return ex is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or System.Runtime.InteropServices.SEHException;
    }

    private void App_Exit(object sender, ExitEventArgs e)
    {
        Logger.LogInfo("[App] Shutting down — releasing resources");
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
    }

    private static LauncherConfig? TryLoadLauncherConfig()
    {
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher_config.json");
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private static void TryAutoClearLogs(LauncherConfig? cfg)
    {
        if (cfg?.AutoClearLogs != true) return;
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var logFile = Path.Combine(baseDir, "app_debug.log");
            var archiveDir = Path.Combine(baseDir, "logs");
            if (File.Exists(logFile)) File.Delete(logFile);
            if (Directory.Exists(archiveDir)) Directory.Delete(archiveDir, recursive: true);
        }
        catch
        {
            // Best-effort: logger not up yet
        }
    }

    private static LogLevel ResolveStartupLogLevel(LauncherConfig? cfg)
    {
        if (cfg == null) return LogLevel.Info;
        if (cfg.EnableDebugLog) return LogLevel.Debug;
        return Enum.TryParse<LogLevel>(cfg.LogLevel, ignoreCase: true, out var lvl) ? lvl : LogLevel.Info;
    }
}
