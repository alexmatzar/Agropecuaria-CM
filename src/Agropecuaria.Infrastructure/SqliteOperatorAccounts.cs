using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Agropecuaria.Application;
using Agropecuaria.Contracts;
using Agropecuaria.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Agropecuaria.Infrastructure;

public sealed partial class SqlitePosStore
{
    private static readonly PasswordHasher<object> Hasher = new(Options.Create(new PasswordHasherOptions { IterationCount = 220_000 }));
    private static readonly object HashSubject = new();
    private static readonly string DummyHash = Hasher.HashPassword(HashSubject, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    private DateTimeOffset Now => (timeProvider ?? TimeProvider.System).GetUtcNow();
    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

    private static void InitializeSecurity(SqliteConnection connection, SqliteTransaction transaction) =>
        Execute(connection, transaction, """
            CREATE TABLE operators (
                id TEXT PRIMARY KEY, username TEXT NOT NULL UNIQUE, display_name TEXT NOT NULL,
                role TEXT NOT NULL CHECK(role IN ('Admin','Cashier','Viewer')),
                password_hash TEXT NOT NULL, active INTEGER NOT NULL CHECK(active IN (0,1)),
                must_change INTEGER NOT NULL CHECK(must_change IN (0,1)),
                failed_attempts INTEGER NOT NULL DEFAULT 0, locked_until TEXT
            ) STRICT;
            CREATE TABLE sessions (
                token_hash TEXT PRIMARY KEY, operator_id TEXT NOT NULL REFERENCES operators(id),
                expires_at TEXT NOT NULL, idle_expires_at TEXT NOT NULL
            ) STRICT;
            CREATE INDEX ix_sessions_operator ON sessions(operator_id);
            CREATE TABLE security_audit (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT, actor_id TEXT, action TEXT NOT NULL,
                subject_id TEXT, recorded_at TEXT NOT NULL
            ) STRICT;
            ALTER TABLE sales ADD COLUMN operator_id TEXT REFERENCES operators(id);
            PRAGMA user_version=2;
            """);

    public UserView BootstrapAdministrator(string username, string displayName, string password)
    {
        ValidatePassword(password);
        var normalized = Username(username); ValidateDisplayName(displayName);
        var hash = Hasher.HashPassword(HashSubject, password);
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        if ((long)Scalar(connection, transaction, "SELECT COUNT(*) FROM operators")! != 0)
            throw new OperationConflict("Ya existen operadores; el alta inicial está cerrada.");
        var id = Guid.NewGuid();
        InsertOperator(connection, transaction, id, normalized, displayName.Trim(), OperatorRole.Admin, hash, false);
        AuditWrite(connection, transaction, id, "AdministratorBootstrapped", id, Now);
        transaction.Commit(); return new(id, normalized, displayName.Trim(), "Admin", true, false);
    }

    public LoginResult Login(LoginRequest request)
    {
        if (request.Password is null || request.Password.Length > 128) throw new LoginRejected();
        string username;
        try { username = Username(request.Username); }
        catch (RuleViolation) { Hasher.VerifyHashedPassword(HashSubject, DummyHash, request.Password); throw new LoginRejected(); }
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        var row = FindOperator(connection, transaction, "username", username);
        var result = Hasher.VerifyHashedPassword(HashSubject, row?.PasswordHash ?? DummyHash, request.Password);
        var now = Now;
        if (row is null || !row.Active || row.LockedUntil > now || result == PasswordVerificationResult.Failed)
        {
            if (row != null && row.Active && !(row.LockedUntil > now))
            {
                var failed = row.LockedUntil != null ? 1 : row.FailedAttempts + 1;
                Execute(connection, transaction, "UPDATE operators SET failed_attempts=$failed,locked_until=$until WHERE id=$id",
                    ("$failed", failed), ("$until", failed >= 5 ? Timestamp(now.AddMinutes(15)) : DBNull.Value), ("$id", row.Id.ToString()));
            }
            AuditWrite(connection, transaction, null, "LoginRejected", row?.Id, now);
            transaction.Commit(); throw new LoginRejected();
        }
        Execute(connection, transaction, "UPDATE operators SET failed_attempts=0,locked_until=NULL,password_hash=$hash WHERE id=$id",
            ("$hash", result == PasswordVerificationResult.SuccessRehashNeeded ? Hasher.HashPassword(HashSubject, request.Password) : row.PasswordHash), ("$id", row.Id.ToString()));
        Execute(connection, transaction, "DELETE FROM sessions WHERE expires_at<=$now OR idle_expires_at<=$now", ("$now", Timestamp(now)));
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var expires = now.AddHours(8);
        Execute(connection, transaction, "INSERT INTO sessions VALUES($token,$user,$expires,$idle)",
            ("$token", TokenHash(token)), ("$user", row.Id.ToString()), ("$expires", Timestamp(expires)), ("$idle", Timestamp(now.AddMinutes(30))));
        AuditWrite(connection, transaction, row.Id, "LoginSucceeded", row.Id, now);
        transaction.Commit();
        return new(token, expires, row.View);
    }

    // Offline administration tool only. Caller must first prove possession of recovery material.
    // Deliberately absent from IOperatorAccounts and all HTTP routes.
    public void RecoverAdministrator(string username, string newPassword)
    {
        ValidatePassword(newPassword); var normalized = Username(username);
        var hash = Hasher.HashPassword(HashSubject, newPassword);
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        var row = FindOperator(connection, transaction, "username", normalized);
        if (row is null || row.Role != OperatorRole.Admin) throw new RuleViolation("Administrador existente no encontrado.");
        Execute(connection, transaction, "UPDATE operators SET password_hash=$hash,active=1,must_change=1,failed_attempts=0,locked_until=NULL WHERE id=$id",
            ("$hash", hash), ("$id", row.Id.ToString()));
        Execute(connection, transaction, "DELETE FROM sessions");
        AuditWrite(connection, transaction, null, "AdministratorRecoveredOffline", row.Id, Now);
        transaction.Commit();
    }

    public OperatorIdentity? Authenticate(string token)
    {
        if (token.Length is < 40 or > 100) return null;
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        string? operatorId;
        using (var command = Command(connection, transaction, "SELECT operator_id FROM sessions WHERE token_hash=$hash AND expires_at>$now AND idle_expires_at>$now",
            ("$hash", TokenHash(token)), ("$now", Timestamp(Now))))
            operatorId = command.ExecuteScalar() as string;
        if (operatorId is null) return null;
        var row = FindOperator(connection, transaction, "id", operatorId);
        if (row is null || !row.Active) return null;
        Execute(connection, transaction, "UPDATE sessions SET idle_expires_at=$idle WHERE token_hash=$hash",
            ("$idle", Timestamp(Now.AddMinutes(30))), ("$hash", TokenHash(token)));
        transaction.Commit();
        return new(row.Id, row.Username, row.DisplayName, row.Role, row.MustChange);
    }
    public void Logout(string token)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        var userId = Scalar(connection, transaction, "SELECT operator_id FROM sessions WHERE token_hash=$hash", ("$hash", TokenHash(token))) as string;
        Execute(connection, transaction, "DELETE FROM sessions WHERE token_hash=$hash", ("$hash", TokenHash(token)));
        if (Guid.TryParse(userId, out var id)) AuditWrite(connection, transaction, id, "Logout", id, Now);
        transaction.Commit();
    }

    public IReadOnlyList<UserView> ListUsers(OperatorIdentity actor)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(); RequireAdmin(connection, transaction, actor);
        using var command = Command(connection, transaction, "SELECT id,username,display_name,role,active,must_change FROM operators ORDER BY username");
        using var reader = command.ExecuteReader(); var users = new List<UserView>();
        while (reader.Read()) users.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4), reader.GetBoolean(5)));
        return users;
    }
    public UserView CreateUser(OperatorIdentity actor, CreateUserRequest request)
    {
        var username = Username(request.Username); ValidateDisplayName(request.DisplayName); ValidatePassword(request.TemporaryPassword);
        var role = Parse<OperatorRole>(request.Role);
        var hash = Hasher.HashPassword(HashSubject, request.TemporaryPassword);
        using var connection = Open(); using var transaction = connection.BeginTransaction(); RequireAdmin(connection, transaction, actor);
        if (FindOperator(connection, transaction, "username", username) != null) throw new OperationConflict("El nombre de usuario ya existe.");
        var id = Guid.NewGuid(); InsertOperator(connection, transaction, id, username, request.DisplayName.Trim(), role, hash, true);
        AuditWrite(connection, transaction, actor.Id, "UserCreated", id, Now); transaction.Commit();
        return new(id, username, request.DisplayName.Trim(), role.ToString(), true, true);
    }
    public UserView UpdateUser(OperatorIdentity actor, Guid userId, UpdateUserRequest request)
    {
        var role = Parse<OperatorRole>(request.Role);
        using var connection = Open(); using var transaction = connection.BeginTransaction(); RequireAdmin(connection, transaction, actor);
        var row = FindOperator(connection, transaction, "id", userId.ToString()) ?? throw new RuleViolation("Usuario no encontrado.");
        if (row.Active && row.Role == OperatorRole.Admin && (!request.IsActive || role != OperatorRole.Admin) &&
            (long)Scalar(connection, transaction, "SELECT COUNT(*) FROM operators WHERE active=1 AND role='Admin'")! <= 1)
            throw new OperationConflict("No se puede desactivar o degradar al último administrador.");
        Execute(connection, transaction, "UPDATE operators SET role=$role,active=$active WHERE id=$id",
            ("$role", role.ToString()), ("$active", request.IsActive ? 1 : 0), ("$id", userId.ToString()));
        RevokeSessions(connection, transaction, userId);
        AuditWrite(connection, transaction, actor.Id, request.IsActive ? "UserRoleOrStatusChanged" : "UserDisabled", userId, Now);
        transaction.Commit(); return row.View with { Role = role.ToString(), IsActive = request.IsActive };
    }
    public void ResetPassword(OperatorIdentity actor, Guid userId, string newPassword)
    {
        ValidatePassword(newPassword); var hash = Hasher.HashPassword(HashSubject, newPassword);
        using var connection = Open(); using var transaction = connection.BeginTransaction(); RequireAdmin(connection, transaction, actor);
        if (FindOperator(connection, transaction, "id", userId.ToString()) is null) throw new RuleViolation("Usuario no encontrado.");
        Execute(connection, transaction, "UPDATE operators SET password_hash=$hash,must_change=1,failed_attempts=0,locked_until=NULL WHERE id=$id",
            ("$hash", hash), ("$id", userId.ToString()));
        RevokeSessions(connection, transaction, userId); AuditWrite(connection, transaction, actor.Id, "PasswordReset", userId, Now);
        transaction.Commit();
    }
    public void ChangePassword(OperatorIdentity actor, ChangePasswordRequest request)
    {
        ValidatePassword(request.NewPassword);
        if (request.CurrentPassword is null || request.CurrentPassword.Length > 128 || request.CurrentPassword == request.NewPassword)
            throw new RuleViolation("La nueva contraseña debe ser distinta de la actual.");
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        var row = RequireOperator(connection, transaction, actor);
        if (row.LockedUntil > Now || Hasher.VerifyHashedPassword(HashSubject, row.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
        {
            var failed = row.FailedAttempts + 1;
            Execute(connection, transaction, "UPDATE operators SET failed_attempts=$failed,locked_until=$until WHERE id=$id",
                ("$failed", failed), ("$until", failed >= 5 ? Timestamp(Now.AddMinutes(15)) : DBNull.Value), ("$id", actor.Id.ToString()));
            AuditWrite(connection, transaction, actor.Id, "PasswordChangeRejected", actor.Id, Now); transaction.Commit(); throw new LoginRejected();
        }
        Execute(connection, transaction, "UPDATE operators SET password_hash=$hash,must_change=0,failed_attempts=0,locked_until=NULL WHERE id=$id",
            ("$hash", Hasher.HashPassword(HashSubject, request.NewPassword)), ("$id", actor.Id.ToString()));
        RevokeSessions(connection, transaction, actor.Id); AuditWrite(connection, transaction, actor.Id, "PasswordChanged", actor.Id, Now);
        transaction.Commit();
    }
    public IReadOnlyList<AuditView> Audit(OperatorIdentity actor)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(); RequireAdmin(connection, transaction, actor);
        using var command = Command(connection, transaction, "SELECT sequence,actor_id,action,subject_id,recorded_at FROM security_audit ORDER BY sequence DESC LIMIT 200");
        using var reader = command.ExecuteReader(); var events = new List<AuditView>();
        while (reader.Read()) events.Add(new(reader.GetInt64(0), reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
            reader.GetString(2), reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)), DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture)));
        return events;
    }

    private static void InsertOperator(SqliteConnection connection, SqliteTransaction transaction, Guid id,
        string username, string displayName, OperatorRole role, string hash, bool mustChange) =>
        Execute(connection, transaction, "INSERT INTO operators(id,username,display_name,role,password_hash,active,must_change) VALUES($id,$username,$name,$role,$hash,1,$change)",
            ("$id", id.ToString()), ("$username", username), ("$name", displayName), ("$role", role.ToString()), ("$hash", hash), ("$change", mustChange ? 1 : 0));
    private static void AuditWrite(SqliteConnection connection, SqliteTransaction transaction, Guid? actor,
        string action, Guid? subject, DateTimeOffset time) => Execute(connection, transaction,
            "INSERT INTO security_audit(actor_id,action,subject_id,recorded_at) VALUES($actor,$action,$subject,$time)",
            ("$actor", actor?.ToString() ?? (object)DBNull.Value), ("$action", action),
            ("$subject", subject?.ToString() ?? (object)DBNull.Value), ("$time", Timestamp(time)));
    private static void RevokeSessions(SqliteConnection connection, SqliteTransaction transaction, Guid id) =>
        Execute(connection, transaction, "DELETE FROM sessions WHERE operator_id=$id", ("$id", id.ToString()));
    private static OperatorRow RequireOperator(SqliteConnection connection, SqliteTransaction transaction, OperatorIdentity actor)
    {
        var row = FindOperator(connection, transaction, "id", actor.Id.ToString());
        if (row is null || !row.Active || row.Role != actor.Role) throw new AccessDenied();
        return row;
    }
    private static void RequireAdmin(SqliteConnection connection, SqliteTransaction transaction, OperatorIdentity actor)
    {
        var row = RequireOperator(connection, transaction, actor);
        if (row.Role != OperatorRole.Admin || row.MustChange) throw new AccessDenied();
    }
    private static string Username(string value)
    {
        if (value is null || value.Length is < 3 or > 64 || !Regex.IsMatch(value, "\\A[a-zA-Z0-9._-]+\\z", RegexOptions.CultureInvariant))
            throw new RuleViolation("El usuario debe tener entre 3 y 64 letras ASCII, números, puntos, guiones o guiones bajos.");
        return value.ToUpperInvariant();
    }
    private static void ValidateDisplayName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100) throw new RuleViolation("Nombre inválido.");
    }
    private static void ValidatePassword(string value)
    {
        if (value is null || value.Length is < 15 or > 128 || string.IsNullOrWhiteSpace(value))
            throw new RuleViolation("La contraseña requiere entre 15 y 128 caracteres. Se permiten espacios.");
    }
    private static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private sealed record OperatorRow(Guid Id, string Username, string DisplayName, OperatorRole Role,
        string PasswordHash, bool Active, bool MustChange, int FailedAttempts, DateTimeOffset? LockedUntil)
    {
        public UserView View => new(Id, Username, DisplayName, Role.ToString(), Active, MustChange);
    }
    private static OperatorRow? FindOperator(SqliteConnection connection, SqliteTransaction transaction, string column, string value)
    {
        if (column is not ("id" or "username")) throw new ArgumentException("Columna inválida.");
        using var command = Command(connection, transaction, $"SELECT id,username,display_name,role,password_hash,active,must_change,failed_attempts,locked_until FROM operators WHERE {column}=$value", ("$value", value));
        using var reader = command.ExecuteReader();
        return !reader.Read() ? null : new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
            Parse<OperatorRole>(reader.GetString(3)), reader.GetString(4), reader.GetBoolean(5), reader.GetBoolean(6), reader.GetInt32(7),
            reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture));
    }
}
