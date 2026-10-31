using System.Text;

namespace Midnight.Demon.Features.Servers.Output;

/// <summary>
/// Turns a byte stream into lines. Splits on '\n' (a trailing '\r' is removed), so multi byte UTF-8 characters are never cut.
/// Very long lines without a line break are emitted in chunks of <see cref="MaxLineBytes"/>.
/// Not thread safe, use one instance per stream.
/// </summary>
internal sealed class LineSplitter
{
    public const int MaxLineBytes = 16 * 1024;

    private byte[] _buffer = new byte[1024];
    private int _length;

    /// <summary>Adds the complete lines contained in <paramref name="data"/> to <paramref name="lines"/></summary>
    public void Append(ReadOnlySpan<byte> data, List<string> lines)
    {
        while (!data.IsEmpty)
        {
            var index = data.IndexOf((byte)'\n');

            if (index < 0)
            {
                Buffer(data, lines);
                return;
            }

            Buffer(data[..index], lines);
            Emit(lines);
            data = data[(index + 1)..];
        }
    }

    /// <summary>Emits the incomplete last line, if any (end of stream)</summary>
    public void Flush(List<string> lines)
    {
        if (_length > 0)
            Emit(lines);
    }

    private void Buffer(ReadOnlySpan<byte> data, List<string> lines)
    {
        while (!data.IsEmpty)
        {
            var space = MaxLineBytes - _length;

            if (space == 0)
            {
                Emit(lines);
                continue;
            }

            var take = Math.Min(space, data.Length);

            if (_length + take > _buffer.Length)
                Array.Resize(ref _buffer, Math.Min(MaxLineBytes, Math.Max(_buffer.Length * 2, _length + take)));

            data[..take].CopyTo(_buffer.AsSpan(_length));
            _length += take;
            data = data[take..];
        }
    }

    private void Emit(List<string> lines)
    {
        var length = _length;

        if (length > 0 && _buffer[length - 1] == (byte)'\r')
            length--;

        lines.Add(Encoding.UTF8.GetString(_buffer, 0, length));
        _length = 0;
    }
}
