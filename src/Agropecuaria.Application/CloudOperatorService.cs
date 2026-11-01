using Agropecuaria.Contracts;
using Agropecuaria.Domain;

namespace Agropecuaria.Application;

public sealed class CloudOperatorService(IFirebaseSignIn firebase, ICloudAuthority authority,
    ICloudSessionCache cache, IOperatorAccounts legacyStore, InstallationIdentity installation)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private CloudAuthorizationRequest Request => new(installation.TenantId, installation.StoreId, installation.DeviceId);
    public OperatorIdentity? Authenticate(string token) => cache.AuthenticateCloudSession(token);
    public void Logout(string token) => legacyStore.Logout(token);
    public async Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var credentials = await firebase.SignInAsync(request.Username, request.Password, ct);
            var grant = await authority.AuthorizeAsync(credentials.IdToken, Request, ct);
            if (credentials.Uid != grant.FirebaseUid) throw new CloudIdentityRejected();
            return cache.BeginCloudSession(grant, credentials.RefreshToken);
        }
        finally { gate.Release(); }
    }
    public async Task RevalidateAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            foreach (var session in cache.ActiveCloudSessions())
            {
                try { await FreshCredentials(session, ct); }
                catch (CloudUnavailable) { /* Keep only the already granted offline lease. Never extend it. */ }
                catch (CloudIdentityRejected) { cache.RevokeCloudSession(session); }
            }
        }
        finally { gate.Release(); }
    }
    public Task<IReadOnlyList<UserView>> ListUsersAsync(string token, CancellationToken ct) => WithSession(token,
        credentials => authority.ListUsersAsync(credentials.IdToken, installation.TenantId, installation.StoreId, ct), ct);
    public Task<RegisteredDevice> RegisterDeviceAsync(string token, RegisterDeviceRequest request, CancellationToken ct) => WithSession(token,
        credentials => authority is IDeviceAuthority devices
            ? devices.RegisterDeviceAsync(credentials.IdToken, installation.TenantId, installation.StoreId, request, ct)
            : throw new CloudUnavailable("La autoridad no admite registro de dispositivos."), ct);
    public Task<IReadOnlyList<CloudAuditView>> AuditAsync(string token, CancellationToken ct) => WithSession(token,
        credentials => authority is ICloudAuditAuthority audit
            ? audit.AuditAsync(credentials.IdToken, installation.TenantId, installation.StoreId, ct)
            : throw new CloudUnavailable("La auditoría central no está disponible."), ct);
    public Task<object> RevalidateSessionAsync(string token, CancellationToken ct) =>
        WithSession<object>(token, _ => Task.FromResult<object>(new { Validated = true }), ct);
    public Task<UserView> CreateUserAsync(string token, CreateUserRequest request, CancellationToken ct) => WithSession(token,
        credentials => authority.CreateUserAsync(credentials.IdToken, installation.TenantId, installation.StoreId, request, ct), ct);
    public Task<UserView> UpdateUserAsync(string token, Guid id, UpdateUserRequest request, CancellationToken ct) => WithSession(token, async credentials =>
    {
        var result = await authority.UpdateUserAsync(credentials.IdToken, installation.TenantId, installation.StoreId, id, request, ct);
        cache.RevokeCloudUser(id); return result;
    }, ct);
    public Task<object> ResetPasswordAsync(string token, Guid id, string password, CancellationToken ct) => WithSession<object>(token, async credentials =>
    {
        await authority.ResetPasswordAsync(credentials.IdToken, installation.TenantId, installation.StoreId, id, password, ct);
        cache.RevokeCloudUser(id); return new { Updated = true };
    }, ct);
    public Task<object> ChangePasswordAsync(string token, ChangePasswordRequest request, CancellationToken ct) => WithSession<object>(token, async credentials =>
    {
        var actor = cache.AuthenticateCloudSession(token) ?? throw new CloudIdentityRejected();
        var reauthenticated = await firebase.SignInAsync(actor.Username, request.CurrentPassword, ct);
        if (reauthenticated.Uid != credentials.Uid) throw new CloudIdentityRejected();
        await authority.ChangePasswordAsync(reauthenticated.IdToken, installation.TenantId, installation.StoreId, request.NewPassword, ct);
        cache.RevokeCloudUser(actor.Id); return new { Updated = true };
    }, ct);
    private async Task<FirebaseCredentials> FreshCredentials(CachedCloudSession session, CancellationToken ct)
    {
        var credentials = await firebase.RefreshAsync(session.RefreshToken, ct);
        var grant = await authority.AuthorizeAsync(credentials.IdToken, Request, ct);
        if (credentials.Uid != session.Uid || grant.FirebaseUid != session.Uid) throw new CloudIdentityRejected();
        cache.RenewCloudSession(session, grant, credentials.RefreshToken);
        return credentials;
    }
    private async Task<T> WithSession<T>(string token, Func<FirebaseCredentials, Task<T>> action, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var session = cache.GetCloudSession(token) ?? throw new CloudIdentityRejected();
            try { return await action(await FreshCredentials(session, ct)); }
            catch (CloudIdentityRejected) { cache.RevokeCloudSession(session); throw; }
        }
        finally { gate.Release(); }
    }
}
