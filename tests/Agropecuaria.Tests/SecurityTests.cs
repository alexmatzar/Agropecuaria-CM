using System.Security.Cryptography;
using System.Text;
using Agropecuaria.Application;
using Agropecuaria.Contracts;
using Agropecuaria.Domain;
using Agropecuaria.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Agropecuaria.Tests;

public sealed class SecurityTests : IDisposable
{
    private const string Password = "A long example password 123!";
    private const string NewPassword = "A different private password 456!";
    private const string Master = "A master passphrase used only for tests";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "agropecuaria-security-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    private readonly InstallationIdentity identity = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "GTQ");
    private readonly TestClock clock = new();
    private SqlitePosStore Store() { var store = new SqlitePosStore(Path.Combine(directory, "secure.db"), identity, key, clock); store.Initialize(); return store; }
    private (SqlitePosStore Store, OperatorIdentity Admin) Admin()
    {
        var store = Store(); store.BootstrapAdministrator("owner", "Propietario", Password);
        return (store, store.Authenticate(store.Login(new("owner", Password)).AccessToken)!);
    }

    [Fact]
    public void EncryptedDatabaseRejectsMissingOrWrongKeyAndHidesBusinessText()
    {
        var store = Store();
        using var keeper = SqliteDatabase.Open(Path.Combine(directory, "secure.db"), key);
        store.CreateProduct(new(Guid.NewGuid(), Guid.NewGuid(), "SensitiveBusinessMarker987", "Count", "Piece", 5, [new("Piece", 100)]));
        Assert.True(new FileInfo(Path.Combine(directory, "secure.db-wal")).Length > 0);
        Assert.Throws<SqliteException>(() => SqliteDatabase.Open(Path.Combine(directory, "secure.db")));
        Assert.Throws<SqliteException>(() => SqliteDatabase.Open(Path.Combine(directory, "secure.db"), RandomNumberGenerator.GetBytes(32)));
        foreach (var file in Directory.GetFiles(directory, "secure.db*"))
        {
            // SQLite keeps write handles open for DB/WAL. Raw inspection must allow
            // the same sharing mode on Windows while preserving the live WAL check.
            using var raw = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var bytes = new MemoryStream(); raw.CopyTo(bytes);
            Assert.DoesNotContain("SensitiveBusinessMarker987", Encoding.UTF8.GetString(bytes.ToArray()));
        }
        Assert.Equal(5, Assert.Single(Store().ListProducts()).StockBase);
    }
    [Fact]
    public void MasterPassphraseAndRecoveryUnlockSameKeyOnAnotherInstallation()
    {
        Directory.CreateDirectory(directory);
        var envelope = Path.Combine(directory, "keyring.json"); var recovery = Path.Combine(directory, "offline.key");
        var original = KeyVault.Create(envelope, recovery, Master);
        try
        {
            Assert.Equal(original, KeyVault.Unlock(envelope, Master));
            Assert.ThrowsAny<CryptographicException>(() => KeyVault.Unlock(envelope, Master + "wrong"));
            var copy = Path.Combine(directory, "backup-envelope.json"); KeyVault.ExportRecoveryEnvelope(envelope, copy);
            var restored = Path.Combine(directory, "restored.json"); KeyVault.Recover(copy, recovery, restored, Master + "new");
            Assert.Equal(original, KeyVault.Unlock(restored, Master + "new"));
            Assert.ThrowsAny<CryptographicException>(() => KeyVault.Unlock(restored, Master));
            Assert.DoesNotContain(Convert.ToBase64String(original), File.ReadAllText(envelope));
            Assert.DoesNotContain(Master, File.ReadAllText(envelope));
        }
        finally { CryptographicOperations.ZeroMemory(original); }
    }
    [Fact]
    public void BackupIsEncryptedAndRestoresOperatorsAndInventoryWithRecoveredKey()
    {
        var envelope = Path.Combine(directory, "keyring.json"); var recovery = Path.Combine(directory, "offline.key");
        var secret = KeyVault.Create(envelope, recovery, Master);
        try
        {
            var source = Path.Combine(directory, "original.db"); var store = new SqlitePosStore(source, identity, secret); store.Initialize();
            store.BootstrapAdministrator("owner", "Owner", Password);
            var oldSession = store.Login(new("owner", Password));
            store.CreateProduct(new(Guid.NewGuid(), Guid.NewGuid(), "ProtectedInventory", "Weight", "Quintal", 5, [new("Pound", 100)]));
            var backup = Path.Combine(directory, "backup.db"); SqliteDatabase.Backup(source, backup, secret);
            Assert.Throws<SqliteException>(() => SqliteDatabase.Open(backup));
            var restoredEnvelope = Path.Combine(directory, "restored.json"); KeyVault.Recover(envelope, recovery, restoredEnvelope, Master + "new");
            var restoredKey = KeyVault.Unlock(restoredEnvelope, Master + "new");
            var restored = new SqlitePosStore(backup, identity, restoredKey); restored.Initialize();
            Assert.Null(restored.Authenticate(oldSession.AccessToken));
            Assert.Equal("Admin", restored.Login(new("owner", Password)).User.Role);
            Assert.Equal(500_000_000, Assert.Single(restored.ListProducts()).StockBase);
            CryptographicOperations.ZeroMemory(restoredKey);
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
    [Fact]
    public void TamperedRecoveryEnvelopeFailsAuthentication()
    {
        var envelope = Path.Combine(directory, "keyring.json"); var recovery = Path.Combine(directory, "offline.key");
        KeyVault.Create(envelope, recovery, Master);
        var json = System.Text.Json.JsonSerializer.Deserialize<KeyEnvelope>(File.ReadAllText(envelope))!;
        var tag = Convert.FromBase64String(json.Recovery.Tag); tag[0] ^= 1;
        File.WriteAllText(envelope, System.Text.Json.JsonSerializer.Serialize(json with { Recovery = json.Recovery with { Tag = Convert.ToBase64String(tag) } }));
        Assert.ThrowsAny<CryptographicException>(() => KeyVault.Recover(envelope, recovery, Path.Combine(directory, "invalid.json"), Master));
    }
    [Fact]
    public void BackupNeverClonesAnEnrolledDeviceCredentialOrWritableAuthority()
    {
        var store = Store(); var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        store.EnrollDevice(new(identity.DeviceId, secret, true, 0));
        var product = store.CreateProduct(new(Guid.NewGuid(), Guid.NewGuid(), "Protected", "Count", "Piece", 3, [new("Piece", 100)]));
        var backup = Path.Combine(directory, "device-backup.db"); SqliteDatabase.Backup(Path.Combine(directory, "secure.db"), backup, key);
        var restored = new SqlitePosStore(backup, identity, key); restored.Initialize();
        Assert.Null(restored.Credential()); Assert.False(restored.IsReady()); Assert.Equal(secret, store.Credential()!.Secret);
        Assert.Throws<OperationConflict>(() => restored.CompleteSale(new(Guid.NewGuid(), [new(product.Id, "Piece", 1)])));
        Assert.Equal(3, Assert.Single(restored.ListProducts()).StockBase);
    }
    [Fact]
    public void BootstrapCannotBeRepeatedAndLastAdminCannotBeRemoved()
    {
        var (store, admin) = Admin();
        Assert.Throws<OperationConflict>(() => store.BootstrapAdministrator("another", "Other", Password));
        Assert.Throws<OperationConflict>(() => store.UpdateUser(admin, admin.Id, new("Viewer", true)));
        Assert.Throws<OperationConflict>(() => store.UpdateUser(admin, admin.Id, new("Admin", false)));
    }
    [Fact]
    public void PasswordsAndTokensAreNotStoredInPlaintext()
    {
        var (store, admin) = Admin(); var login = store.Login(new("owner", Password));
        using var connection = SqliteDatabase.Open(Path.Combine(directory, "secure.db"), key);
        using var command = connection.CreateCommand(); command.CommandText = "SELECT password_hash FROM operators";
        Assert.NotEqual(Password, command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM sessions WHERE token_hash=$hash";
        command.Parameters.AddWithValue("$hash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(login.AccessToken))));
        Assert.Equal(1L, command.ExecuteScalar());
        command.Parameters.Clear(); command.CommandText = "SELECT COUNT(*) FROM sessions WHERE token_hash=$token";
        command.Parameters.AddWithValue("$token", login.AccessToken); Assert.Equal(0L, command.ExecuteScalar());
        Assert.DoesNotContain(store.Audit(admin), row => row.Action.Contains(Password, StringComparison.Ordinal));
    }
    [Fact]
    public void RoleUpdatesRevokeEverySessionImmediately()
    {
        var (store, admin) = Admin(); var user = store.CreateUser(admin, new("cashier", "Cashier", "Cashier", Password));
        var login = store.Login(new("cashier", Password));
        store.UpdateUser(admin, user.Id, new("Viewer", true));
        Assert.Null(store.Authenticate(login.AccessToken));
        Assert.Equal("Viewer", store.Login(new("cashier", Password)).User.Role);
    }
    [Fact]
    public void TemporaryPasswordRequiresChangeAndChangeRevokesAllSessions()
    {
        var (store, admin) = Admin(); store.CreateUser(admin, new("cashier", "Cashier", "Cashier", Password));
        var login = store.Login(new("cashier", Password)); var actor = store.Authenticate(login.AccessToken)!;
        Assert.True(actor.MustChangePassword);
        Assert.Throws<AccessDenied>(() => store.ListUsers(actor));
        store.ChangePassword(actor, new(Password, NewPassword)); Assert.Null(store.Authenticate(login.AccessToken));
        Assert.False(store.Login(new("cashier", NewPassword)).User.MustChangePassword);
        Assert.Throws<LoginRejected>(() => store.Login(new("cashier", Password)));
    }
    [Fact]
    public void DisableAndPasswordResetRevokeSessions()
    {
        var (store, admin) = Admin(); var user = store.CreateUser(admin, new("cashier", "Cashier", "Cashier", Password));
        var login = store.Login(new("cashier", Password)); store.ResetPassword(admin, user.Id, NewPassword);
        Assert.Null(store.Authenticate(login.AccessToken));
        var reset = store.Login(new("cashier", NewPassword)); Assert.True(reset.User.MustChangePassword);
        store.UpdateUser(admin, user.Id, new("Cashier", false));
        Assert.Null(store.Authenticate(reset.AccessToken)); Assert.Throws<LoginRejected>(() => store.Login(new("cashier", NewPassword)));
    }
    [Fact]
    public void FailedLoginLockSurvivesRestartAndExpires()
    {
        var (store, _) = Admin();
        for (int i = 0; i < 5; i++) Assert.Throws<LoginRejected>(() => store.Login(new("owner", "not the password")));
        Assert.Throws<LoginRejected>(() => Store().Login(new("owner", Password)));
        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal("Admin", Store().Login(new("owner", Password)).User.Role);
    }
    [Fact]
    public void SessionExpiresWhenIdleAndLogoutRevokes()
    {
        var (store, _) = Admin(); var login = store.Login(new("owner", Password));
        clock.Advance(TimeSpan.FromMinutes(31)); Assert.Null(store.Authenticate(login.AccessToken));
        login = store.Login(new("owner", Password)); store.Logout(login.AccessToken); Assert.Null(store.Authenticate(login.AccessToken));
    }
    [Fact]
    public void CashierCannotCreateOrPromoteUsersEvenThroughApplicationPort()
    {
        var (store, admin) = Admin(); store.CreateUser(admin, new("cashier", "Cashier", "Cashier", Password));
        var login = store.Login(new("cashier", Password)); var actor = store.Authenticate(login.AccessToken)!;
        store.ChangePassword(actor, new(Password, NewPassword)); actor = store.Authenticate(store.Login(new("cashier", NewPassword)).AccessToken)!;
        Assert.Throws<AccessDenied>(() => store.CreateUser(actor, new("hacker", "Hacker", "Admin", Password)));
        Assert.Throws<AccessDenied>(() => store.UpdateUser(actor, actor.Id, new("Admin", true)));
    }
    [Fact]
    public void AbsoluteSessionLifetimeCannotBeExtendedByActivity()
    {
        var (store, _) = Admin(); var login = store.Login(new("owner", Password));
        for (int i = 0; i < 23; i++) { clock.Advance(TimeSpan.FromMinutes(20)); Assert.NotNull(store.Authenticate(login.AccessToken)); }
        clock.Advance(TimeSpan.FromMinutes(21)); Assert.Null(store.Authenticate(login.AccessToken));
    }
    [Fact]
    public void OfflineAdminRecoveryRequiresKeyAndRevokesAllSessions()
    {
        var envelope = Path.Combine(directory, "keyring.json"); var recovery = Path.Combine(directory, "offline.key");
        var secret = KeyVault.Create(envelope, recovery, Master);
        try
        {
            var store = new SqlitePosStore(Path.Combine(directory, "restorable.db"), identity, secret); store.Initialize();
            store.BootstrapAdministrator("owner", "Owner", Password); var login = store.Login(new("owner", Password));
            var recoveredKey = KeyVault.UnlockWithRecovery(envelope, recovery);
            var recovered = new SqlitePosStore(Path.Combine(directory, "restorable.db"), identity, recoveredKey); recovered.Initialize();
            recovered.RecoverAdministrator("owner", NewPassword);
            Assert.Null(store.Authenticate(login.AccessToken));
            Assert.Throws<LoginRejected>(() => store.Login(new("owner", Password)));
            Assert.True(store.Login(new("owner", NewPassword)).User.MustChangePassword);
            CryptographicOperations.ZeroMemory(recoveredKey);
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
    [Fact]
    public void VersionOneMigrationPreservesInventory()
    {
        var store = Store(); store.CreateProduct(new(Guid.NewGuid(), Guid.NewGuid(), "Existing", "Count", "Piece", 7, [new("Piece", 100)]));
        using (var connection = SqliteDatabase.Open(Path.Combine(directory, "secure.db"), key))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TABLE product_details; DROP TABLE replication_state; DROP TABLE replication_inbox; DROP TABLE legacy_records; DROP TABLE legacy_blobs; ALTER TABLE sales DROP COLUMN source_operator_id; DROP TABLE cloud_sessions; ALTER TABLE sales DROP COLUMN operator_id; DROP TABLE sessions; DROP TABLE operators; DROP TABLE security_audit; PRAGMA user_version=1;";
            command.ExecuteNonQuery();
        }
        var migrated = Store(); Assert.Equal(7, Assert.Single(migrated.ListProducts()).StockBase);
        migrated.BootstrapAdministrator("owner", "Owner", Password);
        Assert.Equal("Admin", migrated.Login(new("owner", Password)).User.Role);
    }
    [Fact]
    public void RecoveryAndProvisioningNeverOverwriteExistingSecrets()
    {
        var envelope = Path.Combine(directory, "keyring.json"); var recovery = Path.Combine(directory, "offline.key");
        KeyVault.Create(envelope, recovery, Master); var before = File.ReadAllBytes(envelope);
        Assert.Throws<IOException>(() => KeyVault.Create(envelope, recovery, Master + "other"));
        Assert.Throws<IOException>(() => KeyVault.Recover(envelope, recovery, envelope, Master + "other"));
        Assert.Equal(before, File.ReadAllBytes(envelope));
    }
    public void Dispose() { CryptographicOperations.ZeroMemory(key); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }
}
