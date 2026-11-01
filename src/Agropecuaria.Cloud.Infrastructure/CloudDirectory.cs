using System.Data;
using System.Data.Common;
using Agropecuaria.Contracts;
using Agropecuaria.Domain;

namespace Agropecuaria.Cloud.Infrastructure;

public sealed record Membership(Guid TenantId, Guid StoreId, Guid Id, string Uid, string Email,
    string DisplayName, string Role, bool Active, bool MustChange, long Version)
{
    public UserView View => new(Id, Email, DisplayName, Role, Active, MustChange);
}

// PostgreSQL in deployment; SQLite is a distinct, explicit Development-only adapter.
// Memberships live on the cloud side, never in Firebase custom claims or a POS password file.
public sealed partial class CloudDirectory(Func<DbConnection> open, bool postgres)
{
    public void Initialize()
    {
        if (postgres) { using var database = open(); CloudSchema.Migrate(database); return; }
        using var connection = open(); using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS cloud_stores (tenant_id TEXT NOT NULL, store_id TEXT NOT NULL,
                PRIMARY KEY(tenant_id,store_id));
            CREATE TABLE IF NOT EXISTS cloud_memberships (
                tenant_id TEXT NOT NULL, store_id TEXT NOT NULL, operator_id TEXT NOT NULL,
                uid TEXT NOT NULL, email TEXT NOT NULL, display_name TEXT NOT NULL,
                role TEXT NOT NULL CHECK(role IN ('Admin','Cashier','Viewer')),
                active INTEGER NOT NULL CHECK(active IN (0,1)), must_change INTEGER NOT NULL CHECK(must_change IN (0,1)),
                version BIGINT NOT NULL, PRIMARY KEY(tenant_id,store_id,operator_id),
                UNIQUE(tenant_id,store_id,uid), UNIQUE(tenant_id,store_id,email),
                FOREIGN KEY(tenant_id,store_id) REFERENCES cloud_stores(tenant_id,store_id));
            CREATE TABLE IF NOT EXISTS cloud_user_provisioning (
                tenant_id TEXT NOT NULL,store_id TEXT NOT NULL,operation_id TEXT NOT NULL,target_uid TEXT NOT NULL UNIQUE,
                operator_id TEXT NOT NULL,request_hash TEXT NOT NULL,email TEXT NOT NULL,display_name TEXT NOT NULL,role TEXT NOT NULL,
                state TEXT NOT NULL CHECK(state IN ('Pending','Completed','Conflict')),result_json TEXT,created_at TEXT NOT NULL,completed_at TEXT,
                PRIMARY KEY(tenant_id,store_id,operation_id),UNIQUE(tenant_id,store_id,email),
                FOREIGN KEY(tenant_id,store_id) REFERENCES cloud_stores(tenant_id,store_id));
            CREATE TABLE IF NOT EXISTS cloud_account_audit (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,tenant_id TEXT NOT NULL,store_id TEXT NOT NULL,actor_uid TEXT NOT NULL,
                action TEXT NOT NULL,subject_id TEXT,operation_id TEXT,recorded_at TEXT NOT NULL,details_json TEXT NOT NULL,
                FOREIGN KEY(tenant_id,store_id) REFERENCES cloud_stores(tenant_id,store_id));
            """;
        command.ExecuteNonQuery();
    }
    public Membership? Get(Guid tenant, Guid store, string uid)
    {
        using var connection = open();
        using var command = Command(connection, null, "SELECT * FROM cloud_memberships WHERE tenant_id=@tenant AND store_id=@store AND uid=@uid",
            ("tenant", tenant.ToString()), ("store", store.ToString()), ("uid", uid));
        using var reader = command.ExecuteReader(); return reader.Read() ? Read(reader) : null;
    }
    public IReadOnlyList<StoreAccess> Discover(string uid)
    {
        using var connection = open();
        using var command = Command(connection, null, "SELECT tenant_id,store_id,role FROM cloud_memberships WHERE uid=@uid AND active=1 ORDER BY tenant_id,store_id", ("uid", uid));
        using var reader = command.ExecuteReader(); var stores = new List<StoreAccess>();
        while (reader.Read()) stores.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2)));
        return stores;
    }
    public Membership? GetById(Guid tenant, Guid store, Guid id)
    {
        using var connection = open();
        using var command = Command(connection, null, "SELECT * FROM cloud_memberships WHERE tenant_id=@tenant AND store_id=@store AND operator_id=@id",
            ("tenant", tenant.ToString()), ("store", store.ToString()), ("id", id.ToString()));
        using var reader = command.ExecuteReader(); return reader.Read() ? Read(reader) : null;
    }
    public long MembershipCount(string uid)
    {
        using var connection = open(); using var command = Command(connection, null, "SELECT COUNT(*) FROM cloud_memberships WHERE uid=@uid", ("uid", uid));
        return Convert.ToInt64(command.ExecuteScalar());
    }
    public IReadOnlyList<UserView> List(Membership actor)
    {
        using var connection = open(); using var transaction = connection.BeginTransaction(postgres ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable);
        RequireAdmin(connection, transaction, actor);
        using var command = Command(connection, transaction, "SELECT * FROM cloud_memberships WHERE tenant_id=@tenant AND store_id=@store ORDER BY email",
            ("tenant", actor.TenantId.ToString()), ("store", actor.StoreId.ToString()));
        using var reader = command.ExecuteReader(); var users = new List<UserView>();
        while (reader.Read()) users.Add(Read(reader).View);
        return users;
    }
    public Membership Bootstrap(Guid tenant, Guid store, string uid, string email, string displayName)
    {
        using var connection = open(); using var transaction = connection.BeginTransaction(postgres ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable);
        Execute(connection, transaction, "INSERT INTO cloud_stores VALUES(@tenant,@store) ON CONFLICT DO NOTHING", ("tenant", tenant.ToString()), ("store", store.ToString()));
        LockStore(connection, transaction, tenant, store);
        var count = Scalar(connection, transaction, "SELECT COUNT(*) FROM cloud_memberships WHERE tenant_id=@tenant AND store_id=@store", ("tenant", tenant.ToString()), ("store", store.ToString()));
        if (Convert.ToInt64(count) != 0) throw new OperationConflict("La inicialización del negocio ya se completó.");
        var member = new Membership(tenant, store, Guid.NewGuid(), uid, email, displayName, "Admin", true, false, 1);
        Insert(connection, transaction, member);
        AccountAudit(connection, transaction, new(tenant, store, member.Id, uid), "OwnerBootstrapped", member.Id, null, new { member.Role });
        transaction.Commit(); return member;
    }
    public Membership Add(Membership actor, string uid, string email, string displayName, string role)
    {
        ValidateRole(role);
        using var connection = open(); using var transaction = connection.BeginTransaction(postgres ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable);
        LockStore(connection, transaction, actor.TenantId, actor.StoreId); RequireAdmin(connection, transaction, actor);
        var duplicate = Scalar(connection, transaction, "SELECT operator_id FROM cloud_memberships WHERE tenant_id=@tenant AND store_id=@store AND (email=@email OR uid=@uid)",
            ("tenant", actor.TenantId.ToString()), ("store", actor.StoreId.ToString()), ("email", email), ("uid", uid));
        if (duplicate != null) throw new OperationConflict("La cuenta ya pertenece a este negocio.");
        var member = new Membership(actor.TenantId, actor.StoreId, Guid.NewGuid(), uid, email, displayName, role, true, true, 1);
        Insert(connection, transaction, member);
        AccountAudit(connection, transaction, new(actor.TenantId, actor.StoreId, actor.Id, actor.Uid), "UserCreated", member.Id, null, new { member.Role });
        transaction.Commit(); return member;
    }
    public Membership Update(Membership actor, Guid id, UpdateUserRequest update)
    {
        ValidateRole(update.Role);
        using var connection = open(); using var transaction = connection.BeginTransaction(postgres ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable);
        LockStore(connection, transaction, actor.TenantId, actor.StoreId); RequireAdmin(connection, transaction, actor);
        var target = ReadTarget(connection, transaction, actor, id);
        if (target.Role == "Admin" && target.Active && (!update.IsActive || update.Role != "Admin"))
        {
            var admins = Convert.ToInt64(Scalar(connection, transaction,
                "SELECT COUNT(*) FROM cloud_memberships WHERE tenant_id=@tenant AND store_id=@store AND role='Admin' AND active=1",
                ("tenant", actor.TenantId.ToString()), ("store", actor.StoreId.ToString())));
            if (admins <= 1) throw new OperationConflict("No se puede desactivar o degradar al último administrador.");
        }
        Execute(connection, transaction, """
            UPDATE cloud_memberships SET role=@role,active=@active,version=version+1
            WHERE tenant_id=@tenant AND store_id=@store AND operator_id=@id
            """, ("role", update.Role), ("active", update.IsActive ? 1 : 0), ("tenant", actor.TenantId.ToString()), ("store", actor.StoreId.ToString()), ("id", id.ToString()));
        AccountAudit(connection, transaction, new(actor.TenantId, actor.StoreId, actor.Id, actor.Uid), "UserRoleOrStatusChanged", target.Id, null,
            new { OldRole = target.Role, NewRole = update.Role, WasActive = target.Active, IsActive = update.IsActive });
        transaction.Commit(); return target with { Role = update.Role, Active = update.IsActive, Version = target.Version + 1 };
    }
    public void SetPasswordChange(Membership actor, Guid targetId, bool required)
    {
        using var connection = open(); using var transaction = connection.BeginTransaction(postgres ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable);
        LockStore(connection, transaction, actor.TenantId, actor.StoreId);
        if (actor.Id != targetId) RequireAdmin(connection, transaction, actor);
        ReadTarget(connection, transaction, actor, targetId);
        Execute(connection, transaction, "UPDATE cloud_memberships SET must_change=@change,version=version+1 WHERE tenant_id=@tenant AND store_id=@store AND operator_id=@id",
            ("change", required ? 1 : 0), ("tenant", actor.TenantId.ToString()), ("store", actor.StoreId.ToString()), ("id", targetId.ToString()));
        AccountAudit(connection, transaction, new(actor.TenantId, actor.StoreId, actor.Id, actor.Uid), required ? "PasswordChangeRequired" : "PasswordChangeRequirementCleared", targetId, null, new { });
        transaction.Commit();
    }
    private static void ValidateRole(string role)
    { if (role is not ("Admin" or "Cashier" or "Viewer")) throw new RuleViolation("Rol inválido."); }
    private void LockStore(DbConnection connection, DbTransaction transaction, Guid tenant, Guid store)
    {
        using var command = Command(connection, transaction, "SELECT store_id FROM cloud_stores WHERE tenant_id=@tenant AND store_id=@store" + (postgres ? " FOR UPDATE" : ""),
            ("tenant", tenant.ToString()), ("store", store.ToString()));
        if (command.ExecuteScalar() == null) throw new AccessDenied();
    }
    private static void RequireAdmin(DbConnection connection, DbTransaction transaction, Membership actor)
    {
        var current = ReadTarget(connection, transaction, actor, actor.Id);
        if (!current.Active || current.Role != "Admin" || current.MustChange) throw new AccessDenied();
    }
    private static Membership ReadTarget(DbConnection connection, DbTransaction transaction, Membership scope, Guid id)
    {
        using var command = Command(connection, transaction, "SELECT * FROM cloud_memberships WHERE tenant_id=@tenant AND store_id=@store AND operator_id=@id",
            ("tenant", scope.TenantId.ToString()), ("store", scope.StoreId.ToString()), ("id", id.ToString()));
        using var reader = command.ExecuteReader(); if (!reader.Read()) throw new AccessDenied(); return Read(reader);
    }
    private static Membership Read(DbDataReader reader) => new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
        reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt32(7) == 1, reader.GetInt32(8) == 1, reader.GetInt64(9));
    private static void Insert(DbConnection connection, DbTransaction transaction, Membership member) => Execute(connection, transaction,
        "INSERT INTO cloud_memberships VALUES(@tenant,@store,@id,@uid,@email,@name,@role,@active,@change,@version)",
        ("tenant", member.TenantId.ToString()), ("store", member.StoreId.ToString()), ("id", member.Id.ToString()), ("uid", member.Uid), ("email", member.Email),
        ("name", member.DisplayName), ("role", member.Role), ("active", member.Active ? 1 : 0), ("change", member.MustChange ? 1 : 0), ("version", member.Version));
    private static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object Value)[] values)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in values) { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter); }
        return command;
    }
    private static object? Scalar(DbConnection connection, DbTransaction transaction, string sql, params (string Name, object Value)[] values)
    { using var command = Command(connection, transaction, sql, values); return command.ExecuteScalar(); }
    private static void Execute(DbConnection connection, DbTransaction transaction, string sql, params (string Name, object Value)[] values)
    { using var command = Command(connection, transaction, sql, values); command.ExecuteNonQuery(); }
}
