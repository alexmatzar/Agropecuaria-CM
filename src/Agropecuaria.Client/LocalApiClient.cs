using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agropecuaria.Contracts;

namespace Agropecuaria.Client;

public sealed class LocalApiClient(HttpClient http, string accessToken)
{
    public Task<SyncStatus> GetSyncStatusAsync(CancellationToken ct = default) => Get<SyncStatus>("api/sync/status", ct);
    public Task<List<ProductView>> GetProductsAsync(CancellationToken ct = default) => Get<List<ProductView>>("api/products", ct);
    public Task<SaleResult> CompleteSaleAsync(CreateSaleRequest request, CancellationToken ct = default) => Post<SaleResult>("api/sales", request, ct);

    private async Task<T> Get<T>(string path, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, path);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(ct) ?? throw new HttpRequestException("Respuesta vacía.");
    }
    private async Task<T> Post<T>(string path, object value, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, path); request.Content = JsonContent.Create(value);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(ct) ?? throw new HttpRequestException("Respuesta vacía.");
    }
    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }
}
