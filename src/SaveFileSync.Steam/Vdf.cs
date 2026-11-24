using System.Text;

namespace SaveFileSync.Steam;

/// <summary>A node of a Valve text KeyValues file: either a string value or a set of children.</summary>
public sealed class VdfNode
{
    public string? Value { get; init; }

    public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public VdfNode? this[string key] => Children.GetValueOrDefault(key);

    public string? String(string key) => this[key]?.Value;
}

/// <summary>
/// Minimal parser for Steam's text VDF files (<c>libraryfolders.vdf</c>, <c>appmanifest_*.acf</c>,
/// <c>remotecache.vdf</c>). Tolerant: anything unreadable yields <c>null</c> instead of an exception,
/// because these files belong to Steam and may be half-written.
/// </summary>
public static class Vdf
{
    public static VdfNode? TryParse(string text)
    {
        try
        {
            var position = 0;
            var root = new VdfNode();
            ReadMembers(text, ref position, root, topLevel: true);
            return root;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static VdfNode? TryParseFile(string path)
    {
        try
        {
            return File.Exists(path) ? TryParse(File.ReadAllText(path)) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void ReadMembers(string text, ref int p, VdfNode into, bool topLevel)
    {
        while (true)
        {
            SkipSpace(text, ref p);
            if (p >= text.Length)
            {
                if (topLevel)
                {
                    return;
                }

                throw new FormatException("Unexpected end");
            }

            if (text[p] == '}')
            {
                if (topLevel)
                {
                    throw new FormatException("Unexpected }");
                }

                p++;
                return;
            }

            var key = ReadString(text, ref p);
            SkipSpace(text, ref p);
            if (p >= text.Length)
            {
                throw new FormatException("Missing value");
            }

            if (text[p] == '{')
            {
                p++;
                var child = new VdfNode();
                ReadMembers(text, ref p, child, topLevel: false);
                into.Children[key] = child;
            }
            else
            {
                into.Children[key] = new VdfNode { Value = ReadString(text, ref p) };
            }
        }
    }

    private static void SkipSpace(string text, ref int p)
    {
        while (p < text.Length)
        {
            if (char.IsWhiteSpace(text[p]))
            {
                p++;
            }
            else if (text[p] == '/' && p + 1 < text.Length && text[p + 1] == '/')
            {
                while (p < text.Length && text[p] != '\n')
                {
                    p++;
                }
            }
            else
            {
                return;
            }
        }
    }

    private static string ReadString(string text, ref int p)
    {
        if (p >= text.Length || text[p] != '"')
        {
            throw new FormatException("Expected a quoted string");
        }

        p++;
        var sb = new StringBuilder();
        while (p < text.Length)
        {
            var c = text[p++];
            switch (c)
            {
                case '"':
                    return sb.ToString();
                case '\\' when p < text.Length:
                    var next = text[p++];
                    sb.Append(next switch { 'n' => '\n', 't' => '\t', _ => next });
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        throw new FormatException("Unterminated string");
    }
}
