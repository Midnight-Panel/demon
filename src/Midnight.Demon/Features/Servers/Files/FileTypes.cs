using System.Text.Unicode;
using Microsoft.AspNetCore.StaticFiles;

namespace Midnight.Demon.Features.Servers.Files;

/// <summary>Mime types and whether a file can be opened in the editor (text and at most <see cref="MaxEditableBytes"/>)</summary>
public static class FileTypes
{
    public const long MaxEditableBytes = 5 * 1024 * 1024;
    public const string DirectoryMime = "inode/directory";
    public const string TextMime = "text/plain";
    public const string BinaryMime = "application/octet-stream";

    /// <summary>Bytes looked at to tell text from binary when the extension is not known</summary>
    public const int SniffBytes = 8192;

    private static readonly FileExtensionContentTypeProvider Provider = CreateProvider();

    /// <summary>Config and script formats game servers use that the default mapping doesn't know (or maps to binary)</summary>
    private static readonly Dictionary<string, string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".properties"] = TextMime, [".yml"] = "application/yaml", [".yaml"] = "application/yaml",
        [".toml"] = "application/toml", [".cfg"] = TextMime, [".conf"] = TextMime, [".ini"] = TextMime,
        [".log"] = TextMime, [".sh"] = "application/x-sh", [".bat"] = TextMime, [".cmd"] = TextMime, [".ps1"] = TextMime,
        [".env"] = TextMime, [".lua"] = TextMime, [".py"] = TextMime, [".rb"] = TextMime, [".php"] = TextMime,
        [".mcmeta"] = "application/json", [".json5"] = TextMime, [".jsonc"] = TextMime, [".lang"] = TextMime,
        [".sk"] = TextMime, [".md"] = "text/markdown", [".csv"] = "text/csv", [".tsv"] = TextMime, [".sql"] = TextMime,
        [".cs"] = TextMime, [".java"] = TextMime, [".kt"] = TextMime, [".ts"] = TextMime, [".go"] = TextMime,
        [".rs"] = TextMime, [".vdf"] = TextMime, [".acf"] = TextMime, [".secret"] = TextMime, [".lock"] = TextMime,
        [".gitignore"] = TextMime, [".dockerignore"] = TextMime, [".editorconfig"] = TextMime, [".service"] = TextMime
    };

    /// <summary>Mime type by the file name, null when the extension is not known</summary>
    public static string? GetMimeByName(string name)
    {
        var extension = Path.GetExtension(name);

        if (string.IsNullOrEmpty(extension))
        {
            // dotfiles like ".env" have no extension for .NET but their name says what they are
            extension = name.StartsWith('.') ? name : "";
        }

        if (extension.Length > 0 && TextExtensions.TryGetValue(extension, out var mime))
            return mime;

        return Provider.TryGetContentType(name, out var contentType) ? contentType : null;
    }

    /// <summary>Text mime types by name (text/*, json, xml, yaml, javascript, ...)</summary>
    public static bool IsTextMime(string mime) =>
        mime.StartsWith("text/", StringComparison.Ordinal) ||
        mime is "application/json" or "application/xml" or "application/yaml" or "application/toml" or "application/x-sh"
            or "application/javascript" or "application/x-javascript" or "image/svg+xml" or "application/manifest+json";

    /// <summary>Text = no NUL byte and valid UTF-8 (a multi-byte character cut off at the end of the sample is fine)</summary>
    public static bool LooksLikeText(ReadOnlySpan<byte> sample, bool complete)
    {
        if (sample.Contains((byte)0))
            return false;

        if (Utf8.IsValid(sample))
            return true;

        if (complete)
            return false;

        // the sample may end in the middle of a character: drop up to 3 trailing bytes of an incomplete sequence
        for (var cut = 1; cut <= 3 && cut < sample.Length; cut++)
        {
            if (Utf8.IsValid(sample[..^cut]))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Mime type and editability of a file: known text extensions are trusted, unknown ones are sniffed (first
    /// <see cref="SniffBytes"/> bytes). Known binary types are never editable.
    /// </summary>
    public static (string Mime, bool Editable) Detect(string path, long size)
    {
        var mime = GetMimeByName(Path.GetFileName(path));

        if (mime != null && IsTextMime(mime))
            return (mime, size <= MaxEditableBytes);

        if (mime != null)
            return (mime, false);

        if (size == 0)
            return (TextMime, true);

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            Span<byte> buffer = stackalloc byte[SniffBytes];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var text = LooksLikeText(buffer[..read], complete: read < buffer.Length);

            return text ? (TextMime, size <= MaxEditableBytes) : (BinaryMime, false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (BinaryMime, false);
        }
    }

    private static FileExtensionContentTypeProvider CreateProvider()
    {
        var provider = new FileExtensionContentTypeProvider();
        provider.Mappings[".jar"] = "application/java-archive";
        provider.Mappings[".gz"] = "application/gzip";
        provider.Mappings[".tgz"] = "application/gzip";
        provider.Mappings[".mca"] = BinaryMime;
        provider.Mappings[".dat"] = BinaryMime;
        provider.Mappings[".db"] = BinaryMime;
        provider.Mappings[".so"] = BinaryMime;
        return provider;
    }
}
