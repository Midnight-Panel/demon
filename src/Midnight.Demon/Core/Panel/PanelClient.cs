using System.Net.Http.Headers;
using System.Net.Http.Json;
using Midnight.Demon.Core.Configuration;
using Midnight.Shared.Http;
using Midnight.Shared.Http.Demon;
using Midnight.Shared.Protocol;

namespace Midnight.Demon.Core.Panel;

/// <summary>Calls the panel api with the node token</summary>
public class PanelClient
{
    private readonly HttpClient _httpClient;

    public PanelClient(HttpClient httpClient, DemonConfiguration configuration)
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
        EnsureSuccess(response);

        return await response.Content.ReadFromJsonAsync<BootResponse>(JsonDefaults.Options, cancellationToken)
               ?? throw new HttpRequestException("The panel sent an empty boot response");
    }

    /// <summary>Configuration of every server of this node, the panel is the source of truth</summary>
    public async Task<List<DemonServerConfiguration>> GetServersAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync("api/nodes/remote/servers", cancellationToken);
        EnsureSuccess(response);

        return await response.Content.ReadFromJsonAsync<List<DemonServerConfiguration>>(JsonDefaults.Options, cancellationToken)
               ?? throw new HttpRequestException("The panel sent an empty server list");
    }

    /// <summary>Tells the panel whether the install script of a server succeeded</summary>
    public async Task ReportInstallAsync(Guid uuid, bool successful, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync($"api/nodes/remote/servers/{uuid}/install",
            new DemonInstallResultRequest { Successful = successful }, JsonDefaults.Options, cancellationToken);
        EnsureSuccess(response);
    }

    /// <summary>Tells the panel the result of a backup</summary>
    public async Task ReportBackupAsync(Guid backupUuid, DemonBackupResultRequest result, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync($"api/nodes/remote/backups/{backupUuid}", result, JsonDefaults.Options, cancellationToken);
        EnsureSuccess(response);
    }

    /// <summary>Tells the panel whether restoring a backup succeeded</summary>
    public async Task ReportRestoreAsync(Guid backupUuid, bool successful, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync($"api/nodes/remote/backups/{backupUuid}/restore",
            new DemonRestoreResultRequest { Successful = successful }, JsonDefaults.Options, cancellationToken);
        EnsureSuccess(response);
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The panel responded with {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
    }
}
