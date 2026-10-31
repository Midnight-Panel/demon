using System.Text;

namespace Midnight.Demon.Features.Servers.ConfigFiles;

internal static class ConfigKeyPath
{
    /// <summary>Splits "a.b.c" into its segments, "\." is a literal dot inside a segment</summary>
    public static List<string> Split(string key)
    {
        var segments = new List<string>();
        var current = new StringBuilder();

        for (var i = 0; i < key.Length; i++)
        {
            if (key[i] == '\\' && i + 1 < key.Length && key[i + 1] == '.')
            {
                current.Append('.');
                i++;
            }
            else if (key[i] == '.')
            {
                segments.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(key[i]);
            }
        }

        segments.Add(current.ToString());
        return segments;
    }
}
