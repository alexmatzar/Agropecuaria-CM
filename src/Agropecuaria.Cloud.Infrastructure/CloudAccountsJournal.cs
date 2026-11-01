using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agropecuaria.Application;
using Agropecuaria.Contracts;
using Agropecuaria.Domain;

namespace Agropecuaria.Cloud.Infrastructure;

public sealed partial class CloudDirectory : ICloudProvisioningJournal
{
    private DbTransaction Transaction(DbConnection connection) => connection.BeginTransaction(postgres ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable);
    private static Membership Scope(CloudAccountActor actor) => new(actor.TenantId, actor.StoreId, actor.OperatorId, actor.Uid, "", "", "", true, false, 0);
    public ProvisioningTicket PrepareUser(CloudAccountActor actor, CreateUserRequest request)
    {
        if (request.OperationId is null || request.OperationId == Guid.Empty) throw new RuleViolation("Falta OperationId.");
        ValidateRole(request.Role);
        var scope = Scope(actor);
        // Passwords and password-derived request hashes never enter the journal.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { request.Username, request.DisplayName, request.Role }))));
        using var connection = open(); using var transaction = Transaction(connection);
        LockStore(connection, transaction, actor.TenantId, actor.StoreId); RequireAdmin(connection, transaction, scope);
        using (var command = Command(connection, transaction, "SELECT request_hash FROM cloud_user_provisioning WHERE tenant_id=@tenant AND store_id=@store AND operation_id=@op",
            ("tenant", actor.TenantId.ToString()), ("store", actor.StoreId.ToString()), ("op", request.OperationId.Value.ToString())))
        {
            if (command.ExecuteScalar() is string saved)
            {
                if (saved != hash) throw new OperationConflict("OperationId ya usado con otra cuenta o permisos.");
                return Ticket(connection, transaction, actor, request.OperationId.Value);
            }
        }
        if (Scalar(connection, transaction, """
            SELECT operator_id FROM cloud_memberships WHERE tenant_id=@tenant AND store_id=@store AND email=@email
            UNION ALL SELECT operator_id FROM cloud_user_provisioning WHERE tenant_id=@tenant AND store_id=@store AND email=@email
            """, ("tenant", actor.TenantId.ToString()), ("store", actor.StoreId.ToString()), ("email", request.Username)) != null)
            throw new OperationConflict("La cuenta ya existe o tiene un aprovisionamiento pendiente.");
        var uid = "cm2-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{actor.TenantId:N}:{actor.StoreId:N}:{request.OperationId.Value:N}")))[..48];
        var operatorId = Guid.NewGuid();
        Execute(connection, transaction, """
            INSERT INTO cloud_user_provisioning VALUES(@tenant,@store,@op,@uid,@operator,@hash,@email,@name,@role,'Pending',NULL,@time,NULL)
            """, ("tenant", actor.TenantId.ToString()), ("store", actor.StoreId.ToString()), ("op", request.OperationId.Value.ToString()),
            ("uid", uid), ("operator", operatorId.ToString()), ("hash", hash), ("email", request.Username), ("name", request.DisplayName),
            ("role", request.Role), ("time", DateTimeOffset.UtcNow.ToString("O")));
        AccountAudit(connection, transaction, actor, "UserProvisioningRequested", operatorId, request.OperationId, new { request.Role });
        transaction.Commit();
        return new(request.OperationId.Value, uid, operatorId, request.Username, request.DisplayName, request.Role, null);
    }
    public UserView CompleteUser(CloudAccountActor actor, Guid operationId)
    {
        using var connection = open(); using var transaction = Transaction(connection);
        LockStore(connection, transaction, actor.TenantId, actor.StoreId); RequireAdmin(connection, transaction, Scope(actor));
        var ticket = Ticket(connection, transaction, actor, operationId);
        if (ticket.Completed != null) return ticket.Completed;
        var member = new Membership(actor.TenantId, actor.StoreId, ticket.OperatorId, ticket.FirebaseUid, ticket.Email, ticket.DisplayName, ticket.Role, true, true, 1);
        Insert(connection, transaction, member);
        Execute(connection, transaction, """
            UPDATE cloud_user_provisioning SET state='Completed',result_json=@result,completed_at=@time
            WHERE tenant_id=@tenant AND store_id=@store AND operation_id=@op
            """, ("result", JsonSerializer.Serialize(member.View)), ("time", DateTimeOffset.UtcNow.ToString("O")),
            ("tenant", actor.TenantId.ToString()), ("store", actor.StoreId.ToString()), ("op", operationId.ToString()));
        AccountAudit(connection, transaction, actor, "UserCreated", member.Id, operationId, new { member.Role });
        transaction.Commit(); return member.View;
    }
    public void MarkUserConflict(CloudAccountActor actor, Guid operationId)
    {
        using var connection = open(); using var transaction = Transaction(connection);
        LockStore(connection, transaction, actor.TenantId, actor.StoreId); RequireAdmin(connection, transaction, Scope(actor));
        var ticket = Ticket(connection, transaction, actor, operationId);
        if (ticket.Completed != null) throw new OperationConflict("La operación ya fue completada.");
        Execute(connection, transaction, "UPDATE cloud_user_provisioning SET state='Conflict' WHERE tenant_id=@tenant AND store_id=@store AND operation_id=@op",
            ("tenant", actor.TenantId.ToString()), ("store", actor.StoreId.ToString()), ("op", operationId.ToString()));
        AccountAudit(connection, transaction, actor, "UserProvisioningConflict", ticket.OperatorId, operationId, new { Reason = "IdentityConflict" });
        transaction.Commit();
    }
    private static ProvisioningTicket Ticket(DbConnection connection, DbTransaction transaction, CloudAccountActor actor, Guid operationId)
    {
        using var command = Command(connection, transaction, """
            SELECT target_uid,operator_id,email,display_name,role,state,result_json FROM cloud_user_provisioning
            WHERE tenant_id=@tenant AND store_id=@store AND operation_id=@op
            """, ("tenant", actor.TenantId.ToString()), ("store", actor.StoreId.ToString()), ("op", operationId.ToString()));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new AccessDenied();
        if (reader.GetString(5) == "Conflict") throw new OperationConflict("El aprovisionamiento necesita un flujo de invitación o revisión.");
        return new(operationId, reader.GetString(0), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.IsDBNull(6) ? null : JsonSerializer.Deserialize<UserView>(reader.GetString(6)));
    }
    public IReadOnlyList<CloudAuditView> AccountAudit(Membership actor)
    {
        using var connection = open(); using var transaction = Transaction(connection); RequireAdmin(connection, transaction, actor);
        using var command = Command(connection, transaction, """
            SELECT sequence,actor_uid,action,subject_id,operation_id,recorded_at,details_json FROM cloud_account_audit
            WHERE tenant_id=@tenant AND store_id=@store ORDER BY sequence DESC LIMIT 200
            """, ("tenant", actor.TenantId.ToString()), ("store", actor.StoreId.ToString()));
        using var reader = command.ExecuteReader(); var rows = new List<CloudAuditView>();
        while (reader.Read()) rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)), reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
            DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture), reader.GetString(6)));
        return rows;
    }
    public void RecordAccountEvent(Membership actor, string action, Guid subject)
    {
        using var connection = open(); using var transaction = Transaction(connection);
        LockStore(connection, transaction, actor.TenantId, actor.StoreId);
        var current = ReadTarget(connection, transaction, actor, actor.Id);
        if (!current.Active || current.Uid != actor.Uid) throw new AccessDenied();
        if (actor.Id != subject) RequireAdmin(connection, transaction, actor);
        ReadTarget(connection, transaction, actor, subject);
        AccountAudit(connection, transaction, new(actor.TenantId, actor.StoreId, actor.Id, actor.Uid), action, subject, null, new { });
        transaction.Commit();
    }
    private static void AccountAudit(DbConnection connection, DbTransaction transaction, CloudAccountActor actor, string action, Guid? subject, Guid? operation, object details) =>
        Execute(connection, transaction, """
            INSERT INTO cloud_account_audit(tenant_id,store_id,actor_uid,action,subject_id,operation_id,recorded_at,details_json)
            VALUES(@tenant,@store,@uid,@action,@subject,@op,@time,@details)
            """, ("tenant", actor.TenantId.ToString()), ("store", actor.StoreId.ToString()), ("uid", actor.Uid), ("action", action),
            ("subject", (object?)subject?.ToString() ?? DBNull.Value), ("op", (object?)operation?.ToString() ?? DBNull.Value),
            ("time", DateTimeOffset.UtcNow.ToString("O")), ("details", JsonSerializer.Serialize(details)));
}
