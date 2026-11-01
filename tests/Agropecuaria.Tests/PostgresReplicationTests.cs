using System.Security.Cryptography;
using System.Text.Json;
using Agropecuaria.Application;
using Agropecuaria.Cloud.Infrastructure;
using Agropecuaria.Contracts;
using Agropecuaria.Domain;
using Agropecuaria.Infrastructure;
using Npgsql;
using Xunit;

namespace Agropecuaria.Tests;

[Trait("Category", "PostgreSQL")]
public sealed class PostgresReplicationTests : IDisposable
{
    private readonly PostgresFixture database = new();
    private readonly string path = Path.Combine(Path.GetTempPath(), "agro-pg-" + Guid.NewGuid().ToString("N"));
    private readonly CloudDirectory directory;
    private readonly CloudReplication cloud;
    private readonly Membership owner;
    private readonly InstallationIdentity identity;
    private readonly SqlitePosStore source;
    public PostgresReplicationTests()
    {
        directory = new(() => database.Open(), true); directory.Initialize();
        owner = directory.Bootstrap(Guid.NewGuid(), Guid.NewGuid(), "owner-uid", "owner@example.test", "Owner");
        identity = new(owner.TenantId, owner.StoreId, Guid.NewGuid(), "GTQ");
        cloud = new(database.Open);
        source = Local("source", identity);
    }
    private SqlitePosStore Local(string name, InstallationIdentity id)
    {
        var store = new SqlitePosStore(Path.Combine(path, name + ".db"), id, RandomNumberGenerator.GetBytes(32)); store.Initialize(); return store;
    }
    private RegisteredDevice Register(Guid? id = null, bool writer = true, bool replace = false, bool data = false) =>
        cloud.Register(owner, new(id ?? identity.DeviceId, writer, replace, data, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
    private DeviceIdentity Actor(RegisteredDevice device) => cloud.Authenticate(owner.TenantId, owner.StoreId, device.DeviceId, device.Secret)!;
    private List<SyncEnvelope> Events()
    {
        var product = source.CreateProduct(new(Guid.NewGuid(), Guid.NewGuid(), "Concentrado", "Weight", "Quintal", 5,
            [new("Pound", 500)], new("001234567890", "Alimento", "Marca", "Descripción")));
        source.CompleteSale(new(Guid.NewGuid(), [new(product.Id, "Pound", 18)]));
        return source.ReadPending(100).ToList();
    }
    [Fact]
    public void MigrationsAreTransactionalRepeatableAndDetectAlteredHistory()
    {
        directory.Initialize(); Assert.Equal(3, database.Scalar<long>("SELECT COUNT(*) FROM cloud_schema_migrations"));
        using (var connection = database.Open())
        using (var command = new NpgsqlCommand("UPDATE cloud_schema_migrations SET sha256='altered' WHERE version=1", connection)) command.ExecuteNonQuery();
        Assert.Throws<InvalidOperationException>(directory.Initialize);
    }
    [Fact]
    public async Task ConcurrentDuplicatesAndLostReceiptsAreIdempotent()
    {
        var actor = Actor(Register()); var data = Events();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => cloud.Accept(actor, data[0]))));
        cloud.Accept(actor, data[1]); cloud.Accept(actor, data[1]);
        Assert.Equal(2, database.Scalar<long>("SELECT COUNT(*) FROM cloud_events"));
        Assert.Equal(1, database.Scalar<long>("SELECT COUNT(*) FROM cloud_sales"));
        Assert.Equal(482_000_000, database.Scalar<long>("SELECT stock_base FROM cloud_products"));
        Assert.Equal(9_000, database.Scalar<long>("SELECT total_minor FROM cloud_sales"));
        Assert.Throws<OperationConflict>(() => cloud.Accept(actor, data[1] with { Payload = "{}" }));
    }
    [Fact]
    public void FailedProjectionNeverConsumesEventSequenceOrCursor()
    {
        var actor = Actor(Register()); var data = Events(); cloud.Accept(actor, data[0]);
        var payload = BusinessEvents.Payload<SaleCompletedPayload>(data[1]);
        var invalid = data[1] with { Payload = JsonSerializer.Serialize(payload with { Sale = payload.Sale with { TotalMinor = 1 } }) };
        Assert.Throws<RuleViolation>(() => cloud.Accept(actor, invalid));
        Assert.Equal(1, database.Scalar<long>("SELECT head_cursor FROM cloud_replication"));
        Assert.Equal(0, database.Scalar<long>("SELECT COUNT(*) FROM cloud_sales"));
        Assert.Equal(500_000_000, database.Scalar<long>("SELECT stock_base FROM cloud_products"));
        cloud.Accept(actor, data[1]); Assert.Equal(2, database.Scalar<long>("SELECT head_cursor FROM cloud_replication"));
    }
    [Fact]
    public void PullRecoversEncryptedNewInstallationWithoutEchoOrRepeatedStockChanges()
    {
        var actor = Actor(Register()); foreach (var item in Events()) cloud.Accept(actor, item);
        var next = identity with { DeviceId = Guid.NewGuid() };
        var registration = Register(next.DeviceId, writer: false);
        var target = Local("target", next); target.EnrollDevice(registration);
        var page = cloud.Pull(Actor(registration), 0, 100); target.ApplyPage(page); target.ApplyPage(page);
        Assert.Equal(482_000_000, Assert.Single(target.ListProducts()).StockBase);
        Assert.Equal(9_000, Assert.Single(target.ListSales()).TotalMinor); Assert.Equal(0, target.PendingCount());
        Assert.Equal(2, target.CloudCursor()); Assert.Equal("001234567890", Assert.Single(target.ListProducts()).Details!.Barcode);
    }
    [Fact]
    public void DeviceSecretsCannotAuthorizeOtherTenantsAndRevocationIsEnforced()
    {
        var registration = Register(); var actor = Actor(registration);
        Assert.Null(cloud.Authenticate(Guid.NewGuid(), owner.StoreId, identity.DeviceId, registration.Secret));
        Assert.Null(cloud.Authenticate(owner.TenantId, Guid.NewGuid(), identity.DeviceId, registration.Secret));
        Assert.Null(cloud.Authenticate(owner.TenantId, owner.StoreId, identity.DeviceId, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        Assert.Throws<AccessDenied>(() => cloud.Accept(actor, Events()[0] with { TenantId = Guid.NewGuid() }));
        cloud.Revoke(owner, identity.DeviceId); Assert.Null(Actor(registration));
        Assert.Throws<AccessDenied>(() => cloud.Pull(actor, 0, 100));
    }
    [Fact]
    public void OnlyOneWriterIsAllowedAndReplacementMustBeExplicit()
    {
        var old = Register(); var data = Events(); cloud.Accept(Actor(old), data[0]);
        Assert.Throws<OperationConflict>(() => Register(Guid.NewGuid()));
        Assert.Throws<OperationConflict>(() => Register(Guid.NewGuid(), replace: true, data: true));
        var next = Register(Guid.NewGuid(), replace: true);
        Assert.Null(Actor(old)); Assert.NotNull(Actor(next));
        var replica = Register(Guid.NewGuid(), writer: false);
        Assert.Throws<AccessDenied>(() => cloud.Accept(Actor(replica), data[1] with { DeviceId = replica.DeviceId }));
        Assert.Single(cloud.Pull(Actor(next), 0, 100).Changes);
    }
    [Fact]
    public void EnrollmentRetryUsesSameSecretAndCannotReanimateRevokedDevice()
    {
        var request = new RegisterDeviceRequest(identity.DeviceId, true, false, false, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var device = cloud.Register(owner, request); Assert.Equal(device, cloud.Register(owner, request));
        Assert.Throws<OperationConflict>(() => cloud.Register(owner, request with { Secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }));
        cloud.Revoke(owner, identity.DeviceId); Assert.Throws<OperationConflict>(() => cloud.Register(owner, request));
    }
    [Fact]
    public async Task LastAdministratorRemainsProtectedUnderConcurrentDemotions()
    {
        var other = directory.Add(owner, "second-uid", "second@example.test", "Second", "Admin");
        directory.SetPasswordChange(owner, other.Id, false); other = directory.Get(owner.TenantId, owner.StoreId, other.Uid)!;
        var results = await Task.WhenAll(new[] { owner, other }.Select(actor => Task.Run(() =>
        {
            try { directory.Update(actor, actor.Id, new("Viewer", true)); return true; }
            catch (OperationConflict) { return false; }
        })));
        Assert.Single(results, v => v);
        Assert.Equal(1, database.Scalar<long>("SELECT COUNT(*) FROM cloud_memberships WHERE active=1 AND role='Admin'"));
    }
    [Fact]
    public void HistoricalArchiveReplicatesWithoutChangingOperationalSalesOrStock()
    {
        var actor = Actor(Register()); var data = Events(); foreach (var item in data) cloud.Accept(actor, item);
        source.ArchiveLegacyRecord(Guid.NewGuid(), new(new string('A', 64), "ventas", "1", "{\"total\":\"90\",\"ventashechas\":\"Texto original\"}"));
        cloud.Accept(actor, source.ReadPending(100)[2]);
        Assert.Equal(1, database.Scalar<long>("SELECT COUNT(*) FROM cloud_legacy_records"));
        Assert.Equal(1, database.Scalar<long>("SELECT COUNT(*) FROM cloud_sales"));
        Assert.Equal(482_000_000, database.Scalar<long>("SELECT stock_base FROM cloud_products"));
        var id = identity with { DeviceId = Guid.NewGuid() }; var registration = Register(id.DeviceId, writer: false);
        var restored = Local("history", id); restored.EnrollDevice(registration); restored.ApplyPage(cloud.Pull(Actor(registration), 0, 100));
        Assert.Single(restored.ListSales()); Assert.Equal(482_000_000, Assert.Single(restored.ListProducts()).StockBase);
    }
    public void Dispose() { database.Dispose(); if (Directory.Exists(path)) Directory.Delete(path, true); }
}
