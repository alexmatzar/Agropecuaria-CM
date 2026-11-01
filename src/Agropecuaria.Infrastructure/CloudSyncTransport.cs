using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agropecuaria.Application;
using Agropecuaria.Contracts;

namespace Agropecuaria.Infrastructure;

public sealed class CloudSyncTransport(HttpClient client, Uri? authority, InstallationIdentity identity, IReplicationStore store) : IChangeTransport
{
    public bool IsConfigured => authority != null && store.Credential() != null;
    private Uri Endpoint(string path) => new(authority!, $"v1/tenants/{identity.TenantId}/stores/{identity.StoreId}/devices/{identity.DeviceId}/replication/{path}");
    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var credential = store.Credential() ?? throw new CloudIdentityRejected();
        using var request = new HttpRequestMessage(method, Endpoint(path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Device", credential.Secret);
        if (body != null) request.Content = JsonContent.Create(body);
        try { return await client.SendAsync(request, ct); }
        catch (HttpRequestException) { throw new CloudUnavailable("La nube de datos no está disponible."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new CloudUnavailable("La nube de datos no está disponible."); }
    }
    private static DeliveryOutcome Outcome(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => DeliveryOutcome.Unauthorized,
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests => DeliveryOutcome.Unavailable,
        _ => (int)status >= 500 ? DeliveryOutcome.Unavailable : (int)status is >= 200 and < 300 ? DeliveryOutcome.Accepted : DeliveryOutcome.Rejected
    };
    public async Task<DeliveryOutcome> ProbeAsync(CancellationToken ct)
    {
        if (!IsConfigured) return DeliveryOutcome.Unavailable;
        try { using var response = await Send(HttpMethod.Get, "status", null, ct); return Outcome(response.StatusCode); }
        catch (CloudUnavailable) { return DeliveryOutcome.Unavailable; }
        catch (CloudIdentityRejected) { return DeliveryOutcome.Unauthorized; }
    }
    public async Task<DeliveryOutcome> SendAsync(SyncEnvelope envelope, CancellationToken ct)
    {
        if (!IsConfigured) return DeliveryOutcome.Unavailable;
        try
        {
            using var response = await Send(HttpMethod.Post, "events", envelope, ct);
            var outcome = Outcome(response.StatusCode); if (outcome != DeliveryOutcome.Accepted) return outcome;
            var ack = await response.Content.ReadFromJsonAsync<SyncAcknowledgement>(ct);
            return ack?.EventId == envelope.EventId ? DeliveryOutcome.Accepted : DeliveryOutcome.Rejected;
        }
        catch (CloudUnavailable) { return DeliveryOutcome.Unavailable; }
        catch (CloudIdentityRejected) { return DeliveryOutcome.Unauthorized; }
        catch (JsonException) { return DeliveryOutcome.Rejected; }
    }
    public async Task<CloudChangePage> PullAsync(long after, CancellationToken ct)
    {
        try
        {
            using var response = await Send(HttpMethod.Get, $"changes?after={after}&limit=50", null, ct);
            switch (Outcome(response.StatusCode))
            {
                case DeliveryOutcome.Unauthorized: throw new CloudIdentityRejected();
                case DeliveryOutcome.Unavailable: throw new CloudUnavailable("La nube de datos no está disponible.");
                case DeliveryOutcome.Rejected: throw new ReplicationRejected();
            }
            return await response.Content.ReadFromJsonAsync<CloudChangePage>(ct) ?? throw new ReplicationRejected();
        }
        catch (JsonException) { throw new ReplicationRejected(); }
    }
}
