using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agropecuaria.Application;
using Agropecuaria.Contracts;
using Agropecuaria.Domain;
using Microsoft.Data.Sqlite;

namespace Agropecuaria.Infrastructure;

// A durable test receiver, not a deployable multi-tenant cloud backend.
public sealed class DevelopmentInbox(string path, InstallationIdentity identity)
{
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open(); return connection;
    }
    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS inbox (
                event_id TEXT PRIMARY KEY, device_id TEXT NOT NULL, sequence INTEGER NOT NULL,
                hash TEXT NOT NULL, envelope_json TEXT NOT NULL, UNIQUE(device_id,sequence)
            ) STRICT;
            """;
        command.ExecuteNonQuery();
    }
    public SyncAcknowledgement Accept(SyncEnvelope envelope)
    {
        if (envelope.TenantId != identity.TenantId || envelope.StoreId != identity.StoreId || envelope.DeviceId != identity.DeviceId)
            throw new RuleViolation("Evento ajeno a esta instalación.");
        if (envelope.EventId == Guid.Empty || envelope.SchemaVersion != 1 || envelope.Sequence <= 0 ||
            envelope.Kind is not ("ProductRegistered" or "SaleCompleted") || string.IsNullOrWhiteSpace(envelope.Payload))
            throw new RuleViolation("Evento inválido.");
        using var jsonDocument = JsonDocument.Parse(envelope.Payload);
        var json = JsonSerializer.Serialize(envelope);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT hash FROM inbox WHERE event_id=$id";
        command.Parameters.AddWithValue("$id", envelope.EventId.ToString());
        if (command.ExecuteScalar() is string saved)
        {
            if (saved != hash) throw new OperationConflict("EventId con contenido diferente.");
            return new SyncAcknowledgement(envelope.EventId);
        }
        command.CommandText = "SELECT COALESCE(MAX(sequence),0)+1 FROM inbox WHERE device_id=$device";
        command.Parameters.AddWithValue("$device", envelope.DeviceId.ToString());
        var expected = (long)command.ExecuteScalar()!;
        if (envelope.Sequence != expected) throw new OperationConflict("Secuencia fuera de orden.");
        command.CommandText = "INSERT INTO inbox VALUES($id,$device,$sequence,$hash,$json)";
        command.Parameters.AddWithValue("$sequence", envelope.Sequence);
        command.Parameters.AddWithValue("$hash", hash); command.Parameters.AddWithValue("$json", json);
        command.ExecuteNonQuery(); transaction.Commit();
        return new SyncAcknowledgement(envelope.EventId);
    }
    public long Count()
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM inbox"; return (long)command.ExecuteScalar()!;
    }
}
