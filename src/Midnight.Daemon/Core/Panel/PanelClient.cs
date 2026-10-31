using System.Net.Http.Headers;
using System.Net.Http.Json;
using Midnight.Daemon.Core.Configuration;
using Midnight.Shared.Http;
using Midnight.Shared.Http.Daemon;
using Midnight.Shared.Protocol;

namespace Midnight.Daemon.Core.Panel;

/// <summary>Calls the panel api with the node token</summary>
public class PanelClient
{
    private readonly HttpClient _httpClient;

    public PanelClient(HttpClient httpClient, DaemonConfiguration configuration)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri(configuration.PanelUrl.TrimEnd('/') + "/");
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", NodeToken.Format(configuration.TokenId, configuration.Token));
        _httpClient.DefaultRequestHeaders.Add(MidnightProtocol.HeaderName, MidnightProtocol.Version.ToString());
    }

    public async Task<BootResponse> GetBootAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync("api/nodes/remote/boot", cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The panel responded with {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<BootResponse>(JsonDefaults.Options, cancellationToken)
               ?? throw new HttpRequestException("The panel sent an empty boot response");
    }
}
