using System.Security.Cryptography;
using Agropecuaria.Application;
using Agropecuaria.Contracts;
using Agropecuaria.Domain;
using Agropecuaria.Infrastructure;
using Xunit;

namespace Agropecuaria.Tests;

public sealed class CloudIdentityTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), "agropecuaria-cloud-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    private readonly InstallationIdentity installation = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "GTQ");
    private readonly Clock clock = new();
    private readonly FakeFirebase firebase = new();
    private readonly FakeAuthority authority;
    private readonly SqlitePosStore cache;
    private readonly CloudOperatorService service;
    public CloudIdentityTests()
    {
        cache = new SqlitePosStore(Path.Combine(path, "secure.db"), installation, key, clock); cache.Initialize();
        authority = new FakeAuthority(new(installation.TenantId, installation.StoreId, "uid-one",
            new(Guid.NewGuid(), "owner@example.test", "Owner", "Admin", true, false), 1, clock.GetUtcNow(), clock.GetUtcNow().AddHours(24)));
        service = new(firebase, authority, cache, cache, installation);
    }
    [Fact]
    public async Task LoginAlwaysRequiresOnlineIdentityAndNeverFallsBackToStoredPasswords()
    {
        cache.BootstrapAdministrator("owner", "Local owner", "Local password for tests 123!");
        firebase.Available = false;
        await Assert.ThrowsAsync<CloudUnavailable>(() => service.LoginAsync(new("owner", "Local password for tests 123!"), default));
        Assert.Empty(cache.ActiveCloudSessions());
    }
    [Fact]
    public async Task GrantedSessionContinuesOfflineButCannotBeExtendedByNetworkFailure()
    {
        var login = await service.LoginAsync(new("owner@example.test", "password"), default);
        Assert.NotNull(service.Authenticate(login.AccessToken));
        firebase.Available = false; clock.Advance(TimeSpan.FromHours(23)); await service.RevalidateAsync(default);
        Assert.NotNull(service.Authenticate(login.AccessToken));
        clock.Advance(TimeSpan.FromHours(2)); Assert.Null(service.Authenticate(login.AccessToken));
    }
    [Fact]
    public async Task FailedAccountSwitchPreservesSessionAndSuccessfulSwitchRevokesPreviousToken()
    {
        var first = await service.LoginAsync(new("owner@example.test", "password"), default);
        firebase.Available = false;
        await Assert.ThrowsAsync<CloudUnavailable>(() => service.LoginAsync(new("other@example.test", "password"), default));
        Assert.NotNull(service.Authenticate(first.AccessToken));
        firebase.Available = true;
        var second = await service.LoginAsync(new("owner@example.test", "password"), default);
        Assert.Null(service.Authenticate(first.AccessToken)); Assert.NotNull(service.Authenticate(second.AccessToken));
    }
    [Fact]
    public async Task CloudRevocationRemovesCachedSessionOnReconnection()
    {
        var login = await service.LoginAsync(new("owner@example.test", "password"), default);
        authority.Rejected = true; await service.RevalidateAsync(default); Assert.Null(service.Authenticate(login.AccessToken));
    }
    [Fact]
    public async Task CurrentCloudRoleReplacesOldCachedRole()
    {
        var login = await service.LoginAsync(new("owner@example.test", "password"), default);
        authority.Grant = authority.Grant with { User = authority.Grant.User with { Role = "Viewer" }, MembershipVersion = 2 };
        await service.RevalidateAsync(default); Assert.Equal(OperatorRole.Viewer, service.Authenticate(login.AccessToken)!.Role);
    }
    [Fact]
    public async Task LocalPasswordsAreNotDownloadedFromFirebaseAndScopeCannotBeSpoofed()
    {
        var login = await service.LoginAsync(new("owner@example.test", "password"), default);
        using var connection = SqliteDatabase.Open(Path.Combine(path, "secure.db"), key);
        using var command = connection.CreateCommand(); command.CommandText = "SELECT password_hash FROM operators WHERE firebase_uid='uid-one'";
        Assert.Equal("", command.ExecuteScalar());
        authority.Grant = authority.Grant with { TenantId = Guid.NewGuid() };
        await Assert.ThrowsAsync<CloudIdentityRejected>(() => service.LoginAsync(new("owner@example.test", "password"), default));
        Assert.NotNull(service.Authenticate(login.AccessToken));
    }
    [Fact]
    public async Task FreshInstallationRestoresSameUserIdentityAndRoleFromAuthority()
    {
        var first = await service.LoginAsync(new("owner@example.test", "password"), default);
        var newKey = RandomNumberGenerator.GetBytes(32);
        var fresh = new SqlitePosStore(Path.Combine(path, "fresh.db"), installation with { DeviceId = Guid.NewGuid() }, newKey, clock); fresh.Initialize();
        var replacement = new CloudOperatorService(firebase, authority, fresh, fresh, installation);
        var recovered = await replacement.LoginAsync(new("owner@example.test", "password"), default);
        Assert.Equal(first.User.Id, recovered.User.Id); Assert.Equal(first.User.Role, recovered.User.Role);
        CryptographicOperations.ZeroMemory(newKey);
    }
    [Fact]
    public async Task LogoutWorksOfflineAndCachedUserCannotBeUsedForOfflineLogin()
    {
        var login = await service.LoginAsync(new("owner@example.test", "password"), default); firebase.Available = false;
        service.Logout(login.AccessToken); Assert.Null(service.Authenticate(login.AccessToken));
        await Assert.ThrowsAsync<CloudUnavailable>(() => service.LoginAsync(new("owner@example.test", "password"), default));
    }
    [Fact]
    public async Task UserAdministrationRequiresConnectivityEvenForCachedAdmin()
    {
        var login = await service.LoginAsync(new("owner@example.test", "password"), default); firebase.Available = false;
        await Assert.ThrowsAsync<CloudUnavailable>(() => service.CreateUserAsync(login.AccessToken, new("other@example.test", "Other", "Cashier", "long enough password"), default));
        Assert.NotNull(service.Authenticate(login.AccessToken));
    }
    public void Dispose() { CryptographicOperations.ZeroMemory(key); Directory.Delete(path, true); }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan delta) => now += delta;
    }
    private sealed class FakeFirebase : IFirebaseSignIn
    {
        public bool Available { get; set; } = true;
        public Task<FirebaseCredentials> SignInAsync(string email, string password, CancellationToken ct) => Available
            ? Task.FromResult(new FirebaseCredentials("firebase-id-token", "refresh-token", "uid-one")) : throw new CloudUnavailable();
        public Task<FirebaseCredentials> RefreshAsync(string refresh, CancellationToken ct) => SignInAsync("", "", ct);
    }
    private sealed class FakeAuthority(CloudGrant grant) : ICloudAuthority
    {
        public CloudGrant Grant { get; set; } = grant;
        public bool Rejected { get; set; }
        public Task<IReadOnlyList<StoreAccess>> DiscoverAsync(string token, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<StoreAccess>>([new(Grant.TenantId, Grant.StoreId, Grant.User.Role)]);
        public Task<CloudGrant> AuthorizeAsync(string token, CloudAuthorizationRequest request, CancellationToken ct) => Rejected ? throw new CloudIdentityRejected() : Task.FromResult(Grant);
        public Task<IReadOnlyList<UserView>> ListUsersAsync(string token, Guid tenant, Guid store, CancellationToken ct) => Task.FromResult<IReadOnlyList<UserView>>([Grant.User]);
        public Task<UserView> CreateUserAsync(string token, Guid tenant, Guid store, CreateUserRequest request, CancellationToken ct) => throw new NotImplementedException();
        public Task<UserView> UpdateUserAsync(string token, Guid tenant, Guid store, Guid id, UpdateUserRequest request, CancellationToken ct) => throw new NotImplementedException();
        public Task ResetPasswordAsync(string token, Guid tenant, Guid store, Guid id, string password, CancellationToken ct) => throw new NotImplementedException();
        public Task ChangePasswordAsync(string token, Guid tenant, Guid store, string password, CancellationToken ct) => Task.CompletedTask;
    }
}
