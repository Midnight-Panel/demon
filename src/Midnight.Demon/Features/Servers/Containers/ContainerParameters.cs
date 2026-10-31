using System.Net;
using Docker.DotNet.Models;
using Midnight.Demon.Core.Configuration;
using Midnight.Shared.Http.Demon;

namespace Midnight.Demon.Features.Servers.Containers;

/// <summary>Maps a <see cref="DemonServerConfiguration"/> to docker container parameters (pure, no docker calls)</summary>
internal static class ContainerParameters
{
    public const string ServerLabel = "midnight.server";
    public const string TypeLabel = "midnight.type";
    public const string ServerType = "server";
    public const string InstallerType = "installer";

    public const string ServerDirectory = "/home/container";
    public const string InstallServerDirectory = "/mnt/server";
    public const string InstallScriptDirectory = "/mnt/install";
    public const string InstallScriptName = "install.sh";

    private const long MiB = 1024 * 1024;

    public static string ServerContainerName(Guid uuid) => uuid.ToString();
    public static string InstallerContainerName(Guid uuid) => $"{uuid}_installer";

    public static CreateContainerParameters ForServer(DemonServerConfiguration configuration, string volumePath, ServerRuntimeConfiguration options)
    {
        var (exposedPorts, portBindings) = BuildPorts(configuration);
        var hostConfig = BuildResources(configuration, options, configuration.Limits.Memory);

        hostConfig.Binds = [$"{volumePath}:{ServerDirectory}"];
        hostConfig.PortBindings = portBindings;
        hostConfig.RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.No };
        hostConfig.SecurityOpt = ["no-new-privileges"];
        hostConfig.LogConfig = new LogConfig
        {
            Type = "json-file",
            Config = new Dictionary<string, string> { ["max-size"] = "5m", ["max-file"] = "1" }
        };

        return new CreateContainerParameters
        {
            Name = ServerContainerName(configuration.Uuid),
            Image = configuration.DockerImage,
            User = string.IsNullOrEmpty(options.ContainerUser) ? null : options.ContainerUser,
            Env = BuildEnvironment(configuration),
            Labels = new Dictionary<string, string> { [ServerLabel] = configuration.Uuid.ToString(), [TypeLabel] = ServerType },
            ExposedPorts = exposedPorts,
            WorkingDir = ServerDirectory,
            AttachStdin = true,
            AttachStdout = true,
            AttachStderr = true,
            OpenStdin = true,
            StdinOnce = false,
            Tty = false,
            StopSignal = NormalizeSignal(configuration.Process.StopSignal),
            HostConfig = hostConfig
        };
    }

    /// <summary>One-off container that runs the install script with the volume at /mnt/server</summary>
    public static CreateContainerParameters ForInstaller(DemonServerConfiguration configuration, string volumePath, string scriptDirectory, ServerRuntimeConfiguration options)
    {
        var hostConfig = BuildResources(configuration, options, Math.Max(configuration.Limits.Memory, options.InstallMinimumMemory));

        hostConfig.Binds = [$"{volumePath}:{InstallServerDirectory}", $"{scriptDirectory}:{InstallScriptDirectory}"];
        hostConfig.RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.No };
        hostConfig.LogConfig = new LogConfig
        {
            Type = "json-file",
            Config = new Dictionary<string, string> { ["max-size"] = "5m", ["max-file"] = "1" }
        };

        var interpreter = string.IsNullOrWhiteSpace(configuration.Install.Interpreter) ? "/bin/bash" : configuration.Install.Interpreter.Trim();

        return new CreateContainerParameters
        {
            Name = InstallerContainerName(configuration.Uuid),
            Image = configuration.Install.Image,
            Env = BuildEnvironment(configuration),
            Labels = new Dictionary<string, string> { [ServerLabel] = configuration.Uuid.ToString(), [TypeLabel] = InstallerType },
            WorkingDir = InstallServerDirectory,
            Entrypoint = [interpreter, $"{InstallScriptDirectory}/{InstallScriptName}"],
            Cmd = [],
            AttachStdin = false,
            AttachStdout = true,
            AttachStderr = true,
            OpenStdin = false,
            Tty = false,
            HostConfig = hostConfig
        };
    }

    /// <summary>The limits docker can change on a running container</summary>
    public static ContainerUpdateParameters ForUpdate(DemonServerConfiguration configuration, ServerRuntimeConfiguration options)
    {
        var resources = BuildResources(configuration, options, configuration.Limits.Memory);

        return new ContainerUpdateParameters
        {
            Memory = resources.Memory,
            MemoryReservation = resources.MemoryReservation,
            MemorySwap = resources.MemorySwap,
            NanoCPUs = resources.NanoCPUs,
            CpusetCpus = resources.CpusetCpus,
            BlkioWeight = resources.BlkioWeight,
            PidsLimit = resources.PidsLimit
        };
    }

    private static HostConfig BuildResources(DemonServerConfiguration configuration, ServerRuntimeConfiguration options, int memoryMiB)
    {
        var limits = configuration.Limits;
        var memory = MemoryLimitBytes(memoryMiB);
        var pids = configuration.Process.Features.PidLimit ?? options.DefaultPidLimit;

        return new HostConfig
        {
            Memory = memory,
            MemoryReservation = memoryMiB * MiB,
            MemorySwap = MemorySwapBytes(memory, limits.Swap),
            NanoCPUs = limits.Cpu > 0 ? limits.Cpu * 10_000_000L : 0,
            CpusetCpus = string.IsNullOrWhiteSpace(limits.Threads) ? null : limits.Threads.Trim(),
            BlkioWeight = (ushort)Math.Clamp(limits.IoWeight, 10, 1000),
            OomKillDisable = limits.OomKillerDisabled,
            PidsLimit = pids > 0 ? pids : null
        };
    }

    /// <summary>
    /// Hard memory limit: the configured memory plus some overhead for the processes around the game
    /// (java, the shell...), otherwise the kernel kills servers that use exactly their limit
    /// </summary>
    public static long MemoryLimitBytes(int memoryMiB)
    {
        var multiplier = memoryMiB switch
        {
            <= 2048 => 1.15,
            <= 4096 => 1.10,
            _ => 1.05
        };

        return (long)Math.Round(memoryMiB * multiplier) * MiB;
    }

    /// <summary>Docker's MemorySwap is memory + swap: 0 = no swap (equal to the memory), -1 = unlimited</summary>
    public static long MemorySwapBytes(long memoryBytes, int swapMiB) => swapMiB switch
    {
        < 0 => -1,
        0 => memoryBytes,
        _ => memoryBytes + swapMiB * MiB
    };

    public static List<string> BuildEnvironment(DemonServerConfiguration configuration)
    {
        var environment = new Dictionary<string, string>(configuration.Environment.Where(x => IsValidVariableName(x.Key)), StringComparer.Ordinal);

        environment.TryAdd("STARTUP", configuration.StartupCommand);
        environment.TryAdd("SERVER_MEMORY", configuration.Limits.Memory.ToString());
        environment.TryAdd("SERVER_IP", configuration.PrimaryAllocation.IpAddress);
        environment.TryAdd("SERVER_PORT", configuration.PrimaryAllocation.Port.ToString());
        environment.TryAdd("P_SERVER_UUID", configuration.Uuid.ToString());

        return environment.Select(x => $"{x.Key}={x.Value}").ToList();
    }

    /// <summary>Every allocation (including the primary one) for tcp and udp, bound on its ip</summary>
    public static (Dictionary<string, EmptyStruct> Exposed, Dictionary<string, IList<PortBinding>> Bindings) BuildPorts(DemonServerConfiguration configuration)
    {
        var exposed = new Dictionary<string, EmptyStruct>();
        var bindings = new Dictionary<string, IList<PortBinding>>();

        var allocations = configuration.Allocations
            .Append(configuration.PrimaryAllocation)
            .Where(x => x.Port is > 0 and <= 65535)
            .Select(x => (Ip: NormalizeIp(x.IpAddress), x.Port))
            .Distinct()
            .OrderBy(x => x.Port);

        foreach (var (ip, port) in allocations)
        {
            foreach (var protocol in new[] { "tcp", "udp" })
            {
                var key = $"{port}/{protocol}";
                exposed[key] = default;

                if (!bindings.TryGetValue(key, out var list))
                    bindings[key] = list = new List<PortBinding>();

                list.Add(new PortBinding { HostIP = ip, HostPort = port.ToString() });
            }
        }

        return (exposed, bindings);
    }

    private static string NormalizeIp(string ip) =>
        IPAddress.TryParse(ip.Trim(), out var address) ? address.ToString() : "0.0.0.0";

    private static bool IsValidVariableName(string name) =>
        name.Length > 0 && !name.Contains('=') && !name.Contains('\0');

    /// <summary>"SIGTERM", "sigint" or "TERM" → docker signal name, invalid values become SIGTERM</summary>
    public static string NormalizeSignal(string? signal)
    {
        var value = signal?.Trim().ToUpperInvariant() ?? "";

        if (value.Length == 0)
            return "SIGTERM";

        if (int.TryParse(value, out _))
            return value;

        if (!value.StartsWith("SIG", StringComparison.Ordinal))
            value = "SIG" + value;

        return value.Length > 3 && value.Skip(3).All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c is '+' or '-') ? value : "SIGTERM";
    }
}
