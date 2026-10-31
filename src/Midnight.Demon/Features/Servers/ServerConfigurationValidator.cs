using System.Net;
using System.Text.RegularExpressions;
using Midnight.Shared.Http.Demon;

namespace Midnight.Demon.Features.Servers;

/// <summary>Checks a configuration from the panel before the demon uses it (pure, no side effects)</summary>
internal static partial class ServerConfigurationValidator
{
    public static List<string> Validate(DemonServerConfiguration? configuration)
    {
        var errors = new List<string>();

        if (configuration == null)
        {
            errors.Add("The configuration is missing");
            return errors;
        }

        if (configuration.Uuid == Guid.Empty)
            errors.Add("Uuid is required");

        if (string.IsNullOrWhiteSpace(configuration.DockerImage))
            errors.Add("DockerImage is required");

        var limits = configuration.Limits;

        if (limits == null)
        {
            errors.Add("Limits are required");
        }
        else
        {
            if (limits.Memory < 1)
                errors.Add("Limits.Memory must be at least 1 MiB");

            if (limits.Cpu < 0)
                errors.Add("Limits.Cpu must not be negative");

            if (limits.Swap < -1)
                errors.Add("Limits.Swap must be -1 (unlimited), 0 (disabled) or a size in MiB");

            if (limits.Disk < 0)
                errors.Add("Limits.Disk must not be negative");

            if (!string.IsNullOrWhiteSpace(limits.Threads) && !ThreadsPattern().IsMatch(limits.Threads.Trim()))
                errors.Add("Limits.Threads must look like 0, 0-3 or 0,2,4");
        }

        foreach (var allocation in (configuration.Allocations ?? []).Append(configuration.PrimaryAllocation))
        {
            if (allocation == null)
                continue;

            if (allocation.Port is < 1 or > 65535)
                errors.Add($"Allocation port {allocation.Port} must be between 1 and 65535");

            if (!string.IsNullOrWhiteSpace(allocation.IpAddress) && !IPAddress.TryParse(allocation.IpAddress.Trim(), out _))
                errors.Add($"Allocation ip '{allocation.IpAddress}' is not an ip address");
        }

        if (configuration.Process == null)
            errors.Add("Process is required");

        if (configuration.Install == null)
            errors.Add("Install is required");
        else if (!string.IsNullOrWhiteSpace(configuration.Install.Script) && string.IsNullOrWhiteSpace(configuration.Install.Image))
            errors.Add("Install.Image is required when there is an install script");

        if (configuration.Environment == null)
            errors.Add("Environment is required");

        return errors;
    }

    [GeneratedRegex(@"^[0-9]+(-[0-9]+)?(,[0-9]+(-[0-9]+)?)*$")]
    private static partial Regex ThreadsPattern();
}
