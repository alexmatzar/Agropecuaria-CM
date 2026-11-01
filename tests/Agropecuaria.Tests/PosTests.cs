using System.Net;
using System.Net.Http.Json;
using Agropecuaria.Application;
using Agropecuaria.Contracts;
using Agropecuaria.Domain;
using Agropecuaria.Infrastructure;
using Xunit;

namespace Agropecuaria.Tests;

public sealed class PosTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "agropecuaria-tests-" + Guid.NewGuid().ToString("N"));
    private readonly InstallationIdentity identity = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "GTQ");
    private readonly SqlitePosStore store;
    public PosTests() { store = NewStore(); store.Initialize(); }
    private SqlitePosStore NewStore() => new(Path.Combine(directory, "local.db"), identity);
    private ProductView Product(decimal opening = 5) => store.CreateProduct(new(Guid.NewGuid(), Guid.NewGuid(), "Concentrado",
        "Weight", "Quintal", opening, [new("Pound", 500), new("Ounce", 40), new("Quintal", 45_000)]));
    private static CreateSaleRequest Sale(Guid productId, decimal quantity, Guid? operation = null) =>
        new(operation ?? Guid.NewGuid(), [new(productId, "Pound", quantity)]);

    [Fact]
    public void FiveQuintalsMinusEighteenPoundsLeaves482Pounds()
    {
        var product = Product(); var result = store.CompleteSale(Sale(product.Id, 18));
        Assert.Equal(482_000_000L, Assert.Single(store.ListProducts()).StockBase);
        Assert.Equal(9_000, result.TotalMinor);
        Assert.Equal(2, store.PendingCount());
    }

    [Theory]
    [InlineData(SaleUnit.Quintal, 100_000_000L)]
    [InlineData(SaleUnit.Arroba, 25_000_000L)]
    [InlineData(SaleUnit.Pound, 1_000_000L)]
    [InlineData(SaleUnit.Ounce, 62_500L)]
    public void WeightConversionsAreExact(SaleUnit unit, long expected) => Assert.Equal(expected, Quantities.ToBase(ProductDimension.Weight, unit, 1));

    [Fact]
    public void FractionalOuncesRemainExact() => Assert.Equal(6_250, Quantities.ToBase(ProductDimension.Weight, SaleUnit.Ounce, 0.1m));
    [Fact]
    public void FractionalPiecesAreRejected() => Assert.Throws<RuleViolation>(() => Quantities.ToBase(ProductDimension.Count, SaleUnit.Piece, 0.5m));
    [Fact]
    public void WeightCannotBeConvertedToPieces() => Assert.Throws<RuleViolation>(() => Quantities.ToBase(ProductDimension.Weight, SaleUnit.Piece, 1));
    [Fact]
    public void RoundingIsExplicitPerLine() => Assert.Equal(1, Quantities.LineTotalMinor(5, 0.1m));

    [Fact]
    public void FailedMultiLineSaleRollsBackStockAndOutbox()
    {
        var product = Product(1);
        var request = new CreateSaleRequest(Guid.NewGuid(), [new(product.Id, "Pound", 90), new(product.Id, "Pound", 20)]);
        Assert.Throws<RuleViolation>(() => store.CompleteSale(request));
        Assert.Equal(100_000_000, Assert.Single(store.ListProducts()).StockBase);
        Assert.Equal(1, store.PendingCount());
    }
    [Fact]
    public void RetryingSaleReturnsSameReceiptWithoutDiscountingTwice()
    {
        var request = Sale(Product().Id, 18);
        var original = store.CompleteSale(request);
        Assert.Equal(original, store.CompleteSale(request));
        Assert.Equal(482_000_000, Assert.Single(store.ListProducts()).StockBase);
        Assert.Equal(2, store.PendingCount());
    }
    [Fact]
    public void ReusingOperationIdForDifferentSaleConflicts()
    {
        var request = Sale(Product().Id, 18); store.CompleteSale(request);
        Assert.Throws<OperationConflict>(() => store.CompleteSale(Sale(request.Lines[0].ProductId, 19, request.OperationId)));
        Assert.Equal(482_000_000, Assert.Single(store.ListProducts()).StockBase);
    }
    [Fact]
    public void PendingEventsSurviveProcessRecreation()
    {
        var request = Sale(Product().Id, 1); store.CompleteSale(request);
        var reopened = NewStore(); reopened.Initialize();
        Assert.Equal(2, reopened.PendingCount()); Assert.Equal(2, reopened.ReadPending(100).Count);
        Assert.Equal(store.CompleteSale(request), reopened.CompleteSale(request));
    }
    [Fact]
    public void InstallationCannotSilentlyChangeTenant()
    {
        var other = new SqlitePosStore(Path.Combine(directory, "local.db"), identity with { TenantId = Guid.NewGuid() });
        Assert.Throws<InvalidOperationException>(other.Initialize);
    }
    [Fact]
    public async Task ConcurrentSalesNeverOversell()
    {
        var id = Product(1).Id;
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            try { store.CompleteSale(Sale(id, 20)); return true; }
            catch (RuleViolation) { return false; }
        })));
        Assert.Equal(5, results.Count(success => success));
        Assert.Equal(0, Assert.Single(store.ListProducts()).StockBase);
        Assert.Equal(6, store.PendingCount());
    }
    [Fact]
    public async Task OfflineSalesAccumulateAndSynchronizeAfterRecovery()
    {
        var transport = new FakeTransport { Outcome = DeliveryOutcome.Unavailable };
        var sync = new SyncCoordinator(store, transport);
        var product = Product(); await sync.RunOnceAsync();
        Assert.Equal("Offline", sync.Status.State);
        store.CompleteSale(Sale(product.Id, 18)); Assert.Equal(2, store.PendingCount());
        transport.Outcome = DeliveryOutcome.Accepted; await sync.RunOnceAsync();
        Assert.Equal(0, store.PendingCount()); Assert.Equal("UpToDate", sync.Status.State);
        Assert.Equal(new long[] { 1, 2 }, transport.Delivered.Select(e => e.Sequence));
        Assert.NotNull(sync.Status.LastSuccessUtc);
    }
    [Fact]
    public async Task LostAcknowledgementIsSafeToRetry()
    {
        Product(); var inbox = new DevelopmentInbox(Path.Combine(directory, "receiver.db"), identity); inbox.Initialize();
        var transport = new InboxTransport(inbox); var sync = new SyncCoordinator(store, transport);
        await sync.RunOnceAsync(); Assert.Equal(1, store.PendingCount()); Assert.Equal(1, inbox.Count());
        await sync.RunOnceAsync(); Assert.Equal(0, store.PendingCount()); Assert.Equal(1, inbox.Count());
    }
    [Fact]
    public void InboxRejectsDifferentContentUnderSameId()
    {
        Product(); var inbox = new DevelopmentInbox(Path.Combine(directory, "receiver.db"), identity); inbox.Initialize();
        var envelope = Assert.Single(store.ReadPending(100)); inbox.Accept(envelope);
        Assert.Throws<OperationConflict>(() => inbox.Accept(envelope with { Payload = "{}" }));
        Assert.Equal(1, inbox.Count());
    }
    [Fact]
    public async Task UnconfiguredCloudDoesNotPretendToBeOnline()
    {
        Product(); var sync = new SyncCoordinator(store, new FakeTransport { IsConfigured = false });
        await sync.RunOnceAsync(); Assert.Equal("NotConfigured", sync.Status.State); Assert.Equal(1, store.PendingCount());
    }
    [Fact]
    public async Task AuthenticationFailurePreservesEventsAndRequiresAttention()
    {
        Product(); var sync = new SyncCoordinator(store, new FakeTransport { Outcome = DeliveryOutcome.Unauthorized });
        await sync.RunOnceAsync(); Assert.Equal("AttentionRequired", sync.Status.State); Assert.Equal(1, store.PendingCount());
    }
    [Fact]
    public async Task ConnectivityIsProbedEvenWithoutPendingEvents()
    {
        var sync = new SyncCoordinator(store, new FakeTransport { Outcome = DeliveryOutcome.Unavailable });
        await sync.RunOnceAsync(); Assert.Equal("Offline", sync.Status.State);
    }
    [Fact]
    public async Task WrongAcknowledgementDoesNotRemovePendingEvent()
    {
        Product(); using var client = new HttpClient(new WrongAckHandler());
        var transport = new HttpSyncTransport(client, new Uri("https://example.invalid/sync/events"), "test-token");
        var sync = new SyncCoordinator(store, transport); await sync.RunOnceAsync();
        Assert.Equal(1, store.PendingCount()); Assert.Equal("AttentionRequired", sync.Status.State);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private sealed class FakeTransport : ISyncTransport
    {
        public bool IsConfigured { get; set; } = true;
        public DeliveryOutcome Outcome { get; set; } = DeliveryOutcome.Accepted;
        public List<SyncEnvelope> Delivered { get; } = [];
        public Task<DeliveryOutcome> ProbeAsync(CancellationToken ct) => Task.FromResult(Outcome);
        public Task<DeliveryOutcome> SendAsync(SyncEnvelope envelope, CancellationToken ct)
        { if (Outcome == DeliveryOutcome.Accepted) Delivered.Add(envelope); return Task.FromResult(Outcome); }
    }
    private sealed class InboxTransport(DevelopmentInbox inbox) : ISyncTransport
    {
        private bool loseFirstAck = true;
        public bool IsConfigured => true;
        public Task<DeliveryOutcome> ProbeAsync(CancellationToken ct) => Task.FromResult(DeliveryOutcome.Accepted);
        public Task<DeliveryOutcome> SendAsync(SyncEnvelope envelope, CancellationToken ct)
        {
            inbox.Accept(envelope);
            if (loseFirstAck) { loseFirstAck = false; return Task.FromResult(DeliveryOutcome.Unavailable); }
            return Task.FromResult(DeliveryOutcome.Accepted);
        }
    }
    private sealed class WrongAckHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(new SyncAcknowledgement(Guid.NewGuid())) });
    }
}
