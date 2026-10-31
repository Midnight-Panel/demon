using System.Text.RegularExpressions;

namespace Midnight.Demon.Features.Servers.Output;

/// <summary>Decides when a starting server counts as online (Process.StartupDone)</summary>
internal static partial class StartupDetector
{
    /// <summary>True when there is at least one non empty pattern, otherwise the server is online right after the start</summary>
    public static bool HasPatterns(IReadOnlyList<string>? patterns) =>
        patterns != null && patterns.Any(pattern => !string.IsNullOrEmpty(pattern));

    /// <summary>True when the line (without ANSI escape codes) contains one of the patterns, case sensitive</summary>
    public static bool IsDone(string line, IReadOnlyList<string>? patterns)
    {
        if (!HasPatterns(patterns))
            return false;

        var plain = StripAnsi(line);

        return patterns!.Any(pattern => !string.IsNullOrEmpty(pattern) && plain.Contains(pattern, StringComparison.Ordinal));
    }

    public static string StripAnsi(string line) =>
        line.Contains('\u001b') ? AnsiPattern().Replace(line, "") : line;

    // CSI sequences (colors, cursor), OSC sequences (window title) and two character escapes
    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])")]
    private static partial Regex AnsiPattern();
}
