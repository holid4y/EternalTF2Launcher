using Eternal.Core.Platform;

namespace Eternal.Core.Servers;

/// <summary>
/// Joining by address. A password travels as one launch argument or one
/// steam:// path segment, so it is limited to characters neither can misread.
/// </summary>
public static class ServerJoin
{
    private const string Symbols = "-._~!$()*,=:@";

    public const string PasswordRules = "The launcher can pass passwords made of letters, numbers and - . _ ~ ! $ ( ) * , = : @, up to 64 characters, not starting with -. For anything else, join from the game's own server browser.";

    public static bool IsSafePassword(string password) =>
        password.Length is > 0 and <= 64
        && password[0] != '-'
        && password.All(c => char.IsAsciiLetterOrDigit(c) || Symbols.Contains(c));

    public static IReadOnlyList<string> LaunchArguments(ServerAddress address, string? password)
    {
        if (password is not null && !IsSafePassword(password))
            throw new ArgumentException(PasswordRules, nameof(password));
        return password is null
            ? ["+connect", address.ToString()]
            : ["+password", password, "+connect", address.ToString()];
    }

    /// <summary>Asks Steam to point the running game at the server.</summary>
    public static bool ConnectRunningGame(ServerAddress address, string? password)
    {
        if (password is not null && !IsSafePassword(password))
            throw new ArgumentException(PasswordRules, nameof(password));
        var url = $"steam://connect/{address}";
        return Shell.OpenUrl(password is null ? url : $"{url}/{password}", logAs: url);
    }
}
