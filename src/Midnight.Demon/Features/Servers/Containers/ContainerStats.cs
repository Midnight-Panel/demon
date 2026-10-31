using System.Text.Json;
using System.Text.Json.Serialization;

namespace Midnight.Demon.Features.Servers.Containers;

/// <summary>
/// One sample of the docker stats stream (GET /containers/{id}/stats?stream=true, one json document per line).
/// Parsed with System.Text.Json because Docker.DotNet's IProgress overload leaves an unobserved read task behind when it is cancelled.
/// </summary>
internal sealed class ContainerStatsSample
{
    [JsonPropertyName("cpu_stats")]
    public CpuStats? CpuStats { get; set; }

    [JsonPropertyName("precpu_stats")]
    public CpuStats? PreCpuStats { get; set; }

    [JsonPropertyName("memory_stats")]
    public MemoryStats? MemoryStats { get; set; }

    [JsonPropertyName("networks")]
    public Dictionary<string, NetworkStats>? Networks { get; set; }

    public static ContainerStatsSample? Parse(string json) => JsonSerializer.Deserialize<ContainerStatsSample>(json);
}

internal sealed class CpuStats
{
    [JsonPropertyName("cpu_usage")]
    public CpuUsage? CpuUsage { get; set; }

    [JsonPropertyName("system_cpu_usage")]
    public ulong SystemCpuUsage { get; set; }

    [JsonPropertyName("online_cpus")]
    public uint OnlineCpus { get; set; }
}

internal sealed class CpuUsage
{
    [JsonPropertyName("total_usage")]
    public ulong TotalUsage { get; set; }

    [JsonPropertyName("percpu_usage")]
    public List<ulong>? PerCpuUsage { get; set; }
}

internal sealed class MemoryStats
{
    [JsonPropertyName("usage")]
    public ulong Usage { get; set; }

    [JsonPropertyName("limit")]
    public ulong Limit { get; set; }

    [JsonPropertyName("stats")]
    public Dictionary<string, ulong>? Stats { get; set; }
}

internal sealed class NetworkStats
{
    [JsonPropertyName("rx_bytes")]
    public ulong RxBytes { get; set; }

    [JsonPropertyName("tx_bytes")]
    public ulong TxBytes { get; set; }
}

/// <summary>Live usage of a running container</summary>
internal sealed record ContainerUsage(double CpuPercent, long MemoryBytes, long NetworkRxBytes, long NetworkTxBytes)
{
    public static readonly ContainerUsage Empty = new(0, 0, 0, 0);

    public static ContainerUsage From(ContainerStatsSample sample)
    {
        var (rx, tx) = CalculateNetwork(sample);
        return new ContainerUsage(Math.Round(CalculateCpuPercent(sample), 2), CalculateMemoryBytes(sample.MemoryStats), rx, tx);
    }

    /// <summary>
    /// Cpu usage between the sample and its previous sample in percent of ONE core (200 = two full cores),
    /// the same formula as "docker stats"
    /// </summary>
    public static double CalculateCpuPercent(ContainerStatsSample sample)
    {
        var current = sample.CpuStats;
        var previous = sample.PreCpuStats;

        // the first sample of a stream has no previous values
        if (current?.CpuUsage == null || previous?.CpuUsage == null || previous.SystemCpuUsage == 0)
            return 0;

        var cpuDelta = (double)current.CpuUsage.TotalUsage - previous.CpuUsage.TotalUsage;
        var systemDelta = (double)current.SystemCpuUsage - previous.SystemCpuUsage;

        if (cpuDelta <= 0 || systemDelta <= 0)
            return 0;

        var cpus = current.OnlineCpus > 0 ? (int)current.OnlineCpus : current.CpuUsage.PerCpuUsage?.Count ?? 1;

        return cpuDelta / systemDelta * Math.Max(1, cpus) * 100.0;
    }

    /// <summary>Usage without the page cache (cgroup v2 inactive_file, v1 total_inactive_file), like "docker stats"</summary>
    public static long CalculateMemoryBytes(MemoryStats? memory)
    {
        if (memory == null)
            return 0;

        ulong cache = 0;

        if (memory.Stats != null && !memory.Stats.TryGetValue("inactive_file", out cache))
            memory.Stats.TryGetValue("total_inactive_file", out cache);

        var used = cache < memory.Usage ? memory.Usage - cache : memory.Usage;
        return (long)Math.Min(used, long.MaxValue);
    }

    public static (long Rx, long Tx) CalculateNetwork(ContainerStatsSample sample)
    {
        if (sample.Networks == null)
            return (0, 0);

        ulong rx = 0, tx = 0;

        foreach (var network in sample.Networks.Values)
        {
            rx += network.RxBytes;
            tx += network.TxBytes;
        }

        return ((long)Math.Min(rx, long.MaxValue), (long)Math.Min(tx, long.MaxValue));
    }
}
