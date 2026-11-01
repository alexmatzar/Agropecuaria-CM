using System.Net;
using System.Net.Http.Json;
using Agropecuaria.Contracts;
using Xunit;

namespace Agropecuaria.Tests;

[Trait("Category", "CloudIntegration")]
public sealed class CloudIntegrationTests
{
    [Fact]
    public async Task RealFirebasePostgresAndLocalHttpRecoverOfflineSalesIntoFreshTerminal()
    {
        using var demo = new FirebaseIntegrationTests.Demo(usePostgres: true);
        await demo.BootstrapAsync(); demo.StartAuthority();
        using var source = demo.StartTerminal(commercial: true); await demo.ReadyAsync(source.Http);
        await FirebaseIntegrationTests.Login(source.Http, demo.Email, demo.Password);
        (await source.Http.PostAsJsonAsync("api/admin/sync/enroll", new EnrollSyncRequest(true, false))).EnsureSuccessStatusCode();
        var product = new CreateProductRequest(Guid.NewGuid(), Guid.NewGuid(), "Concentrado", "Weight", "Quintal", 5,
            [new("Pound", 500)], new("001234567890", "Alimento", "Marca", "Prueba"));
        (await source.Http.PostAsJsonAsync("api/products", product)).EnsureSuccessStatusCode();
        await Synchronized(source.Http);

        demo.StopAuthority();
        var request = new CreateSaleRequest(Guid.NewGuid(), [new(product.ProductId, "Pound", 18)]);
        using var response = await source.Http.PostAsJsonAsync("api/sales", request); response.EnsureSuccessStatusCode();
        var receipt = (await response.Content.ReadFromJsonAsync<SaleResult>())!;
        Assert.Equal(9_000, receipt.TotalMinor);
        using var offline = await source.Http.PostAsync("api/admin/sync/run", null); offline.EnsureSuccessStatusCode();
        Assert.Equal("Offline", (await offline.Content.ReadFromJsonAsync<SyncStatus>())!.State);
        Assert.Equal(482_000_000, Assert.Single((await source.Http.GetFromJsonAsync<List<ProductView>>("api/products"))!).StockBase);

        demo.StartAuthority(); await AutomaticallySynchronized(source.Http);
        using var reader = demo.StartTerminal(commercial: true); await demo.ReadyAsync(reader.Http);
        await FirebaseIntegrationTests.Login(reader.Http, demo.Email, demo.Password);
        (await reader.Http.PostAsJsonAsync("api/admin/sync/enroll", new EnrollSyncRequest(false, false))).EnsureSuccessStatusCode();
        await Synchronized(reader.Http);
        Assert.Equal(482_000_000, Assert.Single((await reader.Http.GetFromJsonAsync<List<ProductView>>("api/products"))!).StockBase);
        Assert.Equal(receipt, Assert.Single((await reader.Http.GetFromJsonAsync<List<SaleResult>>("api/sales"))!));
        Assert.Equal(HttpStatusCode.Conflict, (await reader.Http.PostAsJsonAsync("api/sales", request)).StatusCode);

        using var replacement = demo.StartTerminal(commercial: true); await demo.ReadyAsync(replacement.Http);
        await FirebaseIntegrationTests.Login(replacement.Http, demo.Email, demo.Password);
        (await replacement.Http.PostAsJsonAsync("api/admin/sync/enroll", new EnrollSyncRequest(true, true))).EnsureSuccessStatusCode();
        await Synchronized(replacement.Http);
        (await source.Http.PostAsync("api/admin/sync/run", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await source.Http.PostAsJsonAsync("api/sales", new CreateSaleRequest(Guid.NewGuid(), request.Lines))).StatusCode);
        (await replacement.Http.PostAsJsonAsync("api/sales", new CreateSaleRequest(Guid.NewGuid(), [new(product.ProductId, "Pound", 1)]))).EnsureSuccessStatusCode();
        await Synchronized(replacement.Http); await Synchronized(reader.Http);
        Assert.Equal(481_000_000, Assert.Single((await reader.Http.GetFromJsonAsync<List<ProductView>>("api/products"))!).StockBase);
        Assert.Equal(2, (await reader.Http.GetFromJsonAsync<List<SaleResult>>("api/sales"))!.Count);
    }
    private static async Task Synchronized(HttpClient http)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30); SyncStatus? status = null;
        while (DateTime.UtcNow < deadline)
        {
            using var response = await http.PostAsync("api/admin/sync/run", null); response.EnsureSuccessStatusCode();
            status = await response.Content.ReadFromJsonAsync<SyncStatus>();
            if (status is { State: "UpToDate", PendingCount: 0 }) return;
            Assert.NotEqual("AttentionRequired", status!.State);
            await Task.Delay(150);
        }
        Assert.Fail("Sincronización no completada: " + status?.State + "/" + status?.ErrorCode);
    }
    private static async Task AutomaticallySynchronized(HttpClient http)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var status = await http.GetFromJsonAsync<SyncStatus>("api/sync/status");
            if (status is { State: "UpToDate", PendingCount: 0 }) return;
            await Task.Delay(200);
        }
        Assert.Fail("El trabajador no reanudó automáticamente el envío tras recuperar la conexión.");
    }
}
