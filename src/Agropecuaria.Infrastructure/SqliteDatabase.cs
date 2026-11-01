using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Agropecuaria.Infrastructure;

public static class SqliteDatabase
{
    public static SqliteConnection Open(string path, byte[]? key = null, bool readOnly = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path), Pooling = false, DefaultTimeout = 10,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            if (key != null)
            {
                if (key.Length != 32) throw new CryptographicException("Clave de datos inválida.");
                // Only internally generated, fixed-length hex is interpolated. Never log this SQL.
                command.CommandText = $"PRAGMA cipher='chacha20'; PRAGMA key='raw:{Convert.ToHexString(key)}';";
                command.ExecuteNonQuery();
                command.CommandText = "SELECT sqlite3mc_version()";
                if (command.ExecuteScalar() is not string) throw new CryptographicException("Proveedor cifrado no disponible.");
            }
            command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA temp_store=MEMORY; SELECT count(*) FROM sqlite_master;";
            command.ExecuteScalar(); // Wrong/missing keys must fail before migrations or queries.
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    public static void Backup(string sourcePath, string destinationPath, byte[] key)
    {
        if (File.Exists(destinationPath)) throw new IOException("El respaldo de destino ya existe.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        using var source = Open(sourcePath, key, readOnly: true);
        // Atomically reserve a new destination; do not race another backup or overwrite a file.
        var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var reservation = new FileStream(destinationPath, fileOptions)) { }
        using var destination = Open(destinationPath, key);
        source.BackupDatabase(destination);
        // A restored backup must never revive captured bearer sessions.
        using (var revoke = destination.CreateCommand())
        {
            revoke.CommandText = "DELETE FROM sessions;"; revoke.ExecuteNonQuery();
            revoke.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='replication_state'";
            if ((long)revoke.ExecuteScalar()! == 1)
            {
                revoke.CommandText = "UPDATE replication_state SET credential=NULL,can_write=0,ready=0,registration_pending=0 WHERE credential IS NOT NULL OR registration_pending=1";
                revoke.ExecuteNonQuery();
            }
        }
        using var check = destination.CreateCommand(); check.CommandText = "PRAGMA integrity_check;";
        if ((string?)check.ExecuteScalar() != "ok") throw new IOException("Respaldo no verificado.");
    }
}
