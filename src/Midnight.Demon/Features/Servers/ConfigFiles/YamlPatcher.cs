using System.Globalization;

namespace Midnight.Demon.Features.Servers.ConfigFiles;

/// <summary>
/// Best effort yaml patcher that works on lines, so comments and formatting survive (no yaml library needed).
/// Supports block mappings addressed by dot paths ("settings.port"). Values of existing keys are replaced
/// (keeping their quote style and trailing comment), missing keys are inserted below their deepest existing parent.
/// Not supported: addressing list items, flow style ({a: b}) and keys whose value is a nested mapping.
/// </summary>
internal static class YamlPatcher
{
    private sealed record Entry(int Line, int Indent, List<string> Path, int ValueStart, bool HasValue);

    public static string Patch(string content, IReadOnlyDictionary<string, string> mappings)
    {
        var text = TextLines.Parse(content);

        foreach (var (key, value) in mappings)
            Set(text.Lines, ConfigKeyPath.Split(key), value);

        return text.ToString();
    }

    private static void Set(List<string> lines, List<string> path, string value)
    {
        var entries = Parse(lines);
        var entry = entries.FirstOrDefault(x => x.Path.SequenceEqual(path));

        if (entry != null)
        {
            // a key with a nested mapping can't be turned into a scalar without losing data
            if (!entry.HasValue && HasChildren(lines, entry))
                return;

            var line = lines[entry.Line];
            var (existing, comment) = SplitValue(line[entry.ValueStart..]);
            var prefix = line[..entry.ValueStart];

            if (!entry.HasValue && !prefix.EndsWith(' '))
                prefix += " ";

            lines[entry.Line] = prefix + Format(value, existing) + comment;
            return;
        }

        // the deepest existing parent mapping
        for (var depth = path.Count - 1; depth >= 1; depth--)
        {
            var prefixPath = path.Take(depth).ToList();
            var parent = entries.FirstOrDefault(x => x.Path.SequenceEqual(prefixPath));

            if (parent == null)
                continue;

            // the parent is a scalar, nesting below it would produce invalid yaml
            if (parent.HasValue)
                return;

            var childIndent = FirstChildIndent(lines, parent) ?? parent.Indent + 2;
            var step = Math.Max(2, childIndent - parent.Indent);
            Insert(lines, BlockEnd(lines, parent), path.Skip(depth).ToList(), childIndent, step, value);
            return;
        }

        Insert(lines, lines.Count, path, 0, 2, value);
    }

    private static void Insert(List<string> lines, int index, List<string> segments, int indent, int step, string value)
    {
        var added = new List<string>();

        for (var i = 0; i < segments.Count; i++)
        {
            var padding = new string(' ', indent + i * step);
            added.Add(i == segments.Count - 1 ? $"{padding}{segments[i]}: {Format(value, "")}" : $"{padding}{segments[i]}:");
        }

        lines.InsertRange(index, added);
    }

    private static List<Entry> Parse(List<string> lines)
    {
        var entries = new List<Entry>();
        var stack = new List<(int Indent, string Key)>();
        int? blockScalarIndent = null;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;

            var indent = TextLines.Indentation(line);

            if (blockScalarIndent != null)
            {
                if (indent > blockScalarIndent)
                    continue;

                blockScalarIndent = null;
            }

            if (trimmed is "---" or "...")
            {
                stack.Clear();
                continue;
            }

            while (stack.Count > 0 && stack[^1].Indent >= indent)
                stack.RemoveAt(stack.Count - 1);

            // list items are not addressable, keys below them get a "-" segment so they never match
            if (trimmed == "-" || trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                stack.Add((indent, "-"));
                continue;
            }

            if (!TryParseKey(line, indent, out var key, out var valueStart))
                continue;

            var rest = line[valueStart..].TrimStart();
            var hasValue = rest.Length > 0 && rest[0] != '#';

            stack.Add((indent, key));
            entries.Add(new Entry(i, indent, stack.Select(x => x.Key).ToList(), valueStart, hasValue));

            if (hasValue && rest[0] is '|' or '>')
                blockScalarIndent = indent;
        }

        return entries;
    }

    /// <summary>Parses "key: value" / "'key': value" starting at <paramref name="indent"/>, valueStart = first character after ": "</summary>
    private static bool TryParseKey(string line, int indent, out string key, out int valueStart)
    {
        key = "";
        valueStart = 0;

        if (line[indent] is '{' or '[' or '#')
            return false;

        int colon;

        if (line[indent] is '"' or '\'')
        {
            var quote = line[indent];
            var close = line.IndexOf(quote, indent + 1);

            if (close < 0)
                return false;

            key = line[(indent + 1)..close];
            colon = close + 1;

            while (colon < line.Length && line[colon] == ' ')
                colon++;

            if (colon >= line.Length || line[colon] != ':')
                return false;
        }
        else
        {
            colon = indent;

            while (true)
            {
                colon = line.IndexOf(':', colon);

                if (colon < 0)
                    return false;

                if (colon + 1 == line.Length || line[colon + 1] is ' ' or '\t')
                    break;

                colon++;
            }

            key = line[indent..colon].TrimEnd();

            if (key.Length == 0)
                return false;
        }

        valueStart = colon + 1;

        while (valueStart < line.Length && line[valueStart] is ' ' or '\t')
            valueStart++;

        return true;
    }

    private static bool HasChildren(List<string> lines, Entry entry) => FirstChildIndent(lines, entry) != null;

    private static int? FirstChildIndent(List<string> lines, Entry entry)
    {
        for (var i = entry.Line + 1; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();

            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;

            var indent = TextLines.Indentation(lines[i]);
            return indent > entry.Indent ? indent : null;
        }

        return null;
    }

    /// <summary>Index after the last line that belongs to the block of <paramref name="entry"/></summary>
    private static int BlockEnd(List<string> lines, Entry entry)
    {
        var end = entry.Line + 1;

        for (var i = entry.Line + 1; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();

            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;

            if (TextLines.Indentation(lines[i]) <= entry.Indent)
                break;

            end = i + 1;
        }

        return end;
    }

    /// <summary>Splits an existing value into the value and its trailing comment (" # ...")</summary>
    private static (string Value, string Comment) SplitValue(string rest)
    {
        if (rest.Length == 0 || rest[0] == '#')
            return ("", rest.Length == 0 ? "" : " " + rest);

        var searchFrom = 0;

        if (rest[0] is '"' or '\'')
        {
            var close = rest.IndexOf(rest[0], 1);
            searchFrom = close < 0 ? rest.Length : close + 1;
        }

        for (var i = searchFrom; i < rest.Length; i++)
        {
            if (rest[i] == '#' && i > 0 && rest[i - 1] is ' ' or '\t')
            {
                var valueEnd = i;

                while (valueEnd > 0 && rest[valueEnd - 1] is ' ' or '\t')
                    valueEnd--;

                return (rest[..valueEnd], rest[valueEnd..]);
            }
        }

        return (rest.TrimEnd(), "");
    }

    internal static string Format(string value, string existing)
    {
        if (existing.StartsWith('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";

        if (existing.StartsWith('\'') || NeedsQuotes(value))
            return "'" + value.Replace("'", "''") + "'";

        return value;
    }

    private static bool NeedsQuotes(string value)
    {
        if (value.Length == 0 || char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
            return true;

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return false;

        return "-?:,[]{}#&*!|>'\"%@`".Contains(value[0]) || value.Contains(": ") || value.Contains(" #") || value.EndsWith(':');
    }
}
