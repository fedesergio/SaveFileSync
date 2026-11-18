using System.Text;
using System.Text.RegularExpressions;

namespace SaveFileSync.Core.IO;

/// <summary>
/// Minimal glob matcher for relative paths using '/' separators, case-insensitive.
/// Supports <c>**</c> (any number of segments), <c>*</c> (within a segment) and <c>?</c>.
/// </summary>
public sealed class Glob
{
    private readonly Regex _regex;

    public Glob(string pattern)
    {
        Pattern = pattern;
        _regex = new Regex(ToRegex(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public string Pattern { get; }

    public bool IsMatch(string relativePath) => _regex.IsMatch(relativePath);

    private static string ToRegex(string pattern)
    {
        var p = pattern.Replace('\\', '/').Trim().TrimStart('/');
        var sb = new StringBuilder("^");
        for (var i = 0; i < p.Length; i++)
        {
            var c = p[i];
            if (c == '*')
            {
                var isDouble = i + 1 < p.Length && p[i + 1] == '*';
                if (isDouble)
                {
                    var followedBySlash = i + 2 < p.Length && p[i + 2] == '/';
                    sb.Append(followedBySlash ? "(?:.*/)?" : ".*");
                    i += followedBySlash ? 2 : 1;
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        sb.Append('$');
        return sb.ToString();
    }
}

/// <summary>Include/exclude filter for one location.</summary>
public sealed class PathFilter
{
    private readonly Glob[] _includes;
    private readonly Glob[] _excludes;

    public PathFilter(IEnumerable<string> includes, IEnumerable<string> excludes)
    {
        _includes = includes.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => new Glob(s)).ToArray();
        _excludes = excludes.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => new Glob(s)).ToArray();
    }

    public bool IsIncluded(string relativePath) =>
        (_includes.Length == 0 || _includes.Any(g => g.IsMatch(relativePath)))
        && !_excludes.Any(g => g.IsMatch(relativePath));
}
