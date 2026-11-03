namespace Eternal.Core.Servers;

public static class ServerChecks
{
    public static bool LooksFake(int claimed, IReadOnlyList<PlayerEntry>? listed)
    {
        if (listed is null || claimed < 4)
            return false;
        return claimed - listed.Count >= 3 && listed.Count < claimed * 0.75;
    }

    public static bool IsValve(string name, IReadOnlyList<string> tags) =>
        tags.Contains("valve", StringComparer.OrdinalIgnoreCase)
        || name.StartsWith("Valve Matchmaking Server", StringComparison.OrdinalIgnoreCase);
}
