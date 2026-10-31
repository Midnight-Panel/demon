namespace Midnight.Daemon.Features.Host;

/// <summary>Samples the system wide cpu usage from /proc/stat every two seconds (Linux only)</summary>
public class CpuUsageSampler : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    public double Usage { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!File.Exists("/proc/stat"))
            return;

        var previous = Read();

        using var timer = new PeriodicTimer(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var current = Read();

            if (previous != null && current != null)
                Usage = Calculate(previous.Value, current.Value);

            previous = current;
        }
    }

    /// <summary>Usage in percent between two /proc/stat samples</summary>
    internal static double Calculate((long Idle, long Total) previous, (long Idle, long Total) current)
    {
        var total = current.Total - previous.Total;
        var idle = current.Idle - previous.Idle;

        return total <= 0 ? 0 : Math.Clamp(100.0 * (total - idle) / total, 0, 100);
    }

    /// <summary>Parses the first "cpu" line of /proc/stat. Idle includes iowait.</summary>
    internal static (long Idle, long Total)? Parse(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 5 || parts[0] != "cpu")
            return null;

        var values = parts.Skip(1).Select(x => long.TryParse(x, out var v) ? v : 0).ToArray();
        var idle = values[3] + (values.Length > 4 ? values[4] : 0);

        return (idle, values.Sum());
    }

    private static (long Idle, long Total)? Read()
    {
        try
        {
            using var reader = new StreamReader("/proc/stat");
            return Parse(reader.ReadLine() ?? "");
        }
        catch (IOException)
        {
            return null;
        }
    }
}
