using System.Text;
using System.Text.RegularExpressions;

namespace Eternal.Core.Settings;

/// <summary>Minimal reader for Source engine cfg syntax.</summary>
public static partial class CfgText
{
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex Identifier();

    /// <summary>
    /// Splits cfg text into commands (by line and by ';' outside quotes),
    /// dropping // comments, and tokenizes each one.
    /// </summary>
    public static IEnumerable<IReadOnlyList<string>> Commands(string text)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            var inQuotes = false;
            var hadQuotes = false;

            void Flush()
            {
                if (current.Length > 0 || hadQuotes)
                    tokens.Add(current.ToString());
                current.Clear();
                hadQuotes = false;
            }

            var line = rawLine.TrimEnd('\r');
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    hadQuotes = true;
                    continue;
                }
                if (!inQuotes && c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                    break;
                if (!inQuotes && c == ';')
                {
                    Flush();
                    if (tokens.Count > 0)
                        yield return tokens;
                    tokens = [];
                    continue;
                }
                if (!inQuotes && char.IsWhiteSpace(c))
                {
                    Flush();
                    continue;
                }
                current.Append(c);
            }

            Flush();
            if (tokens.Count > 0)
                yield return tokens;
        }
    }

    /// <summary>"name value" assignments, last one wins, keyed case-insensitively.</summary>
    public static Dictionary<string, string> Assignments(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in Commands(text))
        {
            if (command.Count >= 2 && Identifier().IsMatch(command[0]) && !IsCommandWord(command[0]))
                result[command[0]] = command[1];
        }
        return result;
    }

    private static bool IsCommandWord(string word) =>
        word.Equals("bind", StringComparison.OrdinalIgnoreCase)
        || word.Equals("alias", StringComparison.OrdinalIgnoreCase)
        || word.Equals("exec", StringComparison.OrdinalIgnoreCase)
        || word.Equals("echo", StringComparison.OrdinalIgnoreCase)
        || word.Equals("unbind", StringComparison.OrdinalIgnoreCase);

    /// <summary>Key → command for every bind in the text.</summary>
    public static Dictionary<string, string> Binds(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in Commands(text))
        {
            if (command.Count >= 3 && command[0].Equals("bind", StringComparison.OrdinalIgnoreCase))
                result[command[1]] = command[2];
        }
        return result;
    }
}
