using Agropecuaria.Contracts;

namespace Agropecuaria.Application;

public sealed record ReplicationCredential(string Secret, bool CanWrite);
public interface IReplicationStore
{
    ReplicationCredential? Credential();
    bool HasBusinessData();
    bool EnrollmentPending();
    bool IsReady();
    RegisterDeviceRequest PrepareEnrollment(bool canWrite, bool replaceWriter);
    void EnrollDevice(RegisteredDevice device);
    void SuspendWrites();
    long CloudCursor();
    void ApplyPage(CloudChangePage page);
}
public interface IDeviceAuthority
{
    Task<RegisteredDevice> RegisterDeviceAsync(string idToken, Guid tenant, Guid store, RegisterDeviceRequest request, CancellationToken ct);
}
public interface IChangeTransport : ISyncTransport
{
    Task<CloudChangePage> PullAsync(long after, CancellationToken ct);
}
public sealed class ReplicationRejected : Exception
{
    public ReplicationRejected() : base("La respuesta de sincronización es incompatible o requiere conciliación.") { }
}
