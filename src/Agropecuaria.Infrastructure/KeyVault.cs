using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agropecuaria.Infrastructure;

public sealed record WrappedSecret(string Nonce, string Ciphertext, string Tag);
public sealed record KeyEnvelope(int Version, Guid KeyId, string Protection, string? Salt,
    int Iterations, WrappedSecret? Local, string? Dpapi, WrappedSecret Recovery);

// Standard AES-GCM envelope encryption. DB encryption itself is performed by SQLite3MC.
public static class KeyVault
{
    public const int Iterations = 600_000;
    private static readonly byte[] Purpose = "AgropecuariaCM/database-key/v1"u8.ToArray();

    public static byte[] Create(string path, string recoveryPath, string? passphrase, bool windowsDpapi = false)
    {
        if (Path.GetFullPath(path) == Path.GetFullPath(recoveryPath)) throw new ArgumentException("Separar clave de recuperación y sobre.");
        if (File.Exists(path) || File.Exists(recoveryPath)) throw new IOException("No se sobrescriben claves existentes.");
        var key = RandomNumberGenerator.GetBytes(32);
        var recovery = RandomNumberGenerator.GetBytes(32);
        try
        {
            var id = Guid.NewGuid();
            var envelope = BuildLocal(id, key, recovery, passphrase, windowsDpapi);
            WritePrivate(recoveryPath, Encoding.UTF8.GetBytes(Convert.ToBase64String(recovery)));
            // If this second write fails, preserve recovery material and fail; never create an unprotected DB.
            WritePrivate(path, JsonSerializer.SerializeToUtf8Bytes(envelope));
            return key;
        }
        catch { CryptographicOperations.ZeroMemory(key); throw; }
        finally { CryptographicOperations.ZeroMemory(recovery); }
    }

    public static byte[] Unlock(string path, string? passphrase)
    {
        var envelope = Read(path);
        if (envelope.Protection == "WindowsDpapiCurrentUser")
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Este sobre requiere la identidad Windows que lo creó.");
            return CheckLength(ProtectedData.Unprotect(Convert.FromBase64String(envelope.Dpapi!), Purpose, DataProtectionScope.CurrentUser));
        }
        if (envelope.Protection != "Passphrase") throw new CryptographicException("Protección desconocida.");
        ValidatePassphrase(passphrase);
        var wrappingKey = Rfc2898DeriveBytes.Pbkdf2(passphrase!, Convert.FromBase64String(envelope.Salt!), envelope.Iterations, HashAlgorithmName.SHA256, 32);
        try { return Unwrap(envelope.Local!, wrappingKey, envelope.KeyId, "local"); }
        finally { CryptographicOperations.ZeroMemory(wrappingKey); }
    }

    // Restore into a NEW envelope, protecting the same database key with a new local secret.
    public static void Recover(string envelopePath, string recoveryPath, string destinationPath,
        string? newPassphrase, bool windowsDpapi = false)
    {
        var envelope = Read(envelopePath);
        var recovery = Convert.FromBase64String(File.ReadAllText(recoveryPath).Trim());
        byte[]? key = null;
        try
        {
            key = Unwrap(envelope.Recovery, recovery, envelope.KeyId, "recovery");
            var restored = BuildLocal(envelope.KeyId, key, recovery, newPassphrase, windowsDpapi);
            WritePrivate(destinationPath, JsonSerializer.SerializeToUtf8Bytes(restored));
        }
        finally { if (key != null) CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(recovery); }
    }

    public static byte[] UnlockWithRecovery(string envelopePath, string recoveryPath)
    {
        var envelope = Read(envelopePath);
        var recovery = Convert.FromBase64String(File.ReadAllText(recoveryPath).Trim());
        try { return Unwrap(envelope.Recovery, recovery, envelope.KeyId, "recovery"); }
        finally { CryptographicOperations.ZeroMemory(recovery); }
    }

    public static void ExportRecoveryEnvelope(string envelopePath, string destinationPath)
    {
        var envelope = Read(envelopePath);
        // A backup never needs the host-specific local protection slot.
        WritePrivate(destinationPath, JsonSerializer.SerializeToUtf8Bytes(envelope with
        { Protection = "RecoveryOnly", Salt = null, Iterations = Iterations, Local = null, Dpapi = null }));
    }

    private static KeyEnvelope BuildLocal(Guid id, byte[] key, byte[] recovery, string? passphrase, bool windowsDpapi)
    {
        var recovered = Wrap(key, recovery, id, "recovery");
        if (windowsDpapi)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI requiere Windows.");
            return new(1, id, "WindowsDpapiCurrentUser", null, Iterations, null,
                Convert.ToBase64String(ProtectedData.Protect(key, Purpose, DataProtectionScope.CurrentUser)), recovered);
        }
        ValidatePassphrase(passphrase);
        var salt = RandomNumberGenerator.GetBytes(16);
        var wrappingKey = Rfc2898DeriveBytes.Pbkdf2(passphrase!, salt, Iterations, HashAlgorithmName.SHA256, 32);
        try { return new(1, id, "Passphrase", Convert.ToBase64String(salt), Iterations, Wrap(key, wrappingKey, id, "local"), null, recovered); }
        finally { CryptographicOperations.ZeroMemory(wrappingKey); }
    }

    private static WrappedSecret Wrap(byte[] value, byte[] wrappingKey, Guid id, string slot)
    {
        var nonce = RandomNumberGenerator.GetBytes(12); var ciphertext = new byte[value.Length]; var tag = new byte[16];
        using var aes = new AesGcm(wrappingKey, 16);
        aes.Encrypt(nonce, value, ciphertext, tag, Encoding.UTF8.GetBytes($"AgropecuariaCM/v1/{id:D}/{slot}"));
        return new(Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag));
    }
    private static byte[] Unwrap(WrappedSecret value, byte[] wrappingKey, Guid id, string slot)
    {
        var result = new byte[32];
        try
        {
            using var aes = new AesGcm(wrappingKey, 16);
            aes.Decrypt(Convert.FromBase64String(value.Nonce), Convert.FromBase64String(value.Ciphertext),
                Convert.FromBase64String(value.Tag), result, Encoding.UTF8.GetBytes($"AgropecuariaCM/v1/{id:D}/{slot}"));
            return result;
        }
        catch { CryptographicOperations.ZeroMemory(result); throw; }
    }
    private static KeyEnvelope Read(string path)
    {
        if (new FileInfo(path).Length > 16_384) throw new CryptographicException("Sobre inválido.");
        var envelope = JsonSerializer.Deserialize<KeyEnvelope>(File.ReadAllText(path)) ?? throw new CryptographicException("Sobre inválido.");
        if (envelope.Version != 1 || envelope.KeyId == Guid.Empty || envelope.Iterations != Iterations)
            throw new CryptographicException("Versión o parámetros de sobre no admitidos.");
        return envelope;
    }
    private static byte[] CheckLength(byte[] key) => key.Length == 32 ? key : throw new CryptographicException("Clave inválida.");
    private static void ValidatePassphrase(string? value)
    {
        if (value is null || value.Length < 20 || value.Length > 1024) throw new ArgumentException("La frase maestra requiere entre 20 y 1024 caracteres.");
    }
    private static void WritePrivate(string path, byte[] contents)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options); stream.Write(contents); stream.Flush(flushToDisk: true);
    }
}
