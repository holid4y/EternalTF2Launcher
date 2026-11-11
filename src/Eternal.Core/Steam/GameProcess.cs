using System.Diagnostics;
using Eternal.Core.Platform;

namespace Eternal.Core.Steam;

public static class GameProcess
{
    public static bool IsRunning(Games.GameDefinition game)
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var name in game.WindowsProcesses)
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

        return Proc.AnyNamed([.. game.LinuxProcesses]);
    }
}
