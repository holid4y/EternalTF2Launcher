using System.Diagnostics;
using Eternal.Core.Platform;

namespace Eternal.Core.Steam;

public static class GameProcess
{
    private static readonly string[] WindowsNames = ["tf_win64", "tf"];
    private static readonly string[] LinuxNames = ["tf_linux64", "tf_win64.exe", "tf.exe"];

    public static bool IsRunning()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var name in WindowsNames)
            {
                var processes = Process.GetProcessesByName(name);
                var found = processes.Length > 0;
                foreach (var p in processes)
                    p.Dispose();
                if (found)
                    return true;
            }
            return false;
        }

        return Proc.AnyNamed(LinuxNames);
    }
}
