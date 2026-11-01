using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agropecuaria.Application;

namespace Agropecuaria.Infrastructure;

// Runs on the POS. It needs a Firebase client API key, never an Admin SDK credential.
public sealed class FirebaseSignIn(HttpClient http, string apiKey, Uri? emulator = null) : IFirebaseSignIn
{
    public async Task<FirebaseCredentials> SignInAsync(string email, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email) || password is null || password.Length > 128) throw new CloudIdentityRejected();
        return await Send(new HttpRequestMessage(HttpMethod.Post, Endpoint("identitytoolkit", $"v1/accounts:signInWithPassword?key={Uri.EscapeDataString(apiKey)}"))
        { Content = JsonContent.Create(new { email, password, returnSecureToken = true }) }, false, ct);
    }
    public Task<FirebaseCredentials> RefreshAsync(string refreshToken, CancellationToken ct) =>
        Send(new HttpRequestMessage(HttpMethod.Post, Endpoint("securetoken", $"v1/token?key={Uri.EscapeDataString(apiKey)}"))
        { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken }) }, true, ct);

    private Uri Endpoint(string service, string path) => emulator == null
        ? new Uri($"https://{service}.googleapis.com/{path}")
        : new Uri(emulator, $"{service}.googleapis.com/{path}");
    private async Task<FirebaseCredentials> Send(HttpRequestMessage request, bool refresh, CancellationToken ct)
    {
        using (request)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new CloudUnavailable("Firebase aún no está configurado.");
            try
            {
                using var response = await http.SendAsync(request, ct);
                if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) throw new CloudUnavailable();
                if (!response.IsSuccessStatusCode) throw new CloudIdentityRejected();
                using var json = await response.Content.ReadFromJsonAsync<JsonDocument>(ct) ?? throw new CloudUnavailable();
                var root = json.RootElement;
                return new(root.GetProperty(refresh ? "id_token" : "idToken").GetString()!,
                    root.GetProperty(refresh ? "refresh_token" : "refreshToken").GetString()!,
                    root.GetProperty(refresh ? "user_id" : "localId").GetString()!);
            }
            catch (HttpRequestException) { throw new CloudUnavailable(); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new CloudUnavailable(); }
        }
    }
}
