using Agropecuaria.Cloud.Infrastructure;
using Agropecuaria.Contracts;

namespace Agropecuaria.Cloud.Api;

public static class ReplicationEndpoints
{
    public static void Map(WebApplication app, CloudDirectory directory, CloudReplication replication)
    {
        Membership Admin(HttpContext context, Guid tenant, Guid store)
        {
            var member = directory.Get(tenant, store, context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
            if (member is not { Active: true, MustChange: false, Role: "Admin" }) throw new Agropecuaria.Domain.AccessDenied();
            return member;
        }
        var users = app.MapGroup("/v1/tenants/{tenant:guid}/stores/{store:guid}/devices").RequireAuthorization();
        users.MapPost("", (Guid tenant, Guid store, RegisterDeviceRequest request, HttpContext context) =>
            Results.Ok(replication.Register(Admin(context, tenant, store), request)));
        users.MapPost("/{device:guid}/revoke", (Guid tenant, Guid store, Guid device, HttpContext context) =>
        { replication.Revoke(Admin(context, tenant, store), device); return Results.NoContent(); });
        var devices = app.MapGroup("/v1/tenants/{tenant:guid}/stores/{store:guid}/devices/{device:guid}/replication").RequireAuthorization("Device");
        devices.MapGet("/status", () => Results.Ok(new { Status = "Ready" }));
        devices.MapPost("/events", (SyncEnvelope envelope, HttpContext context) =>
            Results.Ok(replication.Accept((DeviceIdentity)context.Items["device"]!, envelope)));
        devices.MapGet("/changes", (long after, int limit, HttpContext context) =>
            Results.Ok(replication.Pull((DeviceIdentity)context.Items["device"]!, after, limit)));
    }
}
