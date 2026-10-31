using System.Text;
using Midnight.Shared.Http.Shards;

namespace Midnight.Demon.Features.Servers.ConfigFiles;

/// <summary>Patches the configuration files of a server (Process.ConfigFiles) before it starts</summary>
internal static class ConfigFilePatcher
{
    public static readonly IReadOnlyList<string> SupportedParsers = ["properties", "json", "yaml", "yml", "ini", "xml", "file"];

    /// <summary>Patches the content with the parser, null = unknown parser</summary>
    public static string? Patch(string parser, string content, IReadOnlyDictionary<string, string> mappings) =>
        parser.Trim().ToLowerInvariant() switch
        {
            "properties" => PropertiesPatcher.Patch(content, mappings),
            "json" => JsonPatcher.Patch(content, mappings),
            "yaml" or "yml" => YamlPatcher.Patch(content, mappings),
            "ini" => IniPatcher.Patch(content, mappings),
            "xml" => XmlPatcher.Patch(content, mappings),
            "file" => FileLinePatcher.Patch(content, mappings),
            _ => null
        };

    /// <summary>
    /// Applies every config file inside the volume. Problems with a single file are returned as messages and never
    /// stop the server from starting. Returns the files that were written.
    /// </summary>
    public static List<string> Apply(string volume, IEnumerable<ShardConfigFileDto> files, List<string> problems)
    {
        var written = new List<string>();

        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.Path) || file.Mappings.Count == 0)
                continue;

            try
            {
                var path = ServerFileSystem.ResolveInside(volume, file.Path);
                var exists = File.Exists(path);

                // "file" only replaces lines, there is nothing to do without a file
                if (!exists && file.Parser.Trim().Equals("file", StringComparison.OrdinalIgnoreCase))
                    continue;

                var content = exists ? File.ReadAllText(path) : "";
                var patched = Patch(file.Parser, content, file.Mappings);

                if (patched == null)
                {
                    problems.Add($"Config file {file.Path}: unknown parser '{file.Parser}'");
                    continue;
                }

                if (exists && patched == content)
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, patched, new UTF8Encoding(false));
                written.Add(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or System.Xml.XmlException)
            {
                problems.Add($"Config file {file.Path} could not be patched: {e.Message}");
            }
        }

        return written;
    }

    /// <summary>Accepts the Minecraft EULA (eula.txt with eula=true), other lines of the file are kept</summary>
    public static string WriteEula(string volume)
    {
        var path = ServerFileSystem.ResolveInside(volume, "eula.txt");
        var content = File.Exists(path) ? File.ReadAllText(path) : "";

        File.WriteAllText(path, PropertiesPatcher.Patch(content, new Dictionary<string, string> { ["eula"] = "true" }), new UTF8Encoding(false));
        return path;
    }
}
