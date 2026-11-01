using Agropecuaria.Contracts;

namespace Agropecuaria.Application;

public sealed class CloudUnavailable(string message = "Se requiere conexión con el servicio de identidad.") : Exception(message);
public sealed class CloudIdentityRejected() : Exception("No se pudo validar la cuenta o sus permisos en la nube.");
public sealed record FirebaseCredentials(string IdToken, string RefreshToken, string Uid);
public interface IFirebaseSignIn
{
    Task<FirebaseCredentials> SignInAsync(string email, string password, CancellationToken ct);
    Task<FirebaseCredentials> RefreshAsync(string refreshToken, CancellationToken ct);
}
public interface ICloudAuthority
{
    Task<IReadOnlyList<StoreAccess>> DiscoverAsync(string idToken, CancellationToken ct);
    Task<CloudGrant> AuthorizeAsync(string idToken, CloudAuthorizationRequest request, CancellationToken ct);
    Task<IReadOnlyList<UserView>> ListUsersAsync(string idToken, Guid tenantId, Guid storeId, CancellationToken ct);
    Task<UserView> CreateUserAsync(string idToken, Guid tenantId, Guid storeId, CreateUserRequest request, CancellationToken ct);
    Task<UserView> UpdateUserAsync(string idToken, Guid tenantId, Guid storeId, Guid userId, UpdateUserRequest request, CancellationToken ct);
    Task ResetPasswordAsync(string idToken, Guid tenantId, Guid storeId, Guid userId, string password, CancellationToken ct);
    Task ChangePasswordAsync(string idToken, Guid tenantId, Guid storeId, string password, CancellationToken ct);
}
public sealed record CachedCloudSession(string TokenHash, string Uid, string RefreshToken);
public interface ICloudSessionCache
{
    LoginResult BeginCloudSession(CloudGrant grant, string refreshToken);
    OperatorIdentity? AuthenticateCloudSession(string token);
    CachedCloudSession? GetCloudSession(string token);
    IReadOnlyList<CachedCloudSession> ActiveCloudSessions();
    void RenewCloudSession(CachedCloudSession session, CloudGrant grant, string refreshToken);
    void RevokeCloudUser(Guid userId);
    void RevokeCloudSession(CachedCloudSession session);
}
