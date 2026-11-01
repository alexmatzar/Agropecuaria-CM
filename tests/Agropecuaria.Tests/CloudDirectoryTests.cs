using Agropecuaria.Cloud.Infrastructure;
using Agropecuaria.Contracts;
using Agropecuaria.Domain;
using Agropecuaria.Infrastructure;
using Xunit;

namespace Agropecuaria.Tests;

public sealed class CloudDirectoryTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), "agropecuaria-memberships-" + Guid.NewGuid().ToString("N"));
    private readonly CloudDirectory directory;
    private readonly Membership owner;
    public CloudDirectoryTests()
    {
        Directory.CreateDirectory(path);
        directory = new(() => SqliteDatabase.Open(Path.Combine(path, "cloud.db")), false); directory.Initialize();
        owner = directory.Bootstrap(Guid.NewGuid(), Guid.NewGuid(), "firebase-owner", "owner@example.test", "Owner");
    }
    [Fact]
    public void FirebaseUidAloneDoesNotGrantAccessToAnotherBusiness()
    {
        Assert.NotNull(directory.Get(owner.TenantId, owner.StoreId, owner.Uid));
        Assert.Null(directory.Get(Guid.NewGuid(), owner.StoreId, owner.Uid));
        Assert.Null(directory.Get(owner.TenantId, Guid.NewGuid(), owner.Uid));
    }
    [Fact]
    public void DiscoveryReturnsOnlyActiveMembershipsForAuthenticatedUid()
    {
        directory.Bootstrap(Guid.NewGuid(), Guid.NewGuid(), "other-owner", "other@example.test", "Other");
        var viewer = directory.Add(owner, "viewer", "viewer@example.test", "Viewer", "Viewer");
        Assert.Equal(new StoreAccess(owner.TenantId, owner.StoreId, "Admin"), Assert.Single(directory.Discover(owner.Uid)));
        Assert.Single(directory.Discover(viewer.Uid));
        directory.Update(owner, viewer.Id, new("Viewer", false));
        Assert.Empty(directory.Discover(viewer.Uid));
        Assert.Empty(directory.Discover("unknown"));
    }
    [Fact]
    public void LastAdministratorCannotBeDisabledOrDemoted()
    {
        Assert.Throws<OperationConflict>(() => directory.Update(owner, owner.Id, new("Viewer", true)));
        Assert.Throws<OperationConflict>(() => directory.Update(owner, owner.Id, new("Admin", false)));
        Assert.Throws<OperationConflict>(() => directory.Bootstrap(owner.TenantId, owner.StoreId, "other", "other@example.test", "Other"));
    }
    [Fact]
    public void RoleAndStatusArePersistedAndVersionedInAuthority()
    {
        var cashier = directory.Add(owner, "cashier", "cashier@example.test", "Cashier", "Cashier");
        var changed = directory.Update(owner, cashier.Id, new("Viewer", false));
        Assert.Equal(2, changed.Version); Assert.False(changed.Active);
        var reopened = new CloudDirectory(() => SqliteDatabase.Open(Path.Combine(path, "cloud.db")), false);
        Assert.Equal("Viewer", reopened.Get(owner.TenantId, owner.StoreId, "cashier")!.Role);
    }
    [Fact]
    public void MembershipIdFromAnotherTenantCannotBeModified()
    {
        var other = directory.Bootstrap(Guid.NewGuid(), Guid.NewGuid(), "other-owner", "other@example.test", "Other");
        Assert.Throws<AccessDenied>(() => directory.Update(owner, other.Id, new("Viewer", false)));
        Assert.True(directory.Get(other.TenantId, other.StoreId, other.Uid)!.Active);
    }
    [Fact]
    public void CashierAndStaleAdminCannotAdministerUsers()
    {
        var cashier = directory.Add(owner, "cashier", "cashier@example.test", "Cashier", "Cashier");
        Assert.Throws<AccessDenied>(() => directory.Add(cashier, "attacker", "attacker@example.test", "Attacker", "Admin"));
        var second = directory.Add(owner, "second-admin", "second@example.test", "Second", "Admin");
        directory.SetPasswordChange(owner, second.Id, false);
        directory.Update(owner, owner.Id, new("Viewer", true));
        Assert.Throws<AccessDenied>(() => directory.List(owner));
    }
    public void Dispose() => Directory.Delete(path, true);
}
