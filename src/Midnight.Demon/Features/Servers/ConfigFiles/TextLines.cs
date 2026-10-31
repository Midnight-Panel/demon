namespace Midnight.Demon.Features.Servers.ConfigFiles;

/// <summary>Splits a text file into lines and joins it again with its original line ending</summary>
internal sealed class TextLines
{
    public List<string> Lines { get; }
    public string NewLine { get; }

    private TextLines(List<string> lines, string newLine)
    {
        Lines = lines;
        NewLine = newLine;
    }

    public static TextLines Parse(string content)
    {
        var newLine = content.Contains("\r\n") ? "\r\n" : "\n";
        var lines = content.Replace("\r\n", "\n").Split('\n').ToList();

        // "a\nb\n" has no line after the last line break
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        return new TextLines(lines, newLine);
    }

    /// <summary>Joined lines, always with a trailing line break (empty file stays empty)</summary>
    public override string ToString() =>
        Lines.Count == 0 ? "" : string.Join(NewLine, Lines) + NewLine;

    public static int Indentation(string line)
    {
        var count = 0;

        while (count < line.Length && (line[count] == ' ' || line[count] == '\t'))
            count++;

        return count;
    }
}
