using Midnight.Demon.Features.Servers.ConfigFiles;
using Midnight.Shared.Http.Shards;

namespace Midnight.Demon.Tests;

public class ConfigFilePatcherTests
{
    private static Dictionary<string, string> Map(params (string Key, string Value)[] values) =>
        values.ToDictionary(x => x.Key, x => x.Value);

    [Fact]
    public void Properties_ReplacesValues_KeepsComments_AppendsMissing()
    {
        const string content = "#Minecraft server properties\nserver-port=25565\nmotd = Hello\nonline-mode: true\n";

        var patched = PropertiesPatcher.Patch(content, Map(("server-port", "25570"), ("motd", "Midnight"), ("online-mode", "false"), ("max-players", "20")));

        Assert.Equal("#Minecraft server properties\nserver-port=25570\nmotd = Midnight\nonline-mode: false\nmax-players=20\n", patched);
    }

    [Fact]
    public void Properties_KeepsWindowsLineEndings()
    {
        var patched = PropertiesPatcher.Patch("a=1\r\nb=2\r\n", Map(("b", "3")));

        Assert.Equal("a=1\r\nb=3\r\n", patched);
    }

    [Fact]
    public void Json_SetsNestedValues_WithTypes()
    {
        const string content = """{ "server": { "port": 1, "name": "old" }, "list": [ { "port": 1 }, { "port": 2 } ] }""";

        var patched = JsonPatcher.Patch(content, Map(("server.port", "25565"), ("server.name", "123"), ("list.*.port", "7"), ("new.flag", "true")));
        var json = System.Text.Json.Nodes.JsonNode.Parse(patched)!;

        Assert.Equal(25565, json["server"]!["port"]!.GetValue<long>());
        Assert.Equal("123", json["server"]!["name"]!.GetValue<string>());
        Assert.Equal(7, json["list"]![1]!["port"]!.GetValue<long>());
        Assert.True(json["new"]!["flag"]!.GetValue<bool>());
    }

    [Fact]
    public void Json_EmptyFile_IsCreated()
    {
        var patched = JsonPatcher.Patch("", Map(("port", "007")));

        Assert.Equal("007", System.Text.Json.Nodes.JsonNode.Parse(patched)!["port"]!.GetValue<string>());
    }

    [Fact]
    public void Yaml_ReplacesNestedValue_KeepsCommentsAndQuotes()
    {
        const string content = "# config\nserver:\n  port: 25565 # the port\n  motd: \"hello\"\nother: x\n";

        var patched = YamlPatcher.Patch(content, Map(("server.port", "25570"), ("server.motd", "Midnight")));

        Assert.Equal("# config\nserver:\n  port: 25570 # the port\n  motd: \"Midnight\"\nother: x\n", patched);
    }

    [Fact]
    public void Yaml_InsertsMissingKeysBelowTheirParent()
    {
        const string content = "server:\n    port: 1\nother: x\n";

        var patched = YamlPatcher.Patch(content, Map(("server.host", "0.0.0.0"), ("new.deep.key", "a: b")));

        Assert.Equal("server:\n    port: 1\n    host: 0.0.0.0\nother: x\nnew:\n  deep:\n    key: 'a: b'\n", patched);
    }

    [Fact]
    public void Yaml_DoesNotReplaceANestedMapping()
    {
        const string content = "server:\n  port: 1\n";

        Assert.Equal(content, YamlPatcher.Patch(content, Map(("server", "x"))));
    }

    [Fact]
    public void Ini_PatchesSectionsAndGlobalKeys()
    {
        const string content = "global=1\n\n[Server]\n; comment\nPort = 7777\n\n[Other]\nPort=1\n";

        var patched = IniPatcher.Patch(content, Map(("global", "2"), ("Server.Port", "7778"), ("Server.Name", "Midnight"), ("New.Key", "v")));

        Assert.Equal("global=2\n\n[Server]\n; comment\nPort = 7778\nName=Midnight\n\n[Other]\nPort=1\n\n[New]\nKey=v\n", patched);
    }

    [Fact]
    public void Xml_SetsElementsAndAttributes()
    {
        const string content = "<?xml version=\"1.0\" encoding=\"utf-8\"?><Config><Port>1</Port></Config>";

        var patched = XmlPatcher.Patch(content, Map(("Config.Port", "25565"), ("Name", "Midnight"), ("Network.@ip", "0.0.0.0")));
        var document = System.Xml.Linq.XDocument.Parse(patched);

        Assert.Equal("25565", document.Root!.Element("Port")!.Value);
        Assert.Equal("Midnight", document.Root.Element("Name")!.Value);
        Assert.Equal("0.0.0.0", document.Root.Element("Network")!.Attribute("ip")!.Value);
    }

    [Fact]
    public void Xml_RejectsDtds()
    {
        const string content = "<?xml version=\"1.0\"?><!DOCTYPE lol [<!ENTITY lol \"lol\">]><a>&lol;</a>";

        Assert.Throws<System.Xml.XmlException>(() => XmlPatcher.Patch(content, Map(("a", "b"))));
    }

    [Fact]
    public void File_ReplacesLinesStartingWithTheKey()
    {
        var patched = FileLinePatcher.Patch("  port 1\nname x\n", Map(("port", "port 2"), ("missing", "y")));

        Assert.Equal("port 2\nname x\n", patched);
    }

    [Fact]
    public void Patch_UnknownParser_IsNull()
    {
        Assert.Null(ConfigFilePatcher.Patch("toml", "", Map(("a", "b"))));
    }

    [Fact]
    public void Apply_WritesFilesInsideTheVolume_AndRefusesTraversal()
    {
        var volume = Directory.CreateTempSubdirectory("midnight-volume-").FullName;

        try
        {
            File.WriteAllText(Path.Combine(volume, "server.properties"), "server-port=1\n");

            var problems = new List<string>();
            var written = ConfigFilePatcher.Apply(volume,
            [
                new ShardConfigFileDto { Path = "/server.properties", Parser = "properties", Mappings = Map(("server-port", "2")) },
                new ShardConfigFileDto { Path = "config/settings.json", Parser = "json", Mappings = Map(("a", "1")) },
                new ShardConfigFileDto { Path = "../escape.properties", Parser = "properties", Mappings = Map(("a", "1")) },
                new ShardConfigFileDto { Path = "x.toml", Parser = "toml", Mappings = Map(("a", "1")) },
                new ShardConfigFileDto { Path = "missing.txt", Parser = "file", Mappings = Map(("a", "1")) }
            ], problems);

            Assert.Equal(2, written.Count);
            Assert.Equal("server-port=2\n", File.ReadAllText(Path.Combine(volume, "server.properties")));
            Assert.True(File.Exists(Path.Combine(volume, "config", "settings.json")));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(volume)!, "escape.properties")));
            Assert.False(File.Exists(Path.Combine(volume, "missing.txt")));
            Assert.Equal(2, problems.Count);
            Assert.Contains(problems, x => x.Contains("leaves the server directory"));
            Assert.Contains(problems, x => x.Contains("unknown parser"));
        }
        finally
        {
            Directory.Delete(volume, recursive: true);
        }
    }

    [Fact]
    public void WriteEula_AcceptsTheEula_KeepsOtherLines()
    {
        var volume = Directory.CreateTempSubdirectory("midnight-volume-").FullName;

        try
        {
            File.WriteAllText(Path.Combine(volume, "eula.txt"), "#By changing the setting below...\neula=false\n");

            ConfigFilePatcher.WriteEula(volume);

            Assert.Equal("#By changing the setting below...\neula=true\n", File.ReadAllText(Path.Combine(volume, "eula.txt")));
        }
        finally
        {
            Directory.Delete(volume, recursive: true);
        }
    }

    [Fact]
    public void KeyPath_SupportsEscapedDots()
    {
        Assert.Equal(["a.b", "c"], ConfigKeyPath.Split(@"a\.b.c"));
    }
}
