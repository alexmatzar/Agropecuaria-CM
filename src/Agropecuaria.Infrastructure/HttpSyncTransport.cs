using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agropecuaria.Application;
using Agropecuaria.Contracts;

namespace Agropecuaria.Infrastructure;

public sealed class HttpSyncTransport(HttpClient client, Uri? endpoint, string? token) : ISyncTransport
{
    public bool IsConfigured => endpoint is not null && !string.IsNullOrWhiteSpace(token);
    public async Task<DeliveryOutcome> ProbeAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured) return DeliveryOutcome.Unavailable;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint!, "/health"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return DeliveryOutcome.Unauthorized;
            return response.IsSuccessStatusCode ? DeliveryOutcome.Accepted : DeliveryOutcome.Unavailable;
        }
        catch (HttpRequestException) { return DeliveryOutcome.Unavailable; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return DeliveryOutcome.Unavailable; }
    }
    public async Task<DeliveryOutcome> SendAsync(SyncEnvelope envelope, CancellationToken cancellationToken)
    {
        if (!IsConfigured) return DeliveryOutcome.Unavailable;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = JsonContent.Create(envelope);
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return DeliveryOutcome.Unauthorized;
            if ((int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
                return DeliveryOutcome.Unavailable;
            if (!response.IsSuccessStatusCode) return DeliveryOutcome.Rejected;
            var ack = await response.Content.ReadFromJsonAsync<SyncAcknowledgement>(cancellationToken);
            return ack?.EventId == envelope.EventId ? DeliveryOutcome.Accepted : DeliveryOutcome.Rejected;
        }
        catch (HttpRequestException) { return DeliveryOutcome.Unavailable; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return DeliveryOutcome.Unavailable; }
        catch (System.Text.Json.JsonException) { return DeliveryOutcome.Rejected; }
    }
}
