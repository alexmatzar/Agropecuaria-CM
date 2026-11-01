using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Agropecuaria.Application;
using Agropecuaria.Contracts;
using Agropecuaria.Domain;
using Microsoft.Data.Sqlite;

namespace Agropecuaria.Infrastructure;

public sealed partial class SqlitePosStore : IReplicationStore
{
    private static void InitializeReplication(SqliteConnection connection, SqliteTransaction transaction) =>
        Execute(connection, transaction, """
            CREATE TABLE product_details (
                product_id TEXT PRIMARY KEY REFERENCES products(id), barcode TEXT UNIQUE,
                type TEXT, brand TEXT, description TEXT
            ) STRICT;
            CREATE TABLE replication_state (
                singleton INTEGER PRIMARY KEY CHECK(singleton=1), cloud_cursor INTEGER NOT NULL CHECK(cloud_cursor>=0),
                credential TEXT, can_write INTEGER NOT NULL CHECK(can_write IN (0,1)),
                ready INTEGER NOT NULL CHECK(ready IN (0,1)),
                registration_pending INTEGER NOT NULL CHECK(registration_pending IN (0,1)),
                replace_writer INTEGER NOT NULL CHECK(replace_writer IN (0,1))
            ) STRICT;
            INSERT INTO replication_state VALUES(1,0,NULL,1,1,0,0);
            CREATE TABLE replication_inbox (
                cursor INTEGER PRIMARY KEY CHECK(cursor>0), event_id TEXT NOT NULL UNIQUE, content_hash TEXT NOT NULL
            ) STRICT;
            CREATE TABLE legacy_records (
                source_hash TEXT NOT NULL, table_name TEXT NOT NULL, row_key TEXT NOT NULL, record_json TEXT NOT NULL,
                PRIMARY KEY(source_hash,table_name,row_key)
            ) STRICT;
            CREATE TABLE legacy_blobs (
                source_hash TEXT NOT NULL, table_name TEXT NOT NULL, row_key TEXT NOT NULL, column_name TEXT NOT NULL,
                content_hash TEXT NOT NULL, bytes BLOB NOT NULL,
                PRIMARY KEY(source_hash,table_name,row_key,column_name)
            ) STRICT;
            ALTER TABLE sales ADD COLUMN source_operator_id TEXT;
            UPDATE sales SET source_operator_id=operator_id;
            PRAGMA user_version=4;
            """);

    public ReplicationCredential? Credential()
    {
        using var connection = Open(); using var command = Command(connection, null, "SELECT credential,can_write,registration_pending FROM replication_state WHERE singleton=1");
        using var reader = command.ExecuteReader(); reader.Read();
        return reader.IsDBNull(0) || reader.GetInt64(2) == 1 ? null : new(reader.GetString(0), reader.GetInt64(1) == 1);
    }
    public bool EnrollmentPending()
    { using var connection = Open(); return (long)Scalar(connection, null, "SELECT registration_pending FROM replication_state WHERE singleton=1")! == 1; }
    public bool IsReady()
    { using var connection = Open(); return (long)Scalar(connection, null, "SELECT ready FROM replication_state WHERE singleton=1")! == 1; }
    public RegisterDeviceRequest PrepareEnrollment(bool canWrite, bool replaceWriter)
    {
        if (replaceWriter && !canWrite) throw new RuleViolation("Reemplazo inválido.");
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        string? secret; long pending, savedWrite, savedReplace;
        using (var command = Command(connection, transaction, "SELECT credential,registration_pending,can_write,replace_writer FROM replication_state WHERE singleton=1"))
        using (var reader = command.ExecuteReader())
        { reader.Read(); secret = reader.IsDBNull(0) ? null : reader.GetString(0); pending = reader.GetInt64(1); savedWrite = reader.GetInt64(2); savedReplace = reader.GetInt64(3); }
        if (secret != null && (pending == 0 || savedWrite != (canWrite ? 1 : 0) || savedReplace != (replaceWriter ? 1 : 0)))
            throw new OperationConflict("Registro ya completado o pendiente con opciones diferentes.");
        if (!canWrite && HasBusinessData(connection, transaction)) throw new OperationConflict("Una réplica de consulta debe comenzar vacía.");
        secret ??= Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Execute(connection, transaction, "UPDATE replication_state SET credential=$secret,can_write=$write,ready=0,registration_pending=1,replace_writer=$replace WHERE singleton=1",
            ("$secret", secret), ("$write", canWrite ? 1 : 0), ("$replace", replaceWriter ? 1 : 0));
        var request = new RegisterDeviceRequest(identity.DeviceId, canWrite, replaceWriter, HasBusinessData(connection, transaction), secret);
        transaction.Commit(); return request;
    }
    public bool HasBusinessData()
    {
        using var connection = Open();
        return HasBusinessData(connection, null);
    }
    private static bool HasBusinessData(SqliteConnection connection, SqliteTransaction? transaction) =>
        (long)Scalar(connection, transaction, "SELECT (SELECT COUNT(*) FROM products)+(SELECT COUNT(*) FROM operations)+(SELECT COUNT(*) FROM sales)+(SELECT COUNT(*) FROM legacy_records)")! != 0;

    public void EnrollDevice(RegisteredDevice device)
    {
        byte[] secret;
        if (device.Secret is not { Length: 44 }) throw new RuleViolation("Credencial de dispositivo inválida.");
        try { secret = Convert.FromBase64String(device.Secret); } catch (FormatException) { throw new RuleViolation("Credencial de dispositivo inválida."); }
        if (device.DeviceId != identity.DeviceId || secret.Length != 32 || device.HeadCursor < 0) throw new RuleViolation("Registro de dispositivo inválido.");
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        var existing = Scalar(connection, transaction, "SELECT credential FROM replication_state WHERE singleton=1") as string;
        if (existing != null && (existing != device.Secret || (long)Scalar(connection, transaction, "SELECT registration_pending FROM replication_state WHERE singleton=1")! != 1))
            throw new OperationConflict("Esta instalación ya tiene una credencial diferente o un registro completado.");
        if (device.HeadCursor > 0 && HasBusinessData(connection, transaction))
            throw new OperationConflict("No se mezcla la réplica cloud con datos de otra instalación.");
        Execute(connection, transaction, "UPDATE replication_state SET credential=$secret,can_write=$write,ready=$ready,registration_pending=0 WHERE singleton=1",
            ("$secret", device.Secret), ("$write", device.CanWrite ? 1 : 0), ("$ready", device.HeadCursor == 0 ? 1 : 0));
        transaction.Commit();
    }
    public long CloudCursor()
    {
        using var connection = Open(); return (long)Scalar(connection, null, "SELECT cloud_cursor FROM replication_state WHERE singleton=1")!;
    }
    public void SuspendWrites()
    { using var connection = Open(); Execute(connection, null, "UPDATE replication_state SET can_write=0,ready=0 WHERE singleton=1"); }
    public void ApplyPage(CloudChangePage page)
    {
        if (page.Changes is null || page.Changes.Count > 100 || page.FromCursor < 0 || page.ThroughCursor < page.FromCursor ||
            page.HeadCursor < page.ThroughCursor || page.ThroughCursor - page.FromCursor != page.Changes.Count ||
            page.Changes.Count == 0 && page.HeadCursor != page.ThroughCursor) throw new ReplicationRejected();
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        var cursor = (long)Scalar(connection, transaction, "SELECT cloud_cursor FROM replication_state WHERE singleton=1")!;
        if (Scalar(connection, transaction, "SELECT credential FROM replication_state WHERE singleton=1") is not string) throw new ReplicationRejected();
        if (page.FromCursor != cursor)
        {
            if (page.ThroughCursor > cursor || page.Changes.Any(c => c?.Envelope == null ||
                !Equals(Scalar(connection, transaction, "SELECT content_hash FROM replication_inbox WHERE cursor=$cursor AND event_id=$id",
                    ("$cursor", c.Cursor), ("$id", c.Envelope.EventId.ToString())), Convert.ToHexString(BusinessEvents.Hash(c.Envelope))))) throw new ReplicationRejected();
            return;
        }
        foreach (var change in page.Changes)
        {
            if (change == null || change.Cursor != checked(cursor + 1)) throw new ReplicationRejected();
            var envelope = change.Envelope;
            BusinessEvents.ValidateEnvelope(envelope);
            if (envelope.TenantId != identity.TenantId || envelope.StoreId != identity.StoreId) throw new ReplicationRejected();
            var hash = Convert.ToHexString(BusinessEvents.Hash(envelope));
            var local = LocalEnvelope(connection, transaction, envelope.EventId);
            if (local != null)
            {
                if (Convert.ToHexString(BusinessEvents.Hash(local)) != hash) throw new ReplicationRejected();
                // This feed is a persisted server receipt, including the exact original envelope.
                Execute(connection, transaction, "UPDATE outbox SET acknowledged_at=COALESCE(acknowledged_at,$now) WHERE event_id=$id",
                    ("$now", Timestamp(Now)), ("$id", envelope.EventId.ToString()));
            }
            else
            {
                if (Scalar(connection, transaction, "SELECT id FROM operations WHERE id=$id", ("$id", envelope.EventId.ToString())) != null) throw new ReplicationRejected();
                object result = envelope.Kind switch
                {
                    "ProductRegistered" => ReplayProduct(connection, transaction, envelope),
                    "SaleCompleted" => ReplaySale(connection, transaction, envelope),
                    "LegacyRecordArchived" => ReplayLegacy(connection, transaction, envelope),
                    _ => throw new ReplicationRejected()
                };
                using var json = JsonDocument.Parse(envelope.Payload);
                var requestHash = json.RootElement.TryGetProperty("OperationRequestHash", out var property) ? property.GetString() : null;
                if (requestHash != null && (requestHash.Length != 64 || !requestHash.All(Uri.IsHexDigit))) throw new ReplicationRejected();
                Execute(connection, transaction, "INSERT INTO operations VALUES($id,$kind,$hash,$result)",
                    ("$id", envelope.EventId.ToString()), ("$kind", envelope.Kind), ("$hash", requestHash ?? "RESTORED_WITHOUT_REQUEST_HASH"),
                    ("$result", JsonSerializer.Serialize(result)));
            }
            Execute(connection, transaction, "INSERT INTO replication_inbox VALUES($cursor,$id,$hash)",
                ("$cursor", change.Cursor), ("$id", envelope.EventId.ToString()), ("$hash", hash));
            cursor = change.Cursor;
        }
        Execute(connection, transaction, "UPDATE replication_state SET cloud_cursor=$cursor,ready=CASE WHEN $cursor=$head THEN 1 ELSE ready END WHERE singleton=1",
            ("$cursor", cursor), ("$head", page.HeadCursor));
        transaction.Commit();
    }
    private SyncEnvelope? LocalEnvelope(SqliteConnection connection, SqliteTransaction transaction, Guid id)
    {
        using var command = Command(connection, transaction, "SELECT sequence,event_id,kind,payload,created_at FROM outbox WHERE event_id=$id", ("$id", id.ToString()));
        using var reader = command.ExecuteReader();
        return !reader.Read() ? null : new(Guid.Parse(reader.GetString(1)), identity.TenantId, identity.StoreId, identity.DeviceId,
            reader.GetInt64(0), 1, reader.GetString(2), reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture));
    }
    private static ProductView ReplayProduct(SqliteConnection connection, SqliteTransaction transaction, SyncEnvelope envelope)
    {
        var product = BusinessEvents.Product(envelope);
        if (Scalar(connection, transaction, "SELECT id FROM products WHERE id=$id", ("$id", product.Id.ToString())) != null) throw new ReplicationRejected();
        Execute(connection, transaction, "INSERT INTO products VALUES($id,$name,$dimension,$stock)",
            ("$id", product.Id.ToString()), ("$name", product.Name), ("$dimension", product.Dimension), ("$stock", product.StockBase));
        foreach (var price in product.Prices)
            Execute(connection, transaction, "INSERT INTO prices VALUES($id,$unit,$price)", ("$id", product.Id.ToString()), ("$unit", price.Unit), ("$price", price.UnitPriceMinor));
        WriteDetails(connection, transaction, product.Id, product.Details);
        if (product.StockBase != 0) Movement(connection, transaction, envelope.EventId, product.Id, product.StockBase, "Opening", envelope.CreatedAtUtc);
        return product;
    }
    private static SaleResult ReplaySale(SqliteConnection connection, SqliteTransaction transaction, SyncEnvelope envelope)
    {
        var payload = BusinessEvents.Sale(envelope); var sale = payload.Sale;
        Execute(connection, transaction, "INSERT INTO sales(id,total_minor,currency,recorded_at,source_operator_id) VALUES($id,$total,$currency,$time,$actor)",
            ("$id", sale.SaleId.ToString()), ("$total", sale.TotalMinor), ("$currency", sale.Currency), ("$time", Timestamp(sale.RecordedAtUtc)),
            ("$actor", (object?)payload.OperatorId?.ToString() ?? DBNull.Value));
        for (var index = 0; index < payload.Lines.Count; index++)
        {
            var line = payload.Lines[index];
            using (var command = Command(connection, transaction, "SELECT p.name,p.dimension,v.unit_price_minor FROM products p JOIN prices v ON v.product_id=p.id WHERE p.id=$id AND v.unit=$unit",
                ("$id", line.ProductId.ToString()), ("$unit", line.Unit)))
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read() || reader.GetString(0) != line.Name || reader.GetInt64(2) != line.UnitPriceMinor ||
                    Quantities.ToBase(Parse<ProductDimension>(reader.GetString(1)), Parse<SaleUnit>(line.Unit), line.Quantity) != line.QuantityBase)
                    throw new ReplicationRejected();
            }
            if (Execute(connection, transaction, "UPDATE products SET stock_base=stock_base-$quantity WHERE id=$id AND stock_base >= $quantity",
                ("$quantity", line.QuantityBase), ("$id", line.ProductId.ToString())) != 1) throw new ReplicationRejected();
            Execute(connection, transaction, "INSERT INTO sale_lines VALUES($sale,$line,$product,$name,$unit,$quantity,$base,$price,$total)",
                ("$sale", sale.SaleId.ToString()), ("$line", index), ("$product", line.ProductId.ToString()), ("$name", line.Name), ("$unit", line.Unit),
                ("$quantity", line.Quantity.ToString(CultureInfo.InvariantCulture)), ("$base", line.QuantityBase), ("$price", line.UnitPriceMinor), ("$total", line.TotalMinor));
            Movement(connection, transaction, envelope.EventId, line.ProductId, -line.QuantityBase, "Sale", sale.RecordedAtUtc);
        }
        return sale;
    }
    private static LegacyRecordPayload ReplayLegacy(SqliteConnection connection, SqliteTransaction transaction, SyncEnvelope envelope)
    {
        var record = BusinessEvents.Legacy(envelope);
        Execute(connection, transaction, "INSERT INTO legacy_records VALUES($source,$table,$key,$json)",
            ("$source", record.SourceHash), ("$table", record.TableName), ("$key", record.RowKey), ("$json", record.RecordJson));
        return record;
    }
    private static void RequireWritable(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = Command(connection, transaction, "SELECT can_write,ready FROM replication_state WHERE singleton=1");
        using var reader = command.ExecuteReader(); reader.Read();
        if (reader.GetInt64(0) != 1 || reader.GetInt64(1) != 1) throw new OperationConflict("Equipo de consulta o restauración pendiente: no se permiten ventas ni cambios de inventario.");
    }
    private static void WriteDetails(SqliteConnection connection, SqliteTransaction transaction, Guid productId, ProductDetails? details)
    {
        if (details == null) return;
        BusinessEvents.ValidateDetails(details);
        if (details.Barcode != null && Scalar(connection, transaction, "SELECT product_id FROM product_details WHERE barcode=$barcode", ("$barcode", details.Barcode)) != null)
            throw new OperationConflict("Código de barras ya registrado.");
        Execute(connection, transaction, "INSERT INTO product_details VALUES($id,$barcode,$type,$brand,$description)",
            ("$id", productId.ToString()), ("$barcode", (object?)details.Barcode ?? DBNull.Value), ("$type", (object?)details.Type ?? DBNull.Value),
            ("$brand", (object?)details.Brand ?? DBNull.Value), ("$description", (object?)details.Description ?? DBNull.Value));
    }
    public IReadOnlyList<SaleResult> ListSales(int limit = 100)
    {
        if (limit is < 1 or > 100) throw new RuleViolation("Límite inválido.");
        using var connection = Open(); using var command = Command(connection, null, "SELECT id,total_minor,currency,recorded_at FROM sales ORDER BY recorded_at DESC,id LIMIT $limit", ("$limit", limit));
        using var reader = command.ExecuteReader(); var sales = new List<SaleResult>();
        while (reader.Read()) sales.Add(new(Guid.Parse(reader.GetString(0)), reader.GetInt64(1), reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture)));
        return sales;
    }
}
