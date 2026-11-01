using System.Security.Cryptography;
using System.Text.Json;
using Agropecuaria.Application;
using Agropecuaria.Contracts;
using Agropecuaria.Domain;
using Agropecuaria.Infrastructure;
using Xunit;

namespace Agropecuaria.Tests;

public sealed class ReplicationTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), "agro-replay-" + Guid.NewGuid().ToString("N"));
    private readonly InstallationIdentity sourceId = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "GTQ");
    private readonly SqlitePosStore source;
    public ReplicationTests() { source = New("source", sourceId); }
    private SqlitePosStore New(string name, InstallationIdentity id)
    {
        var store = new SqlitePosStore(Path.Combine(path, name + ".db"), id, RandomNumberGenerator.GetBytes(32)); store.Initialize(); return store;
    }
    private (CreateSaleRequest Request, SaleResult Sale, List<CloudChange> Changes) Events()
    {
        var product = source.CreateProduct(new(Guid.NewGuid(), Guid.NewGuid(), "Concentrado", "Weight", "Quintal", 5,
            [new("Pound", 500)], new("001234567890", "Alimento", "Marca", "Descripción")));
        var request = new CreateSaleRequest(Guid.NewGuid(), [new(product.Id, "Pound", 18)]);
        var sale = source.CompleteSale(request);
        return (request, sale, source.ReadPending(100).Select((e, i) => new CloudChange(i + 1, e)).ToList());
    }
    private SqlitePosStore Replica(long head, bool writable = true)
    {
        var identity = sourceId with { DeviceId = Guid.NewGuid() };
        var replica = New(Guid.NewGuid().ToString("N"), identity);
        replica.EnrollDevice(new(identity.DeviceId, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), writable, head));
        return replica;
    }
    [Fact]
    public void RestoringPagesReconstructStockReceiptsMetadataAndIdempotencyWithoutEcho()
    {
        var data = Events(); var replica = Replica(2);
        Assert.False(replica.IsReady());
        replica.ApplyPage(new(0, 1, 2, [data.Changes[0]]));
        Assert.Throws<OperationConflict>(() => replica.CompleteSale(data.Request));
        replica.ApplyPage(new(1, 2, 2, [data.Changes[1]]));
        Assert.True(replica.IsReady());
        var product = Assert.Single(replica.ListProducts());
        Assert.Equal(482_000_000, product.StockBase); Assert.Equal("001234567890", product.Details!.Barcode);
        Assert.Equal(data.Sale, Assert.Single(replica.ListSales()));
        Assert.Equal(data.Sale, replica.CompleteSale(data.Request)); // Retry after disaster recovery.
        Assert.Equal(0, replica.PendingCount()); Assert.Equal(2, replica.CloudCursor());
    }
    [Fact]
    public void PageFailureRollsBackProductsSalesInboxAndCursorTogether()
    {
        var data = Events(); var replica = Replica(2);
        var payload = BusinessEvents.Payload<SaleCompletedPayload>(data.Changes[1].Envelope);
        var invalid = data.Changes[1] with { Envelope = data.Changes[1].Envelope with { Payload = JsonSerializer.Serialize(payload with { Sale = payload.Sale with { TotalMinor = 1 } }) } };
        Assert.Throws<RuleViolation>(() => replica.ApplyPage(new(0, 2, 2, [data.Changes[0], invalid])));
        Assert.Empty(replica.ListProducts()); Assert.Empty(replica.ListSales()); Assert.Equal(0, replica.CloudCursor());
    }
    [Fact]
    public void DuplicatePagesAndOwnEventsNeverSubtractTwice()
    {
        var data = Events();
        source.EnrollDevice(new(sourceId.DeviceId, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), true, 0));
        var page = new CloudChangePage(0, 2, 2, data.Changes); source.ApplyPage(page); source.ApplyPage(page);
        Assert.Equal(482_000_000, Assert.Single(source.ListProducts()).StockBase); Assert.Equal(0, source.PendingCount());
        Assert.Single(source.ListSales());
    }
    [Fact]
    public void WrongTenantGapsAndAlteredOwnEventsAreRejected()
    {
        var data = Events(); var replica = Replica(2);
        Assert.Throws<ReplicationRejected>(() => replica.ApplyPage(new(0, 1, 2, [data.Changes[0] with { Cursor = 2 }])));
        Assert.Throws<ReplicationRejected>(() => replica.ApplyPage(new(0, 1, 2, [data.Changes[0] with
            { Envelope = data.Changes[0].Envelope with { TenantId = Guid.NewGuid() } }])));
        source.EnrollDevice(new(sourceId.DeviceId, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), true, 0));
        Assert.Throws<ReplicationRejected>(() => source.ApplyPage(new(0, 1, 2, [data.Changes[0] with
            { Envelope = data.Changes[0].Envelope with { Payload = "{}" } }])));
        Assert.Equal(0, replica.CloudCursor()); Assert.Equal(2, source.PendingCount());
    }
    [Fact]
    public void ReadReplicaCannotSellEvenWithAdministratorRequests()
    {
        var data = Events(); var replica = Replica(2, false); replica.ApplyPage(new(0, 2, 2, data.Changes));
        Assert.Throws<OperationConflict>(() => replica.CompleteSale(new(Guid.NewGuid(), data.Request.Lines)));
        Assert.Equal(482_000_000, Assert.Single(replica.ListProducts()).StockBase);
    }
    [Fact]
    public void EnrollmentCanResumeWithSameSecretAndFreezesWritesUntilAcknowledged()
    {
        var request = source.PrepareEnrollment(true, false);
        Assert.True(source.EnrollmentPending()); Assert.Null(source.Credential());
        Assert.Equal(request, source.PrepareEnrollment(true, false));
        Assert.Throws<OperationConflict>(() => source.CreateProduct(new(Guid.NewGuid(), Guid.NewGuid(), "Blocked", "Count", "Piece", 1, [new("Piece", 1)])));
        Assert.Throws<OperationConflict>(() => source.PrepareEnrollment(true, true));
        source.EnrollDevice(new(sourceId.DeviceId, request.Secret, true, 0));
        Assert.False(source.EnrollmentPending()); Assert.Equal(request.Secret, source.Credential()!.Secret);
        Assert.Throws<OperationConflict>(() => source.PrepareEnrollment(true, false));
    }
    [Fact]
    public void ZeroOpeningAndBarcodeUniquenessAreEnforced()
    {
        var product = source.CreateProduct(new(Guid.NewGuid(), Guid.NewGuid(), "Empty", "Count", "Piece", 0, [new("Piece", 1)], new("001", null, null, null)));
        Assert.Equal(0, product.StockBase);
        Assert.Throws<OperationConflict>(() => source.CreateProduct(new(Guid.NewGuid(), Guid.NewGuid(), "Duplicate", "Count", "Piece", 1, [new("Piece", 1)], new("001", null, null, null))));
        Assert.Single(source.ListProducts()); Assert.Equal(1, source.PendingCount());
    }
    [Fact]
    public async Task KnownDeviceRevocationStopsFurtherLocalWrites()
    {
        var data = Events(); source.EnrollDevice(new(sourceId.DeviceId, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), true, 0));
        var coordinator = new SyncCoordinator(source, new RevokedTransport(), source); await coordinator.RunOnceAsync();
        Assert.Equal("Unauthorized", coordinator.Status.ErrorCode); Assert.Equal(2, source.PendingCount());
        Assert.Throws<OperationConflict>(() => source.CompleteSale(new(Guid.NewGuid(), data.Request.Lines)));
    }
    private sealed class RevokedTransport : IChangeTransport
    {
        public bool IsConfigured => true;
        public Task<DeliveryOutcome> ProbeAsync(CancellationToken ct) => Task.FromResult(DeliveryOutcome.Unauthorized);
        public Task<DeliveryOutcome> SendAsync(SyncEnvelope e, CancellationToken ct) => Task.FromResult(DeliveryOutcome.Unauthorized);
        public Task<CloudChangePage> PullAsync(long after, CancellationToken ct) => throw new CloudIdentityRejected();
    }
    public void Dispose() => Directory.Delete(path, true);
}
