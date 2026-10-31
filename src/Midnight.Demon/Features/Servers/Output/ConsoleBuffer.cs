namespace Midnight.Demon.Features.Servers.Output;

/// <summary>Thread safe ring buffer with the last console lines of a server</summary>
internal sealed class ConsoleBuffer
{
    private readonly string[] _lines;
    private readonly Lock _lock = new();
    private int _start;
    private int _count;

    public ConsoleBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _lines = new string[capacity];
    }

    public int Capacity => _lines.Length;

    public void Add(string line)
    {
        lock (_lock)
        {
            if (_count < _lines.Length)
            {
                _lines[(_start + _count) % _lines.Length] = line;
                _count++;
            }
            else
            {
                _lines[_start] = line;
                _start = (_start + 1) % _lines.Length;
            }
        }
    }

    /// <summary>The last <paramref name="count"/> lines, oldest first</summary>
    public List<string> GetLines(int count)
    {
        lock (_lock)
        {
            var take = Math.Clamp(count, 0, _count);
            var result = new List<string>(take);

            for (var i = _count - take; i < _count; i++)
                result.Add(_lines[(_start + i) % _lines.Length]);

            return result;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_lines);
            _start = 0;
            _count = 0;
        }
    }
}
