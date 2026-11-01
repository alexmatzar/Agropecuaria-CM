using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agropecuaria.Application;
using Agropecuaria.Contracts;
using Agropecuaria.Domain;
using Microsoft.Data.Sqlite;

namespace Agropecuaria.Infrastructure;

// Null keys are reserved for legacy/unit fixtures. LocalApi always supplies a key.
public sealed partial class SqlitePosStore(string databasePath, InstallationIdentity identity,
    byte[]? databaseKey = null, TimeProvider? timeProvider = null) : IPosStore, IOutbox, IOperatorAccounts, ICloudSessionCache
{
    private SqliteConnection Open() => SqliteDatabase.Open(databasePath, databaseKey);

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        using var connection = Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
            pragma.ExecuteNonQuery();
        }
        using var transaction = connection.BeginTransaction();
        var version = Convert.ToInt32(Scalar(connection, transaction, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version > 4) throw new InvalidOperationException("Esquema más reciente que esta aplicación.");
        if (version == 0)
        {
            Execute(connection, transaction, """
                CREATE TABLE installation (
                    singleton INTEGER PRIMARY KEY CHECK(singleton=1), identity_json TEXT NOT NULL
                ) STRICT;
                CREATE TABLE products (
                    id TEXT PRIMARY KEY, name TEXT NOT NULL, dimension TEXT NOT NULL,
                    stock_base INTEGER NOT NULL CHECK(stock_base>=0)
                ) STRICT;
                CREATE TABLE prices (
                    product_id TEXT NOT NULL REFERENCES products(id), unit TEXT NOT NULL,
                    unit_price_minor INTEGER NOT NULL CHECK(unit_price_minor>=0),
                    PRIMARY KEY(product_id,unit)
                ) STRICT;
                CREATE TABLE operations (
                    id TEXT PRIMARY KEY, kind TEXT NOT NULL, request_hash TEXT NOT NULL, result_json TEXT NOT NULL
                ) STRICT;
                CREATE TABLE sales (
                    id TEXT PRIMARY KEY, total_minor INTEGER NOT NULL CHECK(total_minor>=0),
                    currency TEXT NOT NULL, recorded_at TEXT NOT NULL
                ) STRICT;
                CREATE TABLE sale_lines (
                    sale_id TEXT NOT NULL REFERENCES sales(id), line_number INTEGER NOT NULL,
                    product_id TEXT NOT NULL REFERENCES products(id), product_name TEXT NOT NULL,
                    unit TEXT NOT NULL, quantity TEXT NOT NULL, quantity_base INTEGER NOT NULL CHECK(quantity_base>0),
                    unit_price_minor INTEGER NOT NULL CHECK(unit_price_minor>=0),
                    total_minor INTEGER NOT NULL CHECK(total_minor>=0), PRIMARY KEY(sale_id,line_number)
                ) STRICT;
                CREATE TABLE inventory_movements (
                    id TEXT PRIMARY KEY, operation_id TEXT NOT NULL, product_id TEXT NOT NULL REFERENCES products(id),
                    delta_base INTEGER NOT NULL CHECK(delta_base<>0), reason TEXT NOT NULL, recorded_at TEXT NOT NULL
                ) STRICT;
                CREATE TABLE outbox (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT, event_id TEXT NOT NULL UNIQUE,
                    kind TEXT NOT NULL, payload TEXT NOT NULL, created_at TEXT NOT NULL, acknowledged_at TEXT
                ) STRICT;
                CREATE INDEX ix_outbox_pending ON outbox(sequence) WHERE acknowledged_at IS NULL;
                PRAGMA user_version=1;
                """);
            Execute(connection, transaction, "INSERT INTO installation VALUES(1,$identity)",
                ("$identity", JsonSerializer.Serialize(identity)));
        }
        var saved = (string)Scalar(connection, transaction, "SELECT identity_json FROM installation WHERE singleton=1")!;
        if (JsonSerializer.Deserialize<InstallationIdentity>(saved) != identity)
            throw new InvalidOperationException("La identidad de la instalación no coincide con la base de datos.");
        if (version < 2) InitializeSecurity(connection, transaction);
        if (version < 3) InitializeCloudSecurity(connection, transaction);
        if (version < 4) InitializeReplication(connection, transaction);
        transaction.Commit();
    }

    public ProductView CreateProduct(CreateProductRequest request, OperatorIdentity? actor = null)
    {
        RequireId(request.OperationId); RequireId(request.ProductId);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200)
            throw new RuleViolation("Nombre de producto inválido.");
        var dimension = Parse<ProductDimension>(request.Dimension);
        Quantities.ToBase(dimension, Parse<SaleUnit>(request.OpeningUnit), 1);
        var stock = request.OpeningQuantity == 0 ? 0 : Quantities.ToBase(dimension, Parse<SaleUnit>(request.OpeningUnit), request.OpeningQuantity);
        BusinessEvents.ValidateDetails(request.Details);
        if (request.Prices is not { Count: > 0 and <= 6 }) throw new RuleViolation("Debe configurar precios de venta.");
        var prices = request.Prices.Select(p =>
        {
            if (p is null || p.UnitPriceMinor < 0) throw new RuleViolation("Precio inválido.");
            var unit = Parse<SaleUnit>(p.Unit);
            Quantities.ToBase(dimension, unit, 1);
            return new PriceInput(unit.ToString(), p.UnitPriceMinor);
        }).ToList();
        if (prices.Select(p => p.Unit).Distinct().Count() != prices.Count) throw new RuleViolation("Unidad de precio duplicada.");

        return Transact(request.OperationId, "ProductRegistered", request, (connection, transaction, now) =>
        {
            if (Scalar(connection, transaction, "SELECT id FROM products WHERE id=$id", ("$id", request.ProductId.ToString())) != null)
                throw new OperationConflict("El producto ya existe.");
            Execute(connection, transaction, "INSERT INTO products VALUES($id,$name,$dimension,$stock)",
                ("$id", request.ProductId.ToString()), ("$name", request.Name.Trim()),
                ("$dimension", dimension.ToString()), ("$stock", stock));
            foreach (var price in prices)
                Execute(connection, transaction, "INSERT INTO prices VALUES($id,$unit,$price)",
                    ("$id", request.ProductId.ToString()), ("$unit", price.Unit), ("$price", price.UnitPriceMinor));
            WriteDetails(connection, transaction, request.ProductId, request.Details);
            if (stock != 0) Movement(connection, transaction, request.OperationId, request.ProductId, stock, "Opening", now);
            var result = new ProductView(request.ProductId, request.Name.Trim(), dimension.ToString(),
                dimension == ProductDimension.Weight ? "micro-pound" : "piece", stock, prices, request.Details);
            return (result, (object)result);
        }, actor);
    }

    public IReadOnlyList<ProductView> ListProducts()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var products = new List<ProductView>();
        using (var command = Command(connection, transaction, "SELECT p.id,p.name,p.dimension,p.stock_base,d.product_id,d.barcode,d.type,d.brand,d.description FROM products p LEFT JOIN product_details d ON d.product_id=p.id ORDER BY p.name,p.id"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                products.Add(new ProductView(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                    reader.GetString(2) == "Weight" ? "micro-pound" : "piece", reader.GetInt64(3), [], reader.IsDBNull(4) ? null :
                    new ProductDetails(reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                        reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8))));
        foreach (var product in products)
        {
            using var command = Command(connection, transaction, "SELECT unit,unit_price_minor FROM prices WHERE product_id=$id ORDER BY unit",
                ("$id", product.Id.ToString()));
            using var reader = command.ExecuteReader();
            while (reader.Read()) product.Prices.Add(new PriceInput(reader.GetString(0), reader.GetInt64(1)));
        }
        transaction.Commit();
        return products;
    }

    public SaleResult CompleteSale(CreateSaleRequest request, OperatorIdentity? actor = null)
    {
        RequireId(request.OperationId);
        if (request.Lines is not { Count: > 0 and <= 100 }) throw new RuleViolation("La venta requiere entre 1 y 100 líneas.");
        return Transact(request.OperationId, "SaleCompleted", request, (connection, transaction, now) =>
        {
            var saleId = Guid.NewGuid();
            long total = 0;
            var snapshots = new List<object>();
            Execute(connection, transaction, "INSERT INTO sales(id,total_minor,currency,recorded_at) VALUES($id,0,$currency,$time)",
                ("$id", saleId.ToString()), ("$currency", identity.Currency), ("$time", now.ToString("O")));
            for (int index = 0; index < request.Lines.Count; index++)
            {
                var input = request.Lines[index];
                if (input is null) throw new RuleViolation("Línea inválida.");
                RequireId(input.ProductId);
                var unit = Parse<SaleUnit>(input.Unit);
                string name; ProductDimension dimension; long price;
                using (var command = Command(connection, transaction, """
                    SELECT p.name,p.dimension,v.unit_price_minor FROM products p JOIN prices v ON v.product_id=p.id
                    WHERE p.id=$id AND v.unit=$unit
                    """, ("$id", input.ProductId.ToString()), ("$unit", unit.ToString())))
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) throw new RuleViolation("Producto o precio de venta no encontrado.");
                    name = reader.GetString(0); dimension = Parse<ProductDimension>(reader.GetString(1)); price = reader.GetInt64(2);
                }
                var quantityBase = Quantities.ToBase(dimension, unit, input.Quantity);
                var lineTotal = Quantities.LineTotalMinor(price, input.Quantity);
                total = checked(total + lineTotal);
                // The predicate and the write share the same immediate transaction.
                var rows = Execute(connection, transaction, "UPDATE products SET stock_base=stock_base-$quantity WHERE id=$id AND stock_base >= $quantity",
                    ("$quantity", quantityBase), ("$id", input.ProductId.ToString()));
                if (rows != 1) throw new RuleViolation("Existencias insuficientes.");
                Execute(connection, transaction, """
                    INSERT INTO sale_lines VALUES($sale,$line,$product,$name,$unit,$quantity,$base,$price,$total)
                    """, ("$sale", saleId.ToString()), ("$line", index), ("$product", input.ProductId.ToString()),
                    ("$name", name), ("$unit", unit.ToString()), ("$quantity", input.Quantity.ToString(CultureInfo.InvariantCulture)),
                    ("$base", quantityBase), ("$price", price), ("$total", lineTotal));
                Movement(connection, transaction, request.OperationId, input.ProductId, -quantityBase, "Sale", now);
                snapshots.Add(new { input.ProductId, Name = name, Unit = unit.ToString(), input.Quantity,
                    QuantityBase = quantityBase, UnitPriceMinor = price, TotalMinor = lineTotal });
            }
            Execute(connection, transaction, "UPDATE sales SET total_minor=$total WHERE id=$id", ("$total", total), ("$id", saleId.ToString()));
            if (actor != null)
                Execute(connection, transaction, "UPDATE sales SET operator_id=$operator,source_operator_id=$operator WHERE id=$id", ("$operator", actor.Id.ToString()), ("$id", saleId.ToString()));
            var result = new SaleResult(saleId, total, identity.Currency, now);
            return (result, (object)new { Sale = result, OperatorId = actor?.Id, Lines = snapshots });
        }, actor);
    }

    private T Transact<T>(Guid operationId, string kind, object request,
        Func<SqliteConnection, SqliteTransaction, DateTimeOffset, (T Result, object Payload)> body, OperatorIdentity? actor)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request,
            new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }))));
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        RequireWritable(connection, transaction);
        if (actor != null)
        {
            var user = RequireOperator(connection, transaction, actor);
            if (user.MustChange || (kind is "ProductRegistered" or "LegacyRecordArchived" && user.Role != OperatorRole.Admin) ||
                (kind == "SaleCompleted" && user.Role is not (OperatorRole.Admin or OperatorRole.Cashier))) throw new AccessDenied();
        }
        using (var command = Command(connection, transaction, "SELECT kind,request_hash,result_json FROM operations WHERE id=$id", ("$id", operationId.ToString())))
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(0) != kind || reader.GetString(1) != hash)
                    throw new OperationConflict("OperationId ya utilizado con otra solicitud.");
                return JsonSerializer.Deserialize<T>(reader.GetString(2))!;
            }
        }
        var now = DateTimeOffset.UtcNow;
        var (result, payload) = body(connection, transaction, now);
        if (actor != null) AuditWrite(connection, transaction, actor.Id, kind, operationId, now);
        Execute(connection, transaction, "INSERT INTO operations VALUES($id,$kind,$hash,$result)",
            ("$id", operationId.ToString()), ("$kind", kind), ("$hash", hash), ("$result", JsonSerializer.Serialize(result)));
        var eventPayload = JsonSerializer.SerializeToNode(payload)!;
        eventPayload["OperationRequestHash"] = hash;
        var serializedPayload = eventPayload.ToJsonString();
        BusinessEvents.ValidateEnvelope(new(operationId, identity.TenantId, identity.StoreId, identity.DeviceId, 1, 1, kind, serializedPayload, now));
        Execute(connection, transaction, "INSERT INTO outbox(event_id,kind,payload,created_at) VALUES($id,$kind,$payload,$time)",
            ("$id", operationId.ToString()), ("$kind", kind), ("$payload", serializedPayload), ("$time", now.ToString("O")));
        transaction.Commit();
        return result;
    }

    public IReadOnlyList<SyncEnvelope> ReadPending(int limit)
    {
        using var connection = Open();
        using var command = Command(connection, null, "SELECT sequence,event_id,kind,payload,created_at FROM outbox WHERE acknowledged_at IS NULL ORDER BY sequence LIMIT $limit", ("$limit", limit));
        using var reader = command.ExecuteReader();
        var events = new List<SyncEnvelope>();
        while (reader.Read()) events.Add(new SyncEnvelope(Guid.Parse(reader.GetString(1)), identity.TenantId, identity.StoreId,
            identity.DeviceId, reader.GetInt64(0), 1, reader.GetString(2), reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture)));
        return events;
    }

    public void Acknowledge(Guid eventId, DateTimeOffset atUtc)
    {
        using var connection = Open();
        Execute(connection, null, "UPDATE outbox SET acknowledged_at=$time WHERE event_id=$id AND acknowledged_at IS NULL",
            ("$time", atUtc.ToString("O")), ("$id", eventId.ToString()));
    }
    public long PendingCount()
    {
        using var connection = Open();
        return (long)Scalar(connection, null, "SELECT COUNT(*) FROM outbox WHERE acknowledged_at IS NULL")!;
    }
    public DateTimeOffset? LastSuccessUtc()
    {
        using var connection = Open();
        var result = Scalar(connection, null, "SELECT MAX(acknowledged_at) FROM outbox");
        return result is string text ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture) : null;
    }

    private static void Movement(SqliteConnection connection, SqliteTransaction transaction, Guid operationId,
        Guid productId, long delta, string reason, DateTimeOffset now) =>
        Execute(connection, transaction, "INSERT INTO inventory_movements VALUES($id,$operation,$product,$delta,$reason,$time)",
            ("$id", Guid.NewGuid().ToString()), ("$operation", operationId.ToString()), ("$product", productId.ToString()),
            ("$delta", delta), ("$reason", reason), ("$time", now.ToString("O")));

    private static void RequireId(Guid id)
    {
        if (id == Guid.Empty) throw new RuleViolation("Identificador vacío.");
    }
    private static T Parse<T>(string value) where T : struct, Enum =>
        Enum.TryParse<T>(value, true, out var result) && Enum.IsDefined(result)
            ? result : throw new RuleViolation($"Valor inválido de {typeof(T).Name}.");

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }
    private static int Execute(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters); return command.ExecuteNonQuery();
    }
    private static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters); return command.ExecuteScalar();
    }
}
