namespace Eternal.Core.Servers;

public static class ServerChecks
{
    /// <summary>
    /// Claims players its own player list doesn't have. TF2 lists bots and people
    /// still connecting too, so a real server's two counts stay close.
    /// </summary>
    public static bool LooksFake(int claimed, IReadOnlyList<PlayerEntry>? listed)
    {
        if (listed is null || claimed < 4)
            return false;
        return claimed - listed.Count >= 3 && listed.Count < claimed * 0.75;
    }

    /// <summary>Valve's own servers sit behind Steam's relay and can't be joined by address.</summary>
    public static bool IsValve(string name, IReadOnlyList<string> tags) =>
        tags.Contains("valve", StringComparer.OrdinalIgnoreCase)
        || name.StartsWith("Valve Matchmaking Server", StringComparison.OrdinalIgnoreCase);
}
