using Agropecuaria.Application;
using Agropecuaria.Cloud.Infrastructure;
using Agropecuaria.Contracts;
using Agropecuaria.Domain;
using Agropecuaria.Infrastructure;
using Xunit;

namespace Agropecuaria.Tests;

internal sealed class TestAccountProvider : ICloudAccountProvider
{
    public int Calls { get; private set; }
    public Dictionary<string, string> Accounts { get; } = [];
    public bool LoseFirstResponse { get; set; }
    public Action? AfterCreate { get; set; }
    public Task EnsureAsync(ProvisioningTicket ticket, string password, CancellationToken ct)
    {
        Calls++;
        if (Accounts.Any(a => a.Key != ticket.FirebaseUid && a.Value == ticket.Email)) throw new CloudAccountConflict();
        Accounts.TryAdd(ticket.FirebaseUid, ticket.Email);
        var action = AfterCreate; AfterCreate = null; action?.Invoke();
        if (LoseFirstResponse) { LoseFirstResponse = false; throw new CloudUnavailable(); }
        return Task.CompletedTask;
    }
}
public sealed class CloudAccountsTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), "agro-accounts-" + Guid.NewGuid().ToString("N"));
    private readonly CloudDirectory directory;
    private readonly Membership owner;
    private const string Password = "Temporary cloud account password 123!";
    public CloudAccountsTests()
    {
        Directory.CreateDirectory(path); directory = Open();
        owner = directory.Bootstrap(Guid.NewGuid(), Guid.NewGuid(), "owner", "owner@example.test", "Owner");
    }
    private CloudDirectory Open() { var value = new CloudDirectory(() => SqliteDatabase.Open(Path.Combine(path, "accounts.db")), false); value.Initialize(); return value; }
    private static CloudAccountActor Actor(Membership member) => new(member.TenantId, member.StoreId, member.Id, member.Uid);
    private static CreateUserRequest Request(Guid? operation = null) => new("cashier@example.test", "Cashier", "Cashier", Password, operation ?? Guid.NewGuid());
    [Fact]
    public async Task LostProviderResponseResumesAfterJournalRecreationWithoutDuplicateAccount()
    {
        var provider = new TestAccountProvider { LoseFirstResponse = true }; var request = Request();
        await Assert.ThrowsAsync<CloudUnavailable>(() => new CloudAccountsService(directory, provider).CreateAsync(Actor(owner), request, default));
        Assert.Single(directory.List(owner)); Assert.Single(provider.Accounts);
        var reopened = Open(); var service = new CloudAccountsService(reopened, provider);
        var user = await service.CreateAsync(Actor(owner), request, default);
        Assert.Equal(user, await service.CreateAsync(Actor(owner), request with { TemporaryPassword = "Another temporary password 456!" }, default));
        Assert.Equal(2, provider.Calls); Assert.Single(provider.Accounts);
        var audit = reopened.AccountAudit(owner); Assert.Single(audit, a => a.Action == "UserCreated" && a.SubjectId == user.Id);
        Assert.DoesNotContain(audit, a => a.DetailsJson.Contains(Password, StringComparison.Ordinal));
    }
    [Fact]
    public async Task ReusedOperationAndEmailConflictWithoutOverwritingIdentity()
    {
        var provider = new TestAccountProvider(); var service = new CloudAccountsService(directory, provider); var request = Request();
        await service.CreateAsync(Actor(owner), request, default);
        await Assert.ThrowsAsync<OperationConflict>(() => service.CreateAsync(Actor(owner), request with { Role = "Admin" }, default));
        await Assert.ThrowsAsync<OperationConflict>(() => service.CreateAsync(Actor(owner), request with { OperationId = Guid.NewGuid() }, default));
        Assert.Equal(1, provider.Calls);
        using var connection = SqliteDatabase.Open(Path.Combine(path, "accounts.db")); using var command = connection.CreateCommand();
        command.CommandText = "SELECT request_hash || result_json FROM cloud_user_provisioning";
        Assert.DoesNotContain(Password, (string)command.ExecuteScalar()!);
    }
    [Fact]
    public async Task AdminRevocationDuringProviderCallCannotGrantMembershipAndAnotherAdminCanResume()
    {
        var other = directory.Add(owner, "other-admin", "other@example.test", "Other", "Admin");
        directory.SetPasswordChange(owner, other.Id, false); other = directory.Get(other.TenantId, other.StoreId, other.Uid)!;
        var provider = new TestAccountProvider { AfterCreate = () => directory.Update(other, owner.Id, new("Viewer", true)) };
        var service = new CloudAccountsService(directory, provider); var request = Request();
        await Assert.ThrowsAsync<AccessDenied>(() => service.CreateAsync(Actor(owner), request, default));
        Assert.Null(directory.Get(other.TenantId, other.StoreId, provider.Accounts.Single().Key));
        var created = await service.CreateAsync(Actor(other), request, default);
        Assert.True(created.MustChangePassword); Assert.Single(provider.Accounts);
        Assert.Contains(directory.AccountAudit(other), a => a.SubjectId == created.Id && a.ActorUid == other.Uid && a.Action == "UserCreated");
    }
    [Fact]
    public async Task SameOperationAcrossBusinessesCannotAdoptAnotherIdentity()
    {
        var provider = new TestAccountProvider(); var service = new CloudAccountsService(directory, provider); var request = Request();
        await service.CreateAsync(Actor(owner), request, default);
        var other = directory.Bootstrap(Guid.NewGuid(), Guid.NewGuid(), "other", "other@example.test", "Other");
        await Assert.ThrowsAsync<OperationConflict>(() => service.CreateAsync(Actor(other), request, default));
        Assert.Single(directory.List(other)); Assert.Single(provider.Accounts);
        Assert.DoesNotContain(directory.AccountAudit(other), e => e.Action == "UserCreated");
        Assert.Contains(directory.AccountAudit(other), e => e.Action == "UserProvisioningConflict");
    }
    public void Dispose() => Directory.Delete(path, true);
}

[Trait("Category", "PostgreSQL")]
public sealed class PostgresAccountsTests
{
    [Fact]
    public async Task PostgreSqlJournalSurvivesUncertainProviderOutcomeAndAuditIsScoped()
    {
        using var database = new PostgresFixture();
        var directory = new CloudDirectory(() => database.Open(), true); directory.Initialize();
        var owner = directory.Bootstrap(Guid.NewGuid(), Guid.NewGuid(), "owner", "owner@example.test", "Owner");
        var actor = new CloudAccountActor(owner.TenantId, owner.StoreId, owner.Id, owner.Uid);
        var provider = new TestAccountProvider { LoseFirstResponse = true };
        var request = new CreateUserRequest("cashier@example.test", "Cashier", "Cashier", "Temporary cloud password 123!", Guid.NewGuid());
        await Assert.ThrowsAsync<CloudUnavailable>(() => new CloudAccountsService(directory, provider).CreateAsync(actor, request, default));
        Assert.Equal(1, database.Scalar<long>("SELECT COUNT(*) FROM cloud_user_provisioning WHERE state='Pending'"));
        var reopened = new CloudDirectory(() => database.Open(), true);
        var user = await new CloudAccountsService(reopened, provider).CreateAsync(actor, request, default);
        Assert.Single(provider.Accounts);
        Assert.Equal(1, database.Scalar<long>("SELECT COUNT(*) FROM cloud_user_provisioning WHERE state='Completed'"));
        Assert.Single(reopened.AccountAudit(owner), e => e.SubjectId == user.Id && e.Action == "UserCreated");
        var other = directory.Bootstrap(Guid.NewGuid(), Guid.NewGuid(), "other", "other@example.test", "Other");
        Assert.DoesNotContain(reopened.AccountAudit(other), e => e.SubjectId == user.Id);
    }
}
