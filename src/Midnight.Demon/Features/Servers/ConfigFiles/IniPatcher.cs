namespace Midnight.Demon.Features.Servers.ConfigFiles;

/// <summary>
/// Ini files. A key "section.name" addresses name in [section], keys without a section live before the first section.
/// When a key contains a dot but no such section exists and the full key exists globally, the global key is used.
/// Missing keys are added to their section, missing sections are appended. Comments (; and #) are kept.
/// </summary>
internal static class IniPatcher
{
    public static string Patch(string content, IReadOnlyDictionary<string, string> mappings)
    {
        var text = TextLines.Parse(content);

        foreach (var (key, value) in mappings)
        {
            var (section, name) = Resolve(text.Lines, key);
            Set(text.Lines, section, name, value);
        }

        return text.ToString();
    }

    private static (string? Section, string Name) Resolve(List<string> lines, string key)
    {
        var dot = key.IndexOf('.');

        if (dot <= 0 || dot == key.Length - 1)
            return (null, key);

        var section = key[..dot];
        var name = key[(dot + 1)..];

        if (FindSection(lines, section) >= 0)
            return (section, name);

        if (FindKey(lines, 0, SectionEnd(lines, -1), key) >= 0)
            return (null, key);

        return (section, name);
    }

    private static void Set(List<string> lines, string? section, string name, string value)
    {
        var header = section == null ? -1 : FindSection(lines, section);

        if (section != null && header < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0)
                lines.Add("");

            lines.Add($"[{section}]");
            lines.Add($"{name}={value}");
            return;
        }

        var start = header + 1;
        var end = SectionEnd(lines, header);
        var index = FindKey(lines, start, end, name);

        if (index >= 0)
        {
            var line = lines[index];
            var valueStart = line.IndexOf('=') + 1;

            while (valueStart < line.Length && line[valueStart] is ' ' or '\t')
                valueStart++;

            lines[index] = line[..valueStart] + value;
            return;
        }

        // insert after the last non empty line of the section
        var insertAt = end;

        while (insertAt > start && lines[insertAt - 1].Trim().Length == 0)
            insertAt--;

        lines.Insert(insertAt, $"{name}={value}");
    }

    private static int FindSection(List<string> lines, string section)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();

            if (trimmed.Length > 2 && trimmed[0] == '[' && trimmed[^1] == ']' &&
                string.Equals(trimmed[1..^1].Trim(), section, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    /// <summary>Index of the next section header after <paramref name="header"/> (-1 = global part), or the line count</summary>
    private static int SectionEnd(List<string> lines, int header)
    {
        for (var i = header + 1; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith('['))
                return i;
        }

        return lines.Count;
    }

    private static int FindKey(List<string> lines, int start, int end, string name)
    {
        for (var i = start; i < end; i++)
        {
            var trimmed = lines[i].Trim();

            if (trimmed.Length == 0 || trimmed[0] is ';' or '#')
                continue;

            var separator = trimmed.IndexOf('=');

            if (separator > 0 && string.Equals(trimmed[..separator].Trim(), name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }
}
