using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace Midnight.Daemon.Core.Logging;

/// <summary>Keeps the last log lines in memory so the panel can show them (GET /api/system/logs)</summary>
public class LogBuffer : ILogEventSink
{
    public const int Capacity = 1000;

    private readonly Queue<string> _lines = new();
    private readonly Lock _lock = new();
    private readonly MessageTemplateTextFormatter _formatter =
        new("[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");

    public void Emit(LogEvent logEvent)
    {
        using var writer = new StringWriter();
        _formatter.Format(logEvent, writer);

        var text = writer.ToString().TrimEnd();

        lock (_lock)
        {
            foreach (var line in text.Split('\n'))
            {
                _lines.Enqueue(line.TrimEnd('\r'));

                if (_lines.Count > Capacity)
                    _lines.Dequeue();
            }
        }
    }

    public List<string> GetLines(int count)
    {
        lock (_lock)
            return _lines.Skip(Math.Max(0, _lines.Count - count)).ToList();
    }
}
