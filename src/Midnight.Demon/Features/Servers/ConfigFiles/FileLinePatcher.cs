namespace Midnight.Demon.Features.Servers.ConfigFiles;

/// <summary>
/// Parser "file": every line that starts with a mapping key (ignoring indentation) is replaced by the mapping value.
/// Nothing is appended, unknown keys are ignored.
/// </summary>
internal static class FileLinePatcher
{
    public static string Patch(string content, IReadOnlyDictionary<string, string> mappings)
    {
        var text = TextLines.Parse(content);

        for (var i = 0; i < text.Lines.Count; i++)
        {
            var trimmed = text.Lines[i].TrimStart();

            foreach (var (key, value) in mappings)
            {
                if (key.Length > 0 && trimmed.StartsWith(key, StringComparison.Ordinal))
                {
                    text.Lines[i] = value;
                    break;
                }
            }
        }

        return text.ToString();
    }
}
