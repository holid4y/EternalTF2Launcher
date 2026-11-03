using System.Text;

namespace Eternal.Core.Steam;

/// <summary>Valve KeyValues (text VDF) node: string values or nested sections.</summary>
public sealed class VdfNode
{
    private readonly Dictionary<string, object> _children = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = [];

    public IEnumerable<KeyValuePair<string, object>> Children => _order.Select(k => new KeyValuePair<string, object>(k, _children[k]));

    public VdfNode? Section(string key) => _children.GetValueOrDefault(key) as VdfNode;
    public string? Value(string key) => _children.GetValueOrDefault(key) as string;

    public VdfNode? Path(params string[] keys)
    {
        var node = this;
        foreach (var key in keys)
        {
            node = node.Section(key);
            if (node is null)
                return null;
        }
        return node;
    }

    public void Set(string key, object value)
    {
        if (value is not (string or VdfNode))
            throw new ArgumentException("A VDF value is a string or a section.", nameof(value));
        if (!_children.TryGetValue(key, out _))
            _order.Add(key);
        else
            key = _order.First(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        _children[key] = value;
    }

    public VdfNode Ensure(string key)
    {
        if (Section(key) is { } existing)
            return existing;
        var created = new VdfNode();
        Set(key, created);
        return created;
    }

    public bool Remove(string key)
    {
        if (!_children.Remove(key))
            return false;
        _order.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        return true;
    }
}

public static class Vdf
{
    public static VdfNode Parse(string text)
    {
        var root = new VdfNode();
        var stack = new Stack<VdfNode>();
        stack.Push(root);
        string? pendingKey = null;

        foreach (var (token, quoted) in Tokens(text))
        {
            if (!quoted && token == "{")
            {
                var child = new VdfNode();
                stack.Peek().Set(pendingKey ?? "", child);
                stack.Push(child);
                pendingKey = null;
            }
            else if (!quoted && token == "}")
            {
                if (stack.Count > 1)
                    stack.Pop();
                pendingKey = null;
            }
            else if (pendingKey is null)
            {
                pendingKey = token;
            }
            else
            {
                stack.Peek().Set(pendingKey, token);
                pendingKey = null;
            }
        }
        return root;
    }

    /// <summary>Writes the tree back the way Steam formats its own files: tabs, quoted keys and values.</summary>
    public static string Write(VdfNode root)
    {
        var sb = new StringBuilder();
        Write(sb, root, 0);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, VdfNode node, int depth)
    {
        foreach (var (key, value) in node.Children)
        {
            sb.Append('\t', depth).Append('"').Append(Escape(key)).Append('"');
            if (value is VdfNode child)
            {
                sb.Append('\n').Append('\t', depth).Append("{\n");
                Write(sb, child, depth + 1);
                sb.Append('\t', depth).Append("}\n");
            }
            else
            {
                sb.Append("\t\t\"").Append(Escape((string)value)).Append("\"\n");
            }
        }
    }

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);

    private static IEnumerable<(string Token, bool Quoted)> Tokens(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                    i++;
            }
            else if (c is '{' or '}')
            {
                yield return (c.ToString(), false);
                i++;
            }
            else if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        i++;
                        sb.Append(text[i] switch { 'n' => '\n', 't' => '\t', _ => text[i] });
                    }
                    else
                    {
                        sb.Append(text[i]);
                    }
                    i++;
                }
                i++;
                yield return (sb.ToString(), true);
            }
            else
            {
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"'))
                    i++;
                yield return (text[start..i], false);
            }
        }
    }
}
