using System.Globalization;

namespace Midnight.Demon.Features.Servers.Files;

/// <summary>Unix permission bits as "ls -l" string ("-rw-r--r--") and octal ("644")</summary>
public static class FileModes
{
    public const UnixFileMode DefaultFile = (UnixFileMode)0b110_100_100;      // 644
    public const UnixFileMode DefaultDirectory = (UnixFileMode)0b111_101_101; // 755

    /// <summary>The mode of an entry, links are not followed. Defaults (644 / 755) on Windows.</summary>
    public static UnixFileMode Get(FileSystemInfo info)
    {
        if (OperatingSystem.IsWindows())
            return info is DirectoryInfo ? DefaultDirectory : DefaultFile;

        try
        {
            return info.UnixFileMode;
        }
        catch (IOException)
        {
            return info is DirectoryInfo ? DefaultDirectory : DefaultFile;
        }
    }

    /// <summary>"drwxr-xr-x", "-rw-r--r--", "lrwxrwxrwx", with s/S and t/T for the special bits</summary>
    public static string Format(UnixFileMode mode, char type)
    {
        Span<char> chars = stackalloc char[10];
        chars[0] = type;

        Triplet(chars[1..], mode.HasFlag(UnixFileMode.UserRead), mode.HasFlag(UnixFileMode.UserWrite),
            mode.HasFlag(UnixFileMode.UserExecute), mode.HasFlag(UnixFileMode.SetUser), 's');
        Triplet(chars[4..], mode.HasFlag(UnixFileMode.GroupRead), mode.HasFlag(UnixFileMode.GroupWrite),
            mode.HasFlag(UnixFileMode.GroupExecute), mode.HasFlag(UnixFileMode.SetGroup), 's');
        Triplet(chars[7..], mode.HasFlag(UnixFileMode.OtherRead), mode.HasFlag(UnixFileMode.OtherWrite),
            mode.HasFlag(UnixFileMode.OtherExecute), mode.HasFlag(UnixFileMode.StickyBit), 't');

        return new string(chars);
    }

    /// <summary>"644", or 4 digits ("4755") when a special bit is set</summary>
    public static string ToOctal(UnixFileMode mode)
    {
        var value = (int)mode & 0xFFF;
        return value > 0x1FF ? Convert.ToString(value, 8).PadLeft(4, '0') : Convert.ToString(value, 8).PadLeft(3, '0');
    }

    /// <summary>Parses "644" / "0755" / "4755", false for anything else</summary>
    public static bool TryParse(string? value, out UnixFileMode mode)
    {
        mode = 0;

        if (string.IsNullOrEmpty(value) || value.Length is < 3 or > 4 || value.Any(c => c is < '0' or > '7'))
            return false;

        var parsed = 0;

        foreach (var c in value)
            parsed = parsed * 8 + (c - '0');

        mode = (UnixFileMode)parsed;
        return true;
    }

    /// <summary>Type character of "ls -l": d = directory, l = link, - = file</summary>
    public static char TypeOf(FileSystemInfo info) =>
        info.LinkTarget != null ? 'l' : info is DirectoryInfo ? 'd' : '-';

    private static void Triplet(Span<char> chars, bool read, bool write, bool execute, bool special, char specialChar)
    {
        chars[0] = read ? 'r' : '-';
        chars[1] = write ? 'w' : '-';
        chars[2] = special
            ? execute ? specialChar : char.ToUpper(specialChar, CultureInfo.InvariantCulture)
            : execute ? 'x' : '-';
    }
}
