using Docker.DotNet;
using Midnight.Demon.Features.Servers.Output;

namespace Midnight.Demon.Features.Servers.Containers;

internal static class ContainerOutput
{
    /// <summary>
    /// Reads the multiplexed stdout/stderr stream of a container (Tty = false) until it ends and reports every line.
    /// stdout and stderr have their own splitter so interleaved frames never mix two half lines.
    /// </summary>
    public static async Task PumpAsync(MultiplexedStream stream, Action<string> onLine, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var stdout = new LineSplitter();
        var stderr = new LineSplitter();
        var lines = new List<string>();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken);

                if (result.EOF)
                    break;

                var splitter = result.Target == MultiplexedStream.TargetStream.StandardError ? stderr : stdout;
                splitter.Append(buffer.AsSpan(0, result.Count), lines);

                Report(lines, onLine);
            }
        }
        finally
        {
            stdout.Flush(lines);
            stderr.Flush(lines);
            Report(lines, onLine);
        }
    }

    private static void Report(List<string> lines, Action<string> onLine)
    {
        foreach (var line in lines)
            onLine(line);

        lines.Clear();
    }
}
