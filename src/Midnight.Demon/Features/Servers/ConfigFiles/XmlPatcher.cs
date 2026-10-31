using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Midnight.Demon.Features.Servers.ConfigFiles;

/// <summary>
/// Best effort xml patcher. Keys are dot paths of element names ("Settings.Port"), the root element may be part of the
/// path or left out. A last segment "@name" sets an attribute. Missing elements are created.
/// </summary>
internal static class XmlPatcher
{
    public static string Patch(string content, IReadOnlyDictionary<string, string> mappings)
    {
        var document = string.IsNullOrWhiteSpace(content)
            ? new XDocument(new XDeclaration("1.0", "utf-8", null))
            : Load(content);

        foreach (var (key, value) in mappings)
        {
            var segments = ConfigKeyPath.Split(key).Where(x => x.Length > 0).ToList();

            if (segments.Count == 0 || segments.Any(x => !IsValidName(x.TrimStart('@'))))
                continue;

            if (document.Root == null)
                document.Add(new XElement(segments[0]));

            var element = document.Root!;

            if (segments.Count == 1 && segments[0] == element.Name.LocalName)
            {
                element.Value = value;
                continue;
            }

            var start = segments[0] == element.Name.LocalName && segments.Count > 1 ? 1 : 0;

            for (var i = start; i < segments.Count; i++)
            {
                var segment = segments[i];

                if (i == segments.Count - 1 && segment.StartsWith('@'))
                {
                    element.SetAttributeValue(segment[1..], value);
                    break;
                }

                var child = element.Elements().FirstOrDefault(x => x.Name.LocalName == segment);

                if (child == null)
                {
                    child = new XElement(element.Name.Namespace + segment);
                    element.Add(child);
                }

                element = child;

                if (i == segments.Count - 1)
                    element.Value = value;
            }
        }

        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            OmitXmlDeclaration = document.Declaration == null,
            Indent = false
        };

        using var stream = new MemoryStream();

        using (var writer = XmlWriter.Create(stream, settings))
            document.Save(writer);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Parses without DTDs: the file belongs to the user, entity expansion could blow up the demon</summary>
    private static XDocument Load(string content)
    {
        using var reader = XmlReader.Create(new StringReader(content), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static bool IsValidName(string name)
    {
        try
        {
            XmlConvert.VerifyName(name);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }
}
