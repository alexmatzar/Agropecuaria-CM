using System.Globalization;
using System.Security.Cryptography;
using Agropecuaria.Application;
using Agropecuaria.Contracts;
using Agropecuaria.Domain;
using Microsoft.Data.Sqlite;

namespace Agropecuaria.Infrastructure;

public sealed partial class SqlitePosStore
{
    private static void InitializeCloudSecurity(SqliteConnection connection, SqliteTransaction transaction) => Execute(connection, transaction, """
        ALTER TABLE operators ADD COLUMN firebase_uid TEXT;
        ALTER TABLE operators ADD COLUMN cloud_version INTEGER NOT NULL DEFAULT 0;
        CREATE UNIQUE INDEX ix_operators_firebase ON operators(firebase_uid) WHERE firebase_uid IS NOT NULL;
        CREATE TABLE cloud_sessions (
            token_hash TEXT PRIMARY KEY REFERENCES sessions(token_hash) ON DELETE CASCADE,
            uid TEXT NOT NULL, refresh_token TEXT NOT NULL, last_validated_at TEXT NOT NULL
        ) STRICT;
        PRAGMA user_version=3;
        """);

    public LoginResult BeginCloudSession(CloudGrant grant, string refreshToken)
    {
        ValidateGrant(grant);
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        CacheGrant(connection, transaction, grant);
        // One interactive operator per terminal: only a successful online login switches the account.
        Execute(connection, transaction, "DELETE FROM sessions");
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); var hash = TokenHash(token);
        Execute(connection, transaction, "INSERT INTO sessions VALUES($token,$user,$expiry,$expiry)",
            ("$token", hash), ("$user", grant.User.Id.ToString()), ("$expiry", Timestamp(grant.OfflineUntilUtc)));
        Execute(connection, transaction, "INSERT INTO cloud_sessions VALUES($hash,$uid,$refresh,$validated)",
            ("$hash", hash), ("$uid", grant.FirebaseUid), ("$refresh", refreshToken), ("$validated", Timestamp(grant.ValidatedAtUtc)));
        AuditWrite(connection, transaction, grant.User.Id, "CloudLoginSucceeded", grant.User.Id, Now);
        transaction.Commit(); return new(token, grant.OfflineUntilUtc, grant.User);
    }
    public OperatorIdentity? AuthenticateCloudSession(string token)
    {
        if (token.Length is < 40 or > 100) return null;
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: true);
        var id = Scalar(connection, transaction, """
            SELECT s.operator_id FROM sessions s JOIN cloud_sessions c ON c.token_hash=s.token_hash
            WHERE s.token_hash=$hash AND s.expires_at>$now
            """, ("$hash", TokenHash(token)), ("$now", Timestamp(Now))) as string;
        if (id == null) return null;
        var row = FindOperator(connection, transaction, "id", id);
        return row is not { Active: true } ? null : new(row.Id, row.Username, row.DisplayName, row.Role, row.MustChange);
    }
    public CachedCloudSession? GetCloudSession(string token)
    {
        if (AuthenticateCloudSession(token) == null) return null;
        return ActiveCloudSessions().SingleOrDefault(s => s.TokenHash == TokenHash(token));
    }
    public IReadOnlyList<CachedCloudSession> ActiveCloudSessions()
    {
        using var connection = Open(); using var command = Command(connection, null, """
            SELECT c.token_hash,c.uid,c.refresh_token FROM cloud_sessions c JOIN sessions s ON s.token_hash=c.token_hash
            WHERE s.expires_at>$now
            """, ("$now", Timestamp(Now)));
        using var reader = command.ExecuteReader(); var result = new List<CachedCloudSession>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }
    public void RenewCloudSession(CachedCloudSession session, CloudGrant grant, string refreshToken)
    {
        ValidateGrant(grant);
        if (session.Uid != grant.FirebaseUid) throw new CloudIdentityRejected();
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        if (Scalar(connection, transaction, "SELECT token_hash FROM cloud_sessions WHERE token_hash=$hash", ("$hash", session.TokenHash)) == null) return;
        CacheGrant(connection, transaction, grant);
        Execute(connection, transaction, "UPDATE sessions SET expires_at=$expires,idle_expires_at=$expires WHERE token_hash=$hash",
            ("$expires", Timestamp(grant.OfflineUntilUtc)), ("$hash", session.TokenHash));
        Execute(connection, transaction, "UPDATE cloud_sessions SET refresh_token=$refresh,last_validated_at=$validated WHERE token_hash=$hash",
            ("$refresh", refreshToken), ("$validated", Timestamp(grant.ValidatedAtUtc)), ("$hash", session.TokenHash));
        transaction.Commit();
    }
    public void RevokeCloudUser(Guid userId)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        RevokeSessions(connection, transaction, userId); AuditWrite(connection, transaction, null, "CloudSessionRevoked", userId, Now);
        transaction.Commit();
    }
    public void RevokeCloudSession(CachedCloudSession session)
    {
        using var connection = Open(); Execute(connection, null, "DELETE FROM sessions WHERE token_hash=$hash", ("$hash", session.TokenHash));
    }
    private void ValidateGrant(CloudGrant grant)
    {
        if (grant.TenantId != identity.TenantId || grant.StoreId != identity.StoreId || string.IsNullOrWhiteSpace(grant.FirebaseUid) ||
            grant.User.Id == Guid.Empty || !grant.User.IsActive || grant.MembershipVersion < 1 ||
            grant.OfflineUntilUtc <= Now || grant.ValidatedAtUtc > Now.AddMinutes(2) ||
            grant.ValidatedAtUtc < Now.AddMinutes(-2) || grant.OfflineUntilUtc > grant.ValidatedAtUtc.AddHours(24)) throw new CloudIdentityRejected();
        Parse<OperatorRole>(grant.User.Role);
    }
    private static void CacheGrant(SqliteConnection connection, SqliteTransaction transaction, CloudGrant grant)
    {
        var savedUid = Scalar(connection, transaction, "SELECT firebase_uid FROM operators WHERE id=$id", ("$id", grant.User.Id.ToString()));
        if (savedUid != null && (savedUid is not string || (string)savedUid != grant.FirebaseUid)) throw new CloudIdentityRejected();
        var current = Scalar(connection, transaction, "SELECT cloud_version FROM operators WHERE firebase_uid=$uid", ("$uid", grant.FirebaseUid));
        if (current is long version && version > grant.MembershipVersion) throw new CloudIdentityRejected();
        Execute(connection, transaction, """
            INSERT INTO operators(id,username,display_name,role,password_hash,active,must_change,firebase_uid,cloud_version)
            VALUES($id,$username,$name,$role,'',1,$change,$uid,$version)
            ON CONFLICT(id) DO UPDATE SET username=$username,display_name=$name,role=$role,active=1,must_change=$change,cloud_version=$version
            """, ("$id", grant.User.Id.ToString()), ("$username", grant.User.Username), ("$name", grant.User.DisplayName),
            ("$role", grant.User.Role), ("$change", grant.User.MustChangePassword ? 1 : 0), ("$uid", grant.FirebaseUid), ("$version", grant.MembershipVersion));
    }
}
