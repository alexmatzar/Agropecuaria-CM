using System.Net.Mail;
using Agropecuaria.Contracts;
using Agropecuaria.Domain;

namespace Agropecuaria.Application;

public sealed record CloudAccountActor(Guid TenantId, Guid StoreId, Guid OperatorId, string Uid);
public sealed record ProvisioningTicket(Guid OperationId, string FirebaseUid, Guid OperatorId,
    string Email, string DisplayName, string Role, UserView? Completed);
public sealed class CloudAccountConflict : Exception;
public interface ICloudProvisioningJournal
{
    ProvisioningTicket PrepareUser(CloudAccountActor actor, CreateUserRequest request);
    UserView CompleteUser(CloudAccountActor actor, Guid operationId);
    void MarkUserConflict(CloudAccountActor actor, Guid operationId);
}
public interface ICloudAccountProvider
{
    Task EnsureAsync(ProvisioningTicket ticket, string temporaryPassword, CancellationToken ct);
}
public sealed class CloudAccountsService(ICloudProvisioningJournal journal, ICloudAccountProvider provider)
{
    public async Task<UserView> CreateAsync(CloudAccountActor actor, CreateUserRequest request, CancellationToken ct)
    {
        if (request.OperationId is null || request.OperationId == Guid.Empty) throw new RuleViolation("OperationId es requerido para crear una cuenta.");
        if (request.Username is null || request.Username.Length > 254 || !MailAddress.TryCreate(request.Username, out var email) || email.Address != request.Username ||
            string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 100 || request.Role is not ("Admin" or "Cashier" or "Viewer") ||
            request.TemporaryPassword is null || request.TemporaryPassword.Length is < 15 or > 128 || string.IsNullOrWhiteSpace(request.TemporaryPassword))
            throw new RuleViolation("Cuenta o contraseña temporal inválida.");
        var normalized = request with { Username = email.Address.ToLowerInvariant(), DisplayName = request.DisplayName.Trim() };
        var ticket = journal.PrepareUser(actor, normalized);
        if (ticket.Completed != null) return ticket.Completed;
        try { await provider.EnsureAsync(ticket, request.TemporaryPassword, ct); }
        catch (CloudAccountConflict)
        {
            journal.MarkUserConflict(actor, ticket.OperationId);
            throw new OperationConflict("La identidad ya existe o requiere revisión. No se reemplaza su contraseña ni se concede acceso; use el futuro flujo de invitación.");
        }
        return journal.CompleteUser(actor, ticket.OperationId);
    }
}
public interface ICloudAuditAuthority
{
    Task<IReadOnlyList<CloudAuditView>> AuditAsync(string token, Guid tenant, Guid store, CancellationToken ct);
}
