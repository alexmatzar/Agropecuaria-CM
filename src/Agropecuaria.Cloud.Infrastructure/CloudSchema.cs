using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Agropecuaria.Cloud.Infrastructure;

public static class CloudSchema
{
    public static void Migrate(DbConnection connection)
    {
        using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT pg_advisory_xact_lock(8437567291024);"; command.ExecuteNonQuery();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS cloud_schema_migrations (
                version INTEGER PRIMARY KEY, sha256 TEXT NOT NULL, applied_at TIMESTAMPTZ NOT NULL DEFAULT now());
            """;
        command.ExecuteNonQuery();
        var assembly = typeof(CloudSchema).Assembly;
        var migrations = assembly.GetManifestResourceNames().Where(n => n.Contains(".Migrations.", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal)).Order().ToArray();
        command.CommandText = "SELECT COALESCE(MAX(version),0) FROM cloud_schema_migrations";
        if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > migrations.Length)
            throw new InvalidOperationException("Esquema cloud más reciente que este servidor.");
        foreach (var resource in migrations)
        {
            var filename = resource[(resource.IndexOf(".Migrations.", StringComparison.Ordinal) + 12)..];
            var version = int.Parse(filename[..filename.IndexOf('_')], CultureInfo.InvariantCulture);
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
            command.Parameters.Clear(); command.CommandText = "SELECT sha256 FROM cloud_schema_migrations WHERE version=@version";
            var parameter = command.CreateParameter(); parameter.ParameterName = "version"; parameter.Value = version; command.Parameters.Add(parameter);
            if (command.ExecuteScalar() is string applied)
            {
                if (applied != hash) throw new InvalidOperationException("Migración aplicada alterada. Añada una nueva migración.");
                continue;
            }
            command.Parameters.Clear(); command.CommandText = sql; command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO cloud_schema_migrations(version,sha256) VALUES(@version,@hash)";
            parameter = command.CreateParameter(); parameter.ParameterName = "version"; parameter.Value = version; command.Parameters.Add(parameter);
            parameter = command.CreateParameter(); parameter.ParameterName = "hash"; parameter.Value = hash; command.Parameters.Add(parameter);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}
