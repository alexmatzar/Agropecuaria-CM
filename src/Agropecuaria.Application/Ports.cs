using Agropecuaria.Contracts;

namespace Agropecuaria.Application;

public sealed record InstallationIdentity(Guid TenantId, Guid StoreId, Guid DeviceId, string Currency);

public interface IPosStore
{
    void Initialize();
    ProductView CreateProduct(CreateProductRequest request, OperatorIdentity? actor = null);
    IReadOnlyList<ProductView> ListProducts();
    SaleResult CompleteSale(CreateSaleRequest request, OperatorIdentity? actor = null);
}

public interface IOutbox
{
    IReadOnlyList<SyncEnvelope> ReadPending(int limit);
    void Acknowledge(Guid eventId, DateTimeOffset atUtc);
    long PendingCount();
    DateTimeOffset? LastSuccessUtc();
}

public enum DeliveryOutcome { Accepted, Unavailable, Unauthorized, Rejected }
public interface ISyncTransport
{
    bool IsConfigured { get; }
    Task<DeliveryOutcome> ProbeAsync(CancellationToken cancellationToken);
    Task<DeliveryOutcome> SendAsync(SyncEnvelope envelope, CancellationToken cancellationToken);
}

// A cycle is serialized even if manually triggered while the background worker is running.
public sealed class SyncCoordinator(IOutbox outbox, ISyncTransport transport, IReplicationStore? replica = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string state = "Starting";
    private string? errorCode;

    public SyncStatus Status => new(Volatile.Read(ref state), outbox.PendingCount(),
        outbox.LastSuccessUtc(), Volatile.Read(ref errorCode));

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!transport.IsConfigured) { Set(replica?.EnrollmentPending() == true ? "AttentionRequired" : "NotConfigured", replica?.EnrollmentPending() == true ? "EnrollmentPending" : null); return; }
            Set("Syncing", null);
            var reachable = await transport.ProbeAsync(cancellationToken);
            if (reachable != DeliveryOutcome.Accepted)
            {
                if (reachable == DeliveryOutcome.Unauthorized && transport is IChangeTransport) replica?.SuspendWrites();
                Set(reachable == DeliveryOutcome.Unavailable ? "Offline" : "AttentionRequired", reachable.ToString());
                return;
            }
            // Preserve per-device ordering; stop at the first failed event.
            foreach (var envelope in outbox.ReadPending(100))
            {
                var outcome = await transport.SendAsync(envelope, cancellationToken);
                if (outcome != DeliveryOutcome.Accepted)
                {
                    if (outcome == DeliveryOutcome.Unauthorized && transport is IChangeTransport) replica?.SuspendWrites();
                    Set(outcome == DeliveryOutcome.Unavailable ? "Offline" : "AttentionRequired", outcome.ToString());
                    return;
                }
                outbox.Acknowledge(envelope.EventId, DateTimeOffset.UtcNow);
            }
            var caughtUp = true;
            if (transport is IChangeTransport changes && replica != null)
            {
                var page = await changes.PullAsync(replica.CloudCursor(), cancellationToken);
                replica.ApplyPage(page);
                caughtUp = page.ThroughCursor == page.HeadCursor;
            }
            Set(outbox.PendingCount() == 0 && caughtUp ? "UpToDate" : replica?.IsReady() == false ? "Restoring" : "Pending", null);
        }
        catch (CloudUnavailable) { Set("Offline", "Unavailable"); }
        catch (CloudIdentityRejected) { replica?.SuspendWrites(); Set("AttentionRequired", "Unauthorized"); }
        catch (Exception ex) when (ex is ReplicationRejected or Agropecuaria.Domain.RuleViolation or Agropecuaria.Domain.OperationConflict or System.Text.Json.JsonException or OverflowException)
        { Set("AttentionRequired", "Rejected"); }
        finally { gate.Release(); }
    }

    private void Set(string next, string? error)
    {
        Volatile.Write(ref errorCode, error);
        Volatile.Write(ref state, next);
    }
}
