namespace Midnight.Demon.Features.Servers.ConfigFiles;

/// <summary>
/// Java style .properties (key=value or key: value). Existing keys keep their position and comments stay untouched,
/// missing keys are appended.
/// </summary>
internal static class PropertiesPatcher
{
    public static string Patch(string content, IReadOnlyDictionary<string, string> mappings)
    {
        var text = TextLines.Parse(content);
        var found = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < text.Lines.Count; i++)
        {
            var line = text.Lines[i];
            var trimmed = line.TrimStart();

            if (trimmed.Length == 0 || trimmed[0] is '#' or '!')
                continue;

            var separator = line.IndexOfAny(['=', ':']);
            var key = (separator < 0 ? line : line[..separator]).Trim();

            if (!mappings.TryGetValue(key, out var value))
                continue;

            found.Add(key);

            if (separator < 0)
            {
                text.Lines[i] = $"{key}={value}";
                continue;
            }

            // keep "key = " exactly like it was
            var valueStart = separator + 1;

            while (valueStart < line.Length && line[valueStart] is ' ' or '\t')
                valueStart++;

            text.Lines[i] = line[..valueStart] + value;
        }

        foreach (var (key, value) in mappings)
        {
            if (!found.Contains(key))
                text.Lines.Add($"{key}={value}");
        }

        return text.ToString();
    }
}
