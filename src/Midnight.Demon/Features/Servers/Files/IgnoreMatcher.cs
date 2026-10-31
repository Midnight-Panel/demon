using System.Text;
using System.Text.RegularExpressions;

namespace Midnight.Demon.Features.Servers.Files;

/// <summary>
/// A subset of the gitignore syntax, used for the ignored files of backups and the file denylist of shards:
/// <list type="bullet">
/// <item>empty lines and lines starting with "#" are skipped, "\#" and "\!" escape the first character</item>
/// <item>"*" matches anything except "/", "?" one character except "/", "[a-z]" / "[!a-z]" character classes</item>
/// <item>"**" matches any number of directories ("**/logs", "logs/**", "a/**/b")</item>
/// <item>a pattern with a "/" at the start or in the middle is relative to the root, otherwise it matches at any level</item>
/// <item>a trailing "/" only matches directories</item>
/// <item>"!" re-includes what an earlier pattern excluded (the last matching pattern wins), but like in git nothing
/// inside an excluded directory can be re-included</item>
/// </list>
/// Paths are relative to the root with "/" separators, a leading "/" is ignored. Matching is case sensitive.
/// </summary>
public sealed class IgnoreMatcher
{
    public static readonly IgnoreMatcher Empty = new([]);

    private readonly List<Rule> _rules;

    private IgnoreMatcher(List<Rule> rules) => _rules = rules;

    public bool IsEmpty => _rules.Count == 0;

    public static IgnoreMatcher Parse(string? text) =>
        Parse((text ?? "").Split('\n'));

    public static IgnoreMatcher Parse(IEnumerable<string> lines)
    {
        var rules = new List<Rule>();

        foreach (var raw in lines)
        {
            var rule = ParseLine(raw);

            if (rule != null)
                rules.Add(rule);
        }

        return rules.Count == 0 ? Empty : new IgnoreMatcher(rules);
    }

    /// <summary>
    /// Whether the path itself matches, without looking at its parent directories. Used while walking a directory tree
    /// top-down where excluded directories are not entered at all.
    /// </summary>
    public bool Matches(string relativePath, bool isDirectory)
    {
        var path = Normalize(relativePath);

        if (path.Length == 0)
            return false;

        var ignored = false;

        foreach (var rule in _rules)
        {
            // only a later negation can change an exclusion and the other way around
            if (rule.Negate != ignored)
                continue;

            if (rule.DirectoryOnly && !isDirectory)
                continue;

            if (rule.Regex.IsMatch(path))
                ignored = !rule.Negate;
        }

        return ignored;
    }

    /// <summary>Whether the path or one of its parent directories is excluded (for single paths, e.g. the denylist)</summary>
    public bool IsExcluded(string relativePath, bool isDirectory)
    {
        if (IsEmpty)
            return false;

        var path = Normalize(relativePath);

        if (path.Length == 0)
            return false;

        var separator = path.IndexOf('/');

        while (separator > 0)
        {
            if (Matches(path[..separator], isDirectory: true))
                return true;

            separator = path.IndexOf('/', separator + 1);
        }

        return Matches(path, isDirectory);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    private static Rule? ParseLine(string raw)
    {
        var line = raw.TrimEnd('\r');

        // trailing spaces are ignored unless escaped with a backslash
        var end = line.Length;

        while (end > 0 && line[end - 1] == ' ' && (end < 2 || line[end - 2] != '\\'))
            end--;

        line = line[..end];

        if (line.Length == 0 || line[0] == '#')
            return null;

        var negate = false;

        if (line[0] == '!')
        {
            negate = true;
            line = line[1..];
        }
        else if (line.StartsWith("\\!", StringComparison.Ordinal) || line.StartsWith("\\#", StringComparison.Ordinal))
        {
            line = line[1..];
        }

        var directoryOnly = false;

        if (line.EndsWith('/'))
        {
            directoryOnly = true;
            line = line.TrimEnd('/');
        }

        // "/" inside the pattern (also a leading one) anchors it to the root
        var anchored = line.Contains('/');
        line = line.TrimStart('/');

        if (line.Length == 0)
            return null;

        var pattern = new StringBuilder("^");

        if (!anchored)
            pattern.Append("(?:.*/)?");

        AppendGlob(pattern, line);
        pattern.Append('$');

        return new Rule(new Regex(pattern.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), negate, directoryOnly);
    }

    private static void AppendGlob(StringBuilder pattern, string glob)
    {
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];

            switch (c)
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                {
                    var atStart = i == 0 || glob[i - 1] == '/';
                    var atEnd = i + 2 == glob.Length;
                    var beforeSlash = i + 2 < glob.Length && glob[i + 2] == '/';

                    if (atStart && beforeSlash)
                    {
                        // "**/" = zero or more directories
                        pattern.Append("(?:.*/)?");
                        i += 2;
                    }
                    else if (atStart && atEnd)
                    {
                        // "a/**" = everything inside a (but not a itself)
                        pattern.Append(".+");
                        i += 1;
                    }
                    else
                    {
                        // "**" elsewhere behaves like "*"
                        pattern.Append("[^/]*");
                        i += 1;
                    }

                    break;
                }
                case '*':
                    pattern.Append("[^/]*");
                    break;
                case '?':
                    pattern.Append("[^/]");
                    break;
                case '[':
                {
                    var close = glob.IndexOf(']', i + 2);

                    if (close < 0)
                    {
                        pattern.Append("\\[");
                        break;
                    }

                    var content = glob[(i + 1)..close];
                    var negated = content.StartsWith('!') || content.StartsWith('^');

                    if (negated)
                        content = content[1..];

                    pattern.Append(negated ? "[^/" : "[");

                    foreach (var member in content)
                        pattern.Append(member is '\\' or ']' or '[' or '^' ? "\\" + member : member.ToString());

                    pattern.Append(']');
                    i = close;
                    break;
                }
                case '\\' when i + 1 < glob.Length:
                    pattern.Append(Regex.Escape(glob[++i].ToString()));
                    break;
                default:
                    pattern.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
    }

    private sealed record Rule(Regex Regex, bool Negate, bool DirectoryOnly);
}
