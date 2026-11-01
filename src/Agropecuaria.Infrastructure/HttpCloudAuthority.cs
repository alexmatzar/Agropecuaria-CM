using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agropecuaria.Application;
using Agropecuaria.Contracts;

namespace Agropecuaria.Infrastructure;

public sealed class HttpCloudAuthority(HttpClient http, Uri? endpoint) : ICloudAuthority, IDeviceAuthority, ICloudAuditAuthority
{
    public async Task<IReadOnlyList<CloudAuditView>> AuditAsync(string token, Guid tenant, Guid store, CancellationToken ct) =>
        await Send<List<CloudAuditView>>(token, HttpMethod.Get, $"v1/tenants/{tenant}/stores/{store}/audit", null, ct);
    public Task<RegisteredDevice> RegisterDeviceAsync(string token, Guid tenant, Guid store, RegisterDeviceRequest request, CancellationToken ct) =>
        Send<RegisteredDevice>(token, HttpMethod.Post, $"v1/tenants/{tenant}/stores/{store}/devices", request, ct);
    public async Task<IReadOnlyList<StoreAccess>> DiscoverAsync(string token, CancellationToken ct) =>
        await Send<List<StoreAccess>>(token, HttpMethod.Get, "v1/auth/stores", null, ct);
    public Task<CloudGrant> AuthorizeAsync(string token, CloudAuthorizationRequest request, CancellationToken ct) =>
        Send<CloudGrant>(token, HttpMethod.Post, "v1/auth/authorize", request, ct);
    private static string Scope(Guid tenant, Guid store) => $"v1/tenants/{tenant}/stores/{store}/users";
    public async Task<IReadOnlyList<UserView>> ListUsersAsync(string token, Guid tenant, Guid store, CancellationToken ct) =>
        await Send<List<UserView>>(token, HttpMethod.Get, Scope(tenant, store), null, ct);
    public Task<UserView> CreateUserAsync(string token, Guid tenant, Guid store, CreateUserRequest request, CancellationToken ct) =>
        Send<UserView>(token, HttpMethod.Post, Scope(tenant, store), request, ct);
    public Task<UserView> UpdateUserAsync(string token, Guid tenant, Guid store, Guid id, UpdateUserRequest request, CancellationToken ct) =>
        Send<UserView>(token, HttpMethod.Patch, $"{Scope(tenant, store)}/{id}", request, ct);
    public async Task ResetPasswordAsync(string token, Guid tenant, Guid store, Guid id, string password, CancellationToken ct) =>
        await Send<object>(token, HttpMethod.Post, $"{Scope(tenant, store)}/{id}/reset-password", new ResetPasswordRequest(password), ct);
    public async Task ChangePasswordAsync(string token, Guid tenant, Guid store, string password, CancellationToken ct) =>
        await Send<object>(token, HttpMethod.Post, $"{Scope(tenant, store)}/me/password", new ResetPasswordRequest(password), ct);

    private async Task<T> Send<T>(string token, HttpMethod method, string path, object? value, CancellationToken ct)
    {
        if (endpoint == null) throw new CloudUnavailable("La API de nube aún no está configurada.");
        try
        {
            using var request = new HttpRequestMessage(method, new Uri(endpoint, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (value != null) request.Content = JsonContent.Create(value);
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new CloudIdentityRejected();
            if (response.StatusCode == HttpStatusCode.Conflict) throw new Agropecuaria.Domain.OperationConflict("La operación de usuarios entra en conflicto con el estado de nube.");
            if (response.StatusCode == HttpStatusCode.BadRequest) throw new Agropecuaria.Domain.RuleViolation("Solicitud de usuarios inválida.");
            if (!response.IsSuccessStatusCode) throw new CloudUnavailable();
            return await response.Content.ReadFromJsonAsync<T>(ct) ?? throw new CloudUnavailable();
        }
        catch (HttpRequestException) { throw new CloudUnavailable(); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new CloudUnavailable(); }
    }
}
