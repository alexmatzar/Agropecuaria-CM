using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Agropecuaria.Application;
using Agropecuaria.Contracts;
using Agropecuaria.Domain;
using Npgsql;

namespace Agropecuaria.Cloud.Infrastructure;

public sealed record DeviceIdentity(Guid TenantId, Guid StoreId, Guid DeviceId);

public sealed class CloudReplication(Func<NpgsqlConnection> open)
{
    public RegisteredDevice Register(Membership actor, RegisterDeviceRequest request)
    {
        if (request.DeviceId == Guid.Empty || request.ReplaceWriter && !request.CanWrite || !request.CanWrite && request.HasLocalData)
            throw new RuleViolation("Registro de dispositivo inválido.");
        byte[] secret;
        if (request.Secret is not { Length: 44 }) throw new RuleViolation("Secreto de dispositivo inválido.");
        try { secret = Convert.FromBase64String(request.Secret); } catch (FormatException) { throw new RuleViolation("Secreto de dispositivo inválido."); }
        if (secret.Length != 32) throw new RuleViolation("Secreto de dispositivo inválido.");
        using var connection = open(); using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        LockStore(connection, transaction, actor.TenantId, actor.StoreId);
        RequireAdmin(connection, transaction, actor);
        Execute(connection, transaction, "INSERT INTO cloud_replication(tenant_id,store_id) VALUES(@tenant,@store) ON CONFLICT DO NOTHING", Scope(actor.TenantId, actor.StoreId));
        var head = (long)Scalar(connection, transaction, "SELECT head_cursor FROM cloud_replication WHERE tenant_id=@tenant AND store_id=@store", Scope(actor.TenantId, actor.StoreId))!;
        using (var existing = Command(connection, transaction, "SELECT secret_hash,active,can_write FROM cloud_devices WHERE tenant_id=@tenant AND store_id=@store AND device_id=@device",
            [.. Scope(actor.TenantId, actor.StoreId), ("device", request.DeviceId)]))
        using (var reader = existing.ExecuteReader())
        {
            if (reader.Read())
            {
                if (!reader.GetBoolean(1) || reader.GetBoolean(2) != request.CanWrite ||
                    !CryptographicOperations.FixedTimeEquals((byte[])reader.GetValue(0), SHA256.HashData(secret)))
                    throw new OperationConflict("El dispositivo ya fue registrado con otra credencial o fue revocado.");
                return new(request.DeviceId, request.Secret, request.CanWrite, head);
            }
        }
        if (request.HasLocalData && head > 0) throw new OperationConflict("La nube ya contiene datos. Recupere en una instalación vacía; no se mezclan inventarios.");
        if (request.CanWrite)
        {
            var writer = Scalar(connection, transaction, "SELECT device_id FROM cloud_devices WHERE tenant_id=@tenant AND store_id=@store AND active AND can_write", Scope(actor.TenantId, actor.StoreId));
            if (writer != null && !request.ReplaceWriter) throw new OperationConflict("Existe una autoridad de inventario activa. Su reemplazo debe ser explícito.");
            if (writer is Guid prior)
            {
                Execute(connection, transaction, "UPDATE cloud_devices SET active=false WHERE tenant_id=@tenant AND store_id=@store AND device_id=@device",
                    [.. Scope(actor.TenantId, actor.StoreId), ("device", prior)]);
                Audit(connection, transaction, actor, prior, "WriterReplaced");
            }
        }
        Execute(connection, transaction, """
            INSERT INTO cloud_devices(tenant_id,store_id,device_id,secret_hash,active,can_write,registered_by)
            VALUES(@tenant,@store,@device,@hash,true,@write,@uid)
            """, [.. Scope(actor.TenantId, actor.StoreId), ("device", request.DeviceId), ("hash", SHA256.HashData(secret)), ("write", request.CanWrite), ("uid", actor.Uid)]);
        Audit(connection, transaction, actor, request.DeviceId, "DeviceRegistered");
        transaction.Commit();
        return new(request.DeviceId, Convert.ToBase64String(secret), request.CanWrite, head);
    }
    public void Revoke(Membership actor, Guid device)
    {
        using var connection = open(); using var transaction = connection.BeginTransaction();
        LockStore(connection, transaction, actor.TenantId, actor.StoreId); RequireAdmin(connection, transaction, actor);
        if (Execute(connection, transaction, "UPDATE cloud_devices SET active=false WHERE tenant_id=@tenant AND store_id=@store AND device_id=@device",
            [.. Scope(actor.TenantId, actor.StoreId), ("device", device)]) != 1) throw new AccessDenied();
        Audit(connection, transaction, actor, device, "DeviceRevoked"); transaction.Commit();
    }
    public DeviceIdentity? Authenticate(Guid tenant, Guid store, Guid device, string secret)
    {
        if (secret.Length is < 40 or > 100) return null;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(secret); } catch (FormatException) { return null; }
        if (bytes.Length != 32) return null;
        using var connection = open();
        using var command = Command(connection, null, "SELECT secret_hash FROM cloud_devices WHERE tenant_id=@tenant AND store_id=@store AND device_id=@device AND active",
            [.. Scope(tenant, store), ("device", device)]);
        return command.ExecuteScalar() is byte[] hash && CryptographicOperations.FixedTimeEquals(hash, SHA256.HashData(bytes)) ? new(tenant, store, device) : null;
    }
    public SyncAcknowledgement Accept(DeviceIdentity actor, SyncEnvelope envelope)
    {
        BusinessEvents.ValidateEnvelope(envelope);
        if (envelope.TenantId != actor.TenantId || envelope.StoreId != actor.StoreId || envelope.DeviceId != actor.DeviceId) throw new AccessDenied();
        var hash = BusinessEvents.Hash(envelope);
        using var connection = open(); using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        LockStore(connection, transaction, actor.TenantId, actor.StoreId);
        var (canWrite, lastSequence) = Device(connection, transaction, actor);
        if (!canWrite) throw new AccessDenied();
        using (var command = Command(connection, transaction, "SELECT content_hash FROM cloud_events WHERE tenant_id=@tenant AND store_id=@store AND event_id=@event",
            [.. Scope(actor.TenantId, actor.StoreId), ("event", envelope.EventId)]))
        {
            if (command.ExecuteScalar() is byte[] saved)
            {
                if (!CryptographicOperations.FixedTimeEquals(hash, saved)) throw new OperationConflict("EventId ya recibido con otro contenido.");
                return new(envelope.EventId);
            }
        }
        if (envelope.Sequence != checked(lastSequence + 1)) throw new OperationConflict("Falta un evento anterior o se reutilizó una secuencia.");
        var cursor = checked((long)Scalar(connection, transaction, "SELECT head_cursor FROM cloud_replication WHERE tenant_id=@tenant AND store_id=@store", Scope(actor.TenantId, actor.StoreId))! + 1);
        Execute(connection, transaction, """
            INSERT INTO cloud_events(tenant_id,store_id,event_id,device_id,sequence,cursor,schema_version,kind,envelope_json,content_hash)
            VALUES(@tenant,@store,@event,@device,@sequence,@cursor,@version,@kind,@json,@hash)
            """, [.. Scope(actor.TenantId, actor.StoreId), ("event", envelope.EventId), ("device", actor.DeviceId), ("sequence", envelope.Sequence),
            ("cursor", cursor), ("version", envelope.SchemaVersion), ("kind", envelope.Kind), ("json", JsonSerializer.Serialize(envelope)), ("hash", hash)]);
        switch (envelope.Kind)
        {
            case "ProductRegistered": Product(connection, transaction, envelope); break;
            case "SaleCompleted": Sale(connection, transaction, envelope); break;
            case "LegacyRecordArchived": Legacy(connection, transaction, envelope); break;
        }
        Execute(connection, transaction, "UPDATE cloud_devices SET last_sequence=@sequence WHERE tenant_id=@tenant AND store_id=@store AND device_id=@device",
            [.. Scope(actor.TenantId, actor.StoreId), ("sequence", envelope.Sequence), ("device", actor.DeviceId)]);
        Execute(connection, transaction, "UPDATE cloud_replication SET head_cursor=@cursor WHERE tenant_id=@tenant AND store_id=@store",
            [.. Scope(actor.TenantId, actor.StoreId), ("cursor", cursor)]);
        transaction.Commit(); return new(envelope.EventId);
    }
    public CloudChangePage Pull(DeviceIdentity actor, long after, int limit)
    {
        if (after < 0 || limit is < 1 or > 100) throw new RuleViolation("Cursor o tamaño de página inválido.");
        using var connection = open(); using var transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead);
        Device(connection, transaction, actor);
        var head = (long)Scalar(connection, transaction, "SELECT head_cursor FROM cloud_replication WHERE tenant_id=@tenant AND store_id=@store", Scope(actor.TenantId, actor.StoreId))!;
        if (after > head) throw new OperationConflict("Cursor local por delante del servidor; se requiere conciliación.");
        using var command = Command(connection, transaction, """
            SELECT cursor,envelope_json FROM cloud_events WHERE tenant_id=@tenant AND store_id=@store AND cursor>@after AND cursor<=@head
            ORDER BY cursor LIMIT @limit
            """, [.. Scope(actor.TenantId, actor.StoreId), ("after", after), ("head", head), ("limit", limit)]);
        using var reader = command.ExecuteReader(); var changes = new List<CloudChange>();
        while (reader.Read()) changes.Add(new(reader.GetInt64(0), JsonSerializer.Deserialize<SyncEnvelope>(reader.GetString(1))!));
        return new(after, changes.Count == 0 ? after : changes[^1].Cursor, head, changes);
    }
    private static void Product(NpgsqlConnection connection, NpgsqlTransaction transaction, SyncEnvelope envelope)
    {
        var product = BusinessEvents.Product(envelope);
        var scope = Scope(envelope.TenantId, envelope.StoreId);
        if (Scalar(connection, transaction, "SELECT id FROM cloud_products WHERE tenant_id=@tenant AND store_id=@store AND id=@id", [.. scope, ("id", product.Id)]) != null)
            throw new OperationConflict("Producto ya registrado.");
        Execute(connection, transaction, "INSERT INTO cloud_products VALUES(@tenant,@store,@id,@name,@dimension,@stock,@details)",
            [.. scope, ("id", product.Id), ("name", product.Name), ("dimension", product.Dimension), ("stock", product.StockBase),
            ("details", product.Details == null ? null : JsonSerializer.Serialize(product.Details))]);
        foreach (var price in product.Prices)
            Execute(connection, transaction, "INSERT INTO cloud_prices VALUES(@tenant,@store,@id,@unit,@price)",
                [.. scope, ("id", product.Id), ("unit", price.Unit), ("price", price.UnitPriceMinor)]);
        if (product.StockBase != 0) Movement(connection, transaction, envelope, 0, product.Id, product.StockBase, "Opening");
    }
    private static void Sale(NpgsqlConnection connection, NpgsqlTransaction transaction, SyncEnvelope envelope)
    {
        var payload = BusinessEvents.Sale(envelope); var sale = payload.Sale;
        var scope = Scope(envelope.TenantId, envelope.StoreId);
        if (Scalar(connection, transaction, "SELECT id FROM cloud_sales WHERE tenant_id=@tenant AND store_id=@store AND id=@id", [.. scope, ("id", sale.SaleId)]) != null)
            throw new OperationConflict("Venta ya registrada.");
        Execute(connection, transaction, "INSERT INTO cloud_sales VALUES(@tenant,@store,@id,@operation,@total,@currency,@time,@actor)",
            [.. scope, ("id", sale.SaleId), ("operation", envelope.EventId), ("total", sale.TotalMinor), ("currency", sale.Currency),
            ("time", sale.RecordedAtUtc.UtcDateTime), ("actor", payload.OperatorId)]);
        for (var index = 0; index < payload.Lines.Count; index++)
        {
            var line = payload.Lines[index];
            using (var command = Command(connection, transaction, """
                SELECT p.name,p.dimension,v.unit_price_minor FROM cloud_products p JOIN cloud_prices v
                ON v.tenant_id=p.tenant_id AND v.store_id=p.store_id AND v.product_id=p.id
                WHERE p.tenant_id=@tenant AND p.store_id=@store AND p.id=@id AND v.unit=@unit
                """, [.. scope, ("id", line.ProductId), ("unit", line.Unit)]))
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read() || reader.GetString(0) != line.Name || reader.GetInt64(2) != line.UnitPriceMinor ||
                    !Enum.TryParse<ProductDimension>(reader.GetString(1), out var dimension) ||
                    !Enum.TryParse<SaleUnit>(line.Unit, out var unit) || !Enum.IsDefined(unit) ||
                    Quantities.ToBase(dimension, unit, line.Quantity) != line.QuantityBase)
                    throw new RuleViolation("El detalle de venta no coincide con catálogo, unidades o precios.");
            }
            if (Execute(connection, transaction, "UPDATE cloud_products SET stock_base=stock_base-@quantity WHERE tenant_id=@tenant AND store_id=@store AND id=@id AND stock_base>=@quantity",
                [.. scope, ("quantity", line.QuantityBase), ("id", line.ProductId)]) != 1) throw new OperationConflict("Movimiento no conciliado: stock central insuficiente.");
            Execute(connection, transaction, "INSERT INTO cloud_sale_lines VALUES(@tenant,@store,@sale,@line,@product,@name,@unit,@quantity,@base,@price,@total)",
                [.. scope, ("sale", sale.SaleId), ("line", index), ("product", line.ProductId), ("name", line.Name), ("unit", line.Unit), ("quantity", line.Quantity),
                ("base", line.QuantityBase), ("price", line.UnitPriceMinor), ("total", line.TotalMinor)]);
            Movement(connection, transaction, envelope, index, line.ProductId, -line.QuantityBase, "Sale");
        }
    }
    private static void Legacy(NpgsqlConnection connection, NpgsqlTransaction transaction, SyncEnvelope envelope)
    {
        var record = BusinessEvents.Legacy(envelope);
        Execute(connection, transaction, "INSERT INTO cloud_legacy_records VALUES(@tenant,@store,@source,@table,@key,@json)",
            [.. Scope(envelope.TenantId, envelope.StoreId), ("source", record.SourceHash), ("table", record.TableName), ("key", record.RowKey), ("json", record.RecordJson)]);
    }
    private static void Movement(NpgsqlConnection connection, NpgsqlTransaction transaction, SyncEnvelope envelope, int line, Guid product, long delta, string reason) =>
        Execute(connection, transaction, "INSERT INTO cloud_inventory_movements VALUES(@tenant,@store,@event,@line,@product,@delta,@reason,@time)",
            [.. Scope(envelope.TenantId, envelope.StoreId), ("event", envelope.EventId), ("line", line), ("product", product), ("delta", delta), ("reason", reason), ("time", envelope.CreatedAtUtc.UtcDateTime)]);
    private static (bool CanWrite, long LastSequence) Device(NpgsqlConnection connection, NpgsqlTransaction transaction, DeviceIdentity actor)
    {
        using var command = Command(connection, transaction, "SELECT can_write,last_sequence FROM cloud_devices WHERE tenant_id=@tenant AND store_id=@store AND device_id=@device AND active",
            [.. Scope(actor.TenantId, actor.StoreId), ("device", actor.DeviceId)]);
        using var reader = command.ExecuteReader(); if (!reader.Read()) throw new AccessDenied();
        return (reader.GetBoolean(0), reader.GetInt64(1));
    }
    private static void LockStore(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenant, Guid store)
    {
        if (Scalar(connection, transaction, "SELECT store_id FROM cloud_stores WHERE tenant_id=@tenant AND store_id=@store FOR UPDATE", Scope(tenant, store)) == null) throw new AccessDenied();
    }
    private static void RequireAdmin(NpgsqlConnection connection, NpgsqlTransaction transaction, Membership actor)
    {
        var role = Scalar(connection, transaction, """
            SELECT role FROM cloud_memberships WHERE tenant_id=@tenant AND store_id=@store AND operator_id=@actor
            AND uid=@uid AND active=1 AND must_change=0
            """, [.. Scope(actor.TenantId, actor.StoreId), ("actor", actor.Id.ToString()), ("uid", actor.Uid)]);
        if (!Equals(role, "Admin")) throw new AccessDenied();
    }
    private static void Audit(NpgsqlConnection connection, NpgsqlTransaction transaction, Membership actor, Guid device, string action) =>
        Execute(connection, transaction, "INSERT INTO cloud_device_audit(tenant_id,store_id,device_id,actor_uid,action) VALUES(@tenant,@store,@device,@uid,@action)",
            [.. Scope(actor.TenantId, actor.StoreId), ("device", device), ("uid", actor.Uid), ("action", action)]);
    private static (string, object?)[] Scope(Guid tenant, Guid store) => [("tenant", tenant.ToString()), ("store", store.ToString())];
    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, params (string Name, object? Value)[] values)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private static object? Scalar(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, params (string Name, object? Value)[] values)
    { using var command = Command(connection, transaction, sql, values); return command.ExecuteScalar(); }
    private static int Execute(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, params (string Name, object? Value)[] values)
    { using var command = Command(connection, transaction, sql, values); return command.ExecuteNonQuery(); }
}
