using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Midnight.Demon.Features.Servers.ConfigFiles;

/// <summary>
/// Json files. Keys are dot paths ("server.port"), numbers address array items ("servers.0.port") and "*" every item
/// or property ("servers.*.port"). Missing objects are created. Values keep the type of an existing string, otherwise
/// true/false, numbers and null are written as json literals.
/// </summary>
internal static class JsonPatcher
{
    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Patch(string content, IReadOnlyDictionary<string, string> mappings)
    {
        var root = string.IsNullOrWhiteSpace(content) ? new JsonObject() : JsonNode.Parse(content, documentOptions: ReadOptions) ?? new JsonObject();

        foreach (var (key, value) in mappings)
            Set(root, ConfigKeyPath.Split(key), 0, value);

        return root.ToJsonString(WriteOptions) + "\n";
    }

    private static void Set(JsonNode node, List<string> segments, int index, string value)
    {
        var segment = segments[index];
        var last = index == segments.Count - 1;

        if (segment == "*")
        {
            switch (node)
            {
                case JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                        SetChild(array, i, array[i], segments, index, value, last);
                    break;
                case JsonObject obj:
                    foreach (var name in obj.Select(x => x.Key).ToList())
                        SetChild(obj, name, obj[name], segments, index, value, last);
                    break;
            }

            return;
        }

        switch (node)
        {
            case JsonArray array when int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < array.Count:
                SetChild(array, i, array[i], segments, index, value, last);
                break;
            case JsonObject obj:
                SetChild(obj, segment, obj[segment], segments, index, value, last);
                break;
        }
    }

    private static void SetChild(JsonNode parent, object key, JsonNode? current, List<string> segments, int index, string value, bool last)
    {
        if (last)
        {
            Assign(parent, key, Convert(value, current));
            return;
        }

        if (current is not (JsonObject or JsonArray))
        {
            current = new JsonObject();
            Assign(parent, key, current);
        }

        Set(current, segments, index + 1, value);
    }

    private static void Assign(JsonNode parent, object key, JsonNode? value)
    {
        if (parent is JsonArray array)
            array[(int)key] = value;
        else
            parent[(string)key] = value;
    }

    internal static JsonNode? Convert(string value, JsonNode? existing)
    {
        if (existing is JsonValue current && current.GetValueKind() == JsonValueKind.String)
            return JsonValue.Create(value);

        switch (value)
        {
            case "true":
                return JsonValue.Create(true);
            case "false":
                return JsonValue.Create(false);
            case "null":
                return null;
        }

        // "007" or "+1" stay strings, they are most likely not meant as numbers
        var plainNumber = value.Length > 0 && (char.IsDigit(value[0]) || value[0] == '-') &&
                          !(value.Length > 1 && value[0] == '0' && char.IsDigit(value[1]));

        if (plainNumber && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
            return JsonValue.Create(number);

        if (plainNumber && value.Contains('.') &&
            double.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var floating))
            return JsonValue.Create(floating);

        return JsonValue.Create(value);
    }
}
