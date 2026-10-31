using System.Collections.Concurrent;
using Midnight.Demon.Core.Configuration;
using Midnight.Shared.Http.Servers;

namespace Midnight.Demon.Features.Servers.Files;

/// <summary>
/// Downloads a file from a url into a server volume in the background (POST /files/pull). Only http(s) to public
/// addresses (<see cref="PublicAddressGuard"/>), at most 5 redirects (each checked again), at most the remaining disk
/// space. The result is a notice in the console.
/// </summary>
public sealed class FilePuller : IDisposable
{
    public const int MaxRedirects = 5;

    private readonly ServerFiles _serverFiles;
    private readonly FilesConfiguration _options;
    private readonly ILogger<FilePuller> _logger;
    private readonly CancellationToken _stopping;
    private readonly HttpClient _client;
    private readonly ConcurrentDictionary<Guid, int> _running = new();

    public FilePuller(ServerFiles serverFiles, DemonConfiguration configuration, ILogger<FilePuller> logger, IHostApplicationLifetime lifetime)
    {
        _serverFiles = serverFiles;
        _options = configuration.Files;
        _logger = logger;
        _stopping = lifetime.ApplicationStopping;
        _client = new HttpClient(PublicAddressGuard.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd($"Midnight-Demon/{typeof(FilePuller).Assembly.GetName().Version?.ToString(3) ?? "0"}");
    }

    public void Dispose() => _client.Dispose();

    /// <summary>Checks the request and starts the download in the background</summary>
    public void Start(ServerInstance server, PullFileRequest request)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var url) || !IsHttp(url))
            throw ServerApiException.BadRequest("Invalid url", "Only http and https urls can be downloaded");

        if (!string.IsNullOrEmpty(url.UserInfo))
            throw ServerApiException.BadRequest("Invalid url", "Urls with credentials are not supported");

        var volume = _serverFiles.GetVolume(server);
        var directory = ServerFiles.Resolve(volume, request.Directory, allowRoot: true);

        if (File.Exists(directory))
            throw ServerApiException.BadRequest("Not a directory", $"'{request.Directory}' is a file");

        string? fileName = null;

        if (request.FileName != null)
        {
            fileName = FileNames.Sanitize(request.FileName) ?? throw ServerApiException.BadRequest("Invalid file name");
            CheckTarget(server, volume, directory, fileName);
        }

        if (server.GetRemainingDiskBytes() <= 0)
            throw ServerApiException.TooLarge("Disk limit exceeded", "The server has no disk space left");

        var count = _running.AddOrUpdate(server.Uuid, 1, (_, current) => current + 1);

        if (count > _options.MaxConcurrentPulls)
        {
            Release(server.Uuid);
            throw ServerApiException.Conflict($"At most {_options.MaxConcurrentPulls} downloads can run at the same time");
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(server, url, volume, directory, fileName);
            }
            finally
            {
                Release(server.Uuid);
            }
        }, CancellationToken.None);
    }

    private void Release(Guid uuid)
    {
        if (_running.AddOrUpdate(uuid, 0, (_, current) => current - 1) <= 0)
            _running.TryRemove(new KeyValuePair<Guid, int>(uuid, 0));
    }

    private async Task RunAsync(ServerInstance server, Uri url, string volume, string directory, string? fileName)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.PullTimeoutSeconds));

        var host = url.Host;
        string? temporary = null;

        try
        {
            using var response = await SendAsync(url, timeout.Token);
            var name = fileName ?? GetFileName(response) ?? throw new PullException("The url does not contain a file name, set one");
            var target = CheckTarget(server, volume, directory, name);
            var existingSize = File.Exists(target) ? new FileInfo(target).Length : 0;
            var remaining = server.GetRemainingDiskBytes();
            var maxBytes = remaining == long.MaxValue ? remaining : remaining + existingSize;

            if (response.Content.Headers.ContentLength > maxBytes)
                throw new PullException($"The file is larger than the remaining disk space of the server");

            _serverFiles.CreateDirectories(volume, directory);
            temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.midnight-download");

            long written;

            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            await using (var counting = new CountingWriteStream(output, maxBytes, leaveOpen: true))
            {
                await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
                await body.CopyToAsync(counting, timeout.Token);
                written = counting.BytesWritten;
            }

            // the target may have been created meanwhile, it is replaced like an upload
            File.Move(temporary, target, overwrite: true);
            temporary = null;

            _serverFiles.SetOwner(target);
            server.AddDiskBytes(written - existingSize);

            _logger.LogInformation("Downloaded {Bytes} bytes from {Host} into server {Uuid}", written, host, server.Uuid);
            server.Notice($"Downloaded {name} ({FormatBytes(written)}) from {host}");
        }
        catch (Exception e)
        {
            var reason = e switch
            {
                PullException or BlockedAddressException => e.Message,
                ServerApiException api => api.Detail ?? api.Title,
                SizeLimitExceededException => "The file is larger than the remaining disk space of the server",
                OperationCanceledException when _stopping.IsCancellationRequested => "The demon is shutting down",
                OperationCanceledException => $"The download did not finish within {_options.PullTimeoutSeconds} seconds",
                HttpRequestException http => http.InnerException is BlockedAddressException blocked ? blocked.Message : http.Message,
                IOException io => io.Message,
                _ => "Unexpected error"
            };

            if (e is PullException or BlockedAddressException or ServerApiException or SizeLimitExceededException or OperationCanceledException or HttpRequestException)
                _logger.LogInformation("Download from {Host} into server {Uuid} failed: {Reason}", host, server.Uuid, reason);
            else
                _logger.LogError(e, "Download from {Host} into server {Uuid} failed", host, server.Uuid);

            server.Notice($"The download from {host} failed: {reason}");
        }
        finally
        {
            if (temporary != null)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // best effort
                }
            }
        }
    }

    /// <summary>Sends the request and follows up to <see cref="MaxRedirects"/> redirects, each to http(s) only (the address check is in the handler)</summary>
    private async Task<HttpResponseMessage> SendAsync(Uri url, CancellationToken cancellationToken)
    {
        for (var redirects = 0; ; redirects++)
        {
            var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                response.Dispose();

                if (redirects >= MaxRedirects)
                    throw new PullException($"More than {MaxRedirects} redirects");

                url = location.IsAbsoluteUri ? location : new Uri(url, location);

                if (!IsHttp(url) || !string.IsNullOrEmpty(url.UserInfo))
                    throw new PullException("Redirected to a url that is not http(s)");

                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new PullException($"The server responded with {(int)status} {status}");
            }

            return response;
        }
    }

    /// <summary>Full path of the downloaded file, 403 for the denylist, 409 when a directory has the name</summary>
    private static string CheckTarget(ServerInstance server, string volume, string directory, string name)
    {
        var target = ServerFiles.Resolve(volume, ServerFiles.Join(ServerFileSystem.ToRelative(volume, directory), name));
        ServerFiles.CheckDenylist(ServerFiles.GetDenylist(server), volume, target);

        if (Directory.Exists(target))
            throw ServerApiException.Conflict($"'{name}' already exists as a directory");

        return target;
    }

    internal static string? GetFileName(HttpResponseMessage response)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        var name = FileNames.Sanitize(disposition?.FileNameStar?.Trim('"')) ?? FileNames.Sanitize(disposition?.FileName?.Trim('"'));

        if (name != null)
            return name;

        var path = response.RequestMessage?.RequestUri?.AbsolutePath;
        return path == null ? null : FileNames.Sanitize(Uri.UnescapeDataString(path.Split('/').LastOrDefault() ?? ""));
    }

    private static bool IsHttp(Uri url) => url.Scheme is "http" or "https";

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.##} GiB",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.##} MiB",
        >= 1024 => $"{bytes / 1024.0:0.##} KiB",
        _ => $"{bytes} B"
    };

    private sealed class PullException(string message) : Exception(message);
}
