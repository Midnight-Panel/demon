using Docker.DotNet.Models;
using Midnight.Demon.Core.Configuration;
using Midnight.Demon.Features.Servers.Containers;
using Midnight.Shared.Http.Demon;
using Midnight.Shared.Http.Servers;

namespace Midnight.Demon.Tests;

public class ContainerParametersTests
{
    private const long MiB = 1024 * 1024;
    private static readonly Guid Uuid = Guid.Parse("4f9c1b0e-7d2a-4c1e-9b3f-2a6d8e5c1f00");

    private static DemonServerConfiguration Configuration() => new()
    {
        Uuid = Uuid,
        Name = "Test",
        DockerImage = "ghcr.io/midnight/java:21",
        StartupCommand = "java -jar server.jar",
        Environment = new Dictionary<string, string> { ["STARTUP"] = "java -Xmx1024M -jar server.jar", ["SERVER_JARFILE"] = "server.jar", ["BAD=NAME"] = "x" },
        Limits = new ServerLimitsDto { Cpu = 150, Memory = 1024, Swap = 0, Disk = 5120, IoWeight = 500, Threads = "0-1", OomKillerDisabled = true },
        PrimaryAllocation = new DemonAllocation { IpAddress = "10.0.0.5", Port = 25565 },
        Allocations = [new DemonAllocation { IpAddress = "10.0.0.5", Port = 25565 }, new DemonAllocation { IpAddress = "not-an-ip", Port = 25566 }],
        Process = new DemonProcessConfiguration { StopSignal = "sigint" },
        Install = new DemonInstallConfiguration { Script = "echo hi", Image = "debian:bookworm", Interpreter = "/bin/sh" }
    };

    [Fact]
    public void ForServer_MapsTheConfiguration()
    {
        var options = new ServerRuntimeConfiguration { DefaultPidLimit = 512, ContainerUser = "988:988" };
        var parameters = ContainerParameters.ForServer(Configuration(), "/var/lib/midnight/volumes/x", options);
        var host = parameters.HostConfig;

        Assert.Equal(Uuid.ToString(), parameters.Name);
        Assert.Equal("ghcr.io/midnight/java:21", parameters.Image);
        Assert.Equal("988:988", parameters.User);
        Assert.Equal(Uuid.ToString(), parameters.Labels[ContainerParameters.ServerLabel]);
        Assert.Equal("/home/container", parameters.WorkingDir);
        Assert.True(parameters.OpenStdin);
        Assert.True(parameters.AttachStdin);
        Assert.False(parameters.StdinOnce);
        Assert.False(parameters.Tty);
        Assert.Equal("SIGINT", parameters.StopSignal);

        Assert.Equal(["/var/lib/midnight/volumes/x:/home/container"], host.Binds);
        Assert.Equal(RestartPolicyKind.No, host.RestartPolicy.Name);
        Assert.Equal(1_500_000_000L, host.NanoCPUs);
        Assert.Equal("0-1", host.CpusetCpus);
        Assert.Equal((ushort)500, host.BlkioWeight);
        Assert.True(host.OomKillDisable);
        Assert.Equal(512L, host.PidsLimit);
        Assert.Equal(1024 * MiB, host.MemoryReservation);
        Assert.Equal(ContainerParameters.MemoryLimitBytes(1024), host.Memory);
        Assert.Equal(host.Memory, host.MemorySwap);
    }

    [Fact]
    public void Environment_ConfigurationWins_DefaultsAreAdded_InvalidNamesDropped()
    {
        var environment = ContainerParameters.BuildEnvironment(Configuration());

        Assert.Contains("STARTUP=java -Xmx1024M -jar server.jar", environment);
        Assert.Contains("SERVER_JARFILE=server.jar", environment);
        Assert.Contains("SERVER_MEMORY=1024", environment);
        Assert.Contains("SERVER_IP=10.0.0.5", environment);
        Assert.Contains("SERVER_PORT=25565", environment);
        Assert.Contains($"P_SERVER_UUID={Uuid}", environment);
        Assert.DoesNotContain(environment, x => x.StartsWith("BAD"));
    }

    [Fact]
    public void Ports_AreBoundForTcpAndUdp_OnTheirIp()
    {
        var (exposed, bindings) = ContainerParameters.BuildPorts(Configuration());

        Assert.Equal(["25565/tcp", "25565/udp", "25566/tcp", "25566/udp"], exposed.Keys.Order());
        Assert.Single(bindings["25565/tcp"]);
        Assert.Equal("10.0.0.5", bindings["25565/udp"][0].HostIP);
        Assert.Equal("25565", bindings["25565/udp"][0].HostPort);
        Assert.Equal("0.0.0.0", bindings["25566/tcp"][0].HostIP);
    }

    [Theory]
    [InlineData(1024, 1178)]
    [InlineData(4096, 4506)]
    [InlineData(8192, 8602)]
    public void MemoryLimit_HasOverhead(int memory, int expectedMiB)
    {
        Assert.Equal(expectedMiB * MiB, ContainerParameters.MemoryLimitBytes(memory));
    }

    [Theory]
    [InlineData(-1, -1L)]
    [InlineData(0, 1000L)]
    [InlineData(512, 1000L + 512 * 1024 * 1024)]
    public void Swap_IsMemoryPlusSwap(int swap, long expected)
    {
        Assert.Equal(expected, ContainerParameters.MemorySwapBytes(1000, swap));
    }

    [Fact]
    public void UnlimitedCpu_AndNoPidLimit()
    {
        var configuration = Configuration();
        configuration.Limits.Cpu = 0;
        configuration.Limits.Threads = " ";
        configuration.Limits.IoWeight = 5;
        configuration.Process.Features.PidLimit = 0;

        var host = ContainerParameters.ForServer(configuration, "/v", new ServerRuntimeConfiguration { ContainerUser = "" }).HostConfig;

        Assert.Equal(0L, host.NanoCPUs);
        Assert.Null(host.CpusetCpus);
        Assert.Equal((ushort)10, host.BlkioWeight);
        Assert.Null(host.PidsLimit);
    }

    [Fact]
    public void ForInstaller_RunsTheScriptWithTheInterpreter()
    {
        var parameters = ContainerParameters.ForInstaller(Configuration(), "/data/volumes/x", "/data/install/x", new ServerRuntimeConfiguration { InstallMinimumMemory = 2048 });

        Assert.Equal($"{Uuid}_installer", parameters.Name);
        Assert.Equal("debian:bookworm", parameters.Image);
        Assert.Equal(["/bin/sh", "/mnt/install/install.sh"], parameters.Entrypoint);
        Assert.Equal(["/data/volumes/x:/mnt/server", "/data/install/x:/mnt/install"], parameters.HostConfig.Binds);
        Assert.Equal(ContainerParameters.InstallerType, parameters.Labels[ContainerParameters.TypeLabel]);
        Assert.Equal(ContainerParameters.MemoryLimitBytes(2048), parameters.HostConfig.Memory);
        Assert.Contains("SERVER_JARFILE=server.jar", parameters.Env);
    }

    [Fact]
    public void ForUpdate_ContainsTheLiveLimits()
    {
        var update = ContainerParameters.ForUpdate(Configuration(), new ServerRuntimeConfiguration());

        Assert.Equal(ContainerParameters.MemoryLimitBytes(1024), update.Memory);
        Assert.Equal(update.Memory, update.MemorySwap);
        Assert.Equal(1_500_000_000L, update.NanoCPUs);
        Assert.Equal("0-1", update.CpusetCpus);
        Assert.Equal((ushort)500, update.BlkioWeight);
    }

    [Theory]
    [InlineData("SIGTERM", "SIGTERM")]
    [InlineData("sigint", "SIGINT")]
    [InlineData("KILL", "SIGKILL")]
    [InlineData("", "SIGTERM")]
    [InlineData(null, "SIGTERM")]
    [InlineData("SIG TERM; rm", "SIGTERM")]
    [InlineData("15", "15")]
    public void NormalizeSignal(string? input, string expected)
    {
        Assert.Equal(expected, ContainerParameters.NormalizeSignal(input));
    }

    [Theory]
    [InlineData("debian", "debian", "latest")]
    [InlineData("ghcr.io/org/java:21", "ghcr.io/org/java", "21")]
    [InlineData("localhost:5000/image", "localhost:5000/image", "latest")]
    [InlineData("localhost:5000/image:1.0", "localhost:5000/image", "1.0")]
    [InlineData("debian@sha256:abc", "debian@sha256:abc", null)]
    public void ImageReference_Parse(string image, string repository, string? tag)
    {
        Assert.Equal(new ImageReference(repository, tag), ImageReference.Parse(image));
    }
}

public class ContainerStatsTests
{
    private const string Sample = """
        {
          "cpu_stats": { "cpu_usage": { "total_usage": 3000000000 }, "system_cpu_usage": 20000000000, "online_cpus": 4 },
          "precpu_stats": { "cpu_usage": { "total_usage": 1000000000 }, "system_cpu_usage": 12000000000, "online_cpus": 4 },
          "memory_stats": { "usage": 600000000, "limit": 2000000000, "stats": { "inactive_file": 100000000 } },
          "networks": { "eth0": { "rx_bytes": 100, "tx_bytes": 200 }, "eth1": { "rx_bytes": 1, "tx_bytes": 2 } }
        }
        """;

    [Fact]
    public void CpuPercent_IsRelativeToOneCore()
    {
        // 2s of cpu time in 8s of system time on 4 cpus = one full core
        var sample = ContainerStatsSample.Parse(Sample)!;

        Assert.Equal(100, ContainerUsage.CalculateCpuPercent(sample), 3);
    }

    [Fact]
    public void FirstSample_HasNoCpuUsage()
    {
        var sample = ContainerStatsSample.Parse("""{ "cpu_stats": { "cpu_usage": { "total_usage": 5 }, "system_cpu_usage": 10, "online_cpus": 1 }, "precpu_stats": { "cpu_usage": { "total_usage": 0 }, "system_cpu_usage": 0 } }""")!;

        Assert.Equal(0, ContainerUsage.CalculateCpuPercent(sample));
    }

    [Fact]
    public void CpuCount_FallsBackToPerCpuUsage()
    {
        var sample = ContainerStatsSample.Parse("""
            { "cpu_stats": { "cpu_usage": { "total_usage": 200, "percpu_usage": [1, 2] }, "system_cpu_usage": 400 },
              "precpu_stats": { "cpu_usage": { "total_usage": 100 }, "system_cpu_usage": 200 } }
            """)!;

        Assert.Equal(100, ContainerUsage.CalculateCpuPercent(sample), 3);
    }

    [Fact]
    public void Usage_SubtractsThePageCache_AndSumsNetworks()
    {
        var usage = ContainerUsage.From(ContainerStatsSample.Parse(Sample)!);

        Assert.Equal(500000000, usage.MemoryBytes);
        Assert.Equal(101, usage.NetworkRxBytes);
        Assert.Equal(202, usage.NetworkTxBytes);
        Assert.Equal(100, usage.CpuPercent);
    }

    [Fact]
    public void Memory_CgroupV1Cache()
    {
        var memory = new Midnight.Demon.Features.Servers.Containers.MemoryStats { Usage = 1000, Stats = new Dictionary<string, ulong> { ["total_inactive_file"] = 400 } };

        Assert.Equal(600, ContainerUsage.CalculateMemoryBytes(memory));
        Assert.Equal(0, ContainerUsage.CalculateMemoryBytes(null));
    }
}

public class ImagePullErrorTests
{
    [Theory]
    [InlineData("error from registry: unauthorized\nunauthorized")]
    [InlineData("pull access denied for midnight/nope, repository does not exist")]
    [InlineData("manifest unknown")]
    [InlineData("{\"message\":\"error from registry: unauthorized\\nunauthorized\"}")]
    public void MissingOrPrivateImagesNameTheImage(string error)
    {
        var message = Midnight.Demon.Features.Docker.DockerService.DescribePullError("ghcr.io/midnight-panel/installers:alpine", error);

        Assert.StartsWith("The docker image ghcr.io/midnight-panel/installers:alpine does not exist or is not public", message);
        Assert.DoesNotContain("\n", message);
        Assert.DoesNotContain("{", message);
    }

    [Fact]
    public void OtherErrorsKeepTheirMessage()
        => Assert.Equal("Pulling node:22-alpine failed: no space left on device",
            Midnight.Demon.Features.Docker.DockerService.DescribePullError("node:22-alpine", "no space left on device\n"));
}
