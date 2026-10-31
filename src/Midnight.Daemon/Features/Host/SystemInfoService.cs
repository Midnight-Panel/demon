using System.Runtime.InteropServices;
using Midnight.Daemon.Core.Configuration;
using Midnight.Daemon.Features.Docker;
using Midnight.Shared.Http.Daemon;
using Midnight.Shared.Protocol;

namespace Midnight.Daemon.Features.Host;

/// <summary>Host information for the panel. Reads /proc on Linux and falls back to runtime values elsewhere.</summary>
public class SystemInfoService
{
    private readonly DaemonConfiguration _configuration;
    private readonly DockerService _docker;
    private readonly CpuUsageSampler _cpu;

    public SystemInfoService(DaemonConfiguration configuration, DockerService docker, CpuUsageSampler cpu)
    {
        _configuration = configuration;
        _docker = docker;
        _cpu = cpu;
    }

    public async Task<SystemInfoResponse> GetAsync(CancellationToken cancellationToken)
    {
        var (memoryTotal, memoryUsed) = ReadMemory();
        var (diskTotal, diskUsed) = ReadDisk(_configuration.DataPath);

        return new SystemInfoResponse
        {
            DaemonVersion = typeof(SystemInfoService).Assembly.GetName().Version?.ToString(3) ?? "",
            ProtocolVersion = MidnightProtocol.Version,
            OperatingSystem = ReadOsName(),
            KernelVersion = ReadFile("/proc/sys/kernel/osrelease")?.Trim() ?? Environment.OSVersion.Version.ToString(),
            Architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            Hostname = Environment.MachineName,
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
            CpuCores = Environment.ProcessorCount,
            CpuModel = ReadCpuModel(),
            CpuUsage = Math.Round(_cpu.Usage, 1),
            MemoryTotalBytes = memoryTotal,
            MemoryUsedBytes = memoryUsed,
            DiskTotalBytes = diskTotal,
            DiskUsedBytes = diskUsed,
            Docker = await _docker.GetInfoAsync(cancellationToken)
        };
    }

    private static string ReadOsName()
    {
        var osRelease = ReadFile("/etc/os-release");

        var prettyName = osRelease?
            .Split('\n')
            .FirstOrDefault(x => x.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))?
            ["PRETTY_NAME=".Length..]
            .Trim('"');

        return prettyName ?? RuntimeInformation.OSDescription;
    }

    private static string? ReadCpuModel()
        => ReadFile("/proc/cpuinfo")?
            .Split('\n')
            .FirstOrDefault(x => x.StartsWith("model name", StringComparison.Ordinal))?
            .Split(':', 2)[1]
            .Trim();

    private static (long Total, long Used) ReadMemory()
    {
        var meminfo = ReadFile("/proc/meminfo");

        if (meminfo != null)
            return ParseMeminfo(meminfo);

        // Not linux: total from the runtime, used is unknown
        var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return (total, 0);
    }

    /// <summary>Parses /proc/meminfo. Used = MemTotal - MemAvailable, like "free" shows it.</summary>
    internal static (long Total, long Used) ParseMeminfo(string content)
    {
        long ReadKb(string key)
        {
            var line = content.Split('\n').FirstOrDefault(x => x.StartsWith(key + ":", StringComparison.Ordinal));
            var value = line?.Split(':', 2)[1].Trim().Split(' ')[0];
            return long.TryParse(value, out var kb) ? kb * 1024 : 0;
        }

        var total = ReadKb("MemTotal");
        var available = ReadKb("MemAvailable");

        return (total, Math.Max(0, total - available));
    }

    private static (long Total, long Used) ReadDisk(string path)
    {
        try
        {
            // The data path may not exist yet on a fresh node, use the closest existing parent
            var existing = path;
            while (!Directory.Exists(existing) && Path.GetDirectoryName(existing) is { } parent)
                existing = parent;

            var drive = new DriveInfo(existing);
            return (drive.TotalSize, drive.TotalSize - drive.AvailableFreeSpace);
        }
        catch (Exception)
        {
            return (0, 0);
        }
    }

    private static string? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
