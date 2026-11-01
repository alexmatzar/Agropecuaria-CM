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

[JsonConverter(typeof(JsonStringEnumConverter<LegacyStockEncoding>))]
public enum LegacyStockEncoding { Unspecified, PurchasedUnit, AtomicOunceOrPiece }
public sealed record ConversionIssue(string Severity, string Code, string? ProductBarcode, string Message);
public sealed record LegacyProductPlan(Guid ProductId, Guid OperationId, string Barcode, string DeclaredUnit,
    string RawStock, LegacyStockEncoding Encoding, long? StockBase, string Dimension, string Name, ProductDetails Details, List<PriceInput> Prices);
public sealed record LegacyConversionPlan(int Version, string SourceSha256, long SourceBytes, LegacyStockEncoding DefaultEncoding,
    Dictionary<string, LegacyStockEncoding> EncodingByBarcode, Dictionary<string, long> RowCounts,
    List<LegacyProductPlan> Products, List<ConversionIssue> Issues, long PhotoCount, long HistoricalSalesTotalMinor);
public sealed record LegacyConversionResult(string SourceSha256, long ProductCount, long HistoricalRecordCount, long PhotoCount, long PendingEvents);

public static class LegacyConversion
{
    private static readonly string[] Tables = ["productos", "unidades_venta", "clientes", "ventas", "notificaciones"];
    private sealed record Raw(string Table, string Key, Dictionary<string, string?> Values, string Json);
    private sealed record Blob(string Table, string Key, string Column, string Hash, byte[] Bytes);
    private sealed record Snapshot(LegacyConversionPlan Plan, List<Raw> Rows, List<Blob> Blobs);

    public static LegacyConversionPlan Analyze(string source, LegacyStockEncoding encoding = LegacyStockEncoding.Unspecified,
        IReadOnlyDictionary<string, LegacyStockEncoding>? byBarcode = null) => Read(source, encoding, byBarcode).Plan;

    public static LegacyConversionResult ConvertCopy(string source, string destination, InstallationIdentity identity, byte[] key, LegacyConversionPlan reviewed)
    {
        if (key.Length != 32) throw new CryptographicException("La conversión requiere una clave de cifrado válida.");
        if (File.Exists(destination)) throw new IOException("La base de destino ya existe; no se reemplaza.");
        var snapshot = Read(source, reviewed.DefaultEncoding, reviewed.EncodingByBarcode);
        if (JsonSerializer.Serialize(snapshot.Plan) != JsonSerializer.Serialize(reviewed))
            throw new OperationConflict("El origen o el informe cambió. Genere y revise nuevamente el plan.");
        if (snapshot.Plan.Issues.Any(i => i.Severity == "Error")) throw new RuleViolation("El plan contiene errores o convenciones sin resolver.");
        var absolute = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        var staging = absolute + ".conversion-" + Guid.NewGuid().ToString("N");
        try
        {
            var store = new SqlitePosStore(staging, identity, key); store.Initialize();
            foreach (var product in snapshot.Plan.Products)
                store.CreateProduct(new(product.OperationId, product.ProductId, product.Name, product.Dimension,
                    product.Dimension == "Weight" ? "Pound" : "Piece",
                    product.Dimension == "Weight" ? product.StockBase!.Value / 1_000_000m : product.StockBase!.Value,
                    product.Prices, product.Details));
            foreach (var row in snapshot.Rows)
                store.ArchiveLegacyRecord(Id(snapshot.Plan.SourceSha256 + ":" + row.Table + ":" + row.Key),
                    new(snapshot.Plan.SourceSha256, row.Table, row.Key, row.Json));
            foreach (var blob in snapshot.Blobs)
                store.ArchiveLegacyBlob(snapshot.Plan.SourceSha256, blob.Table, blob.Key, blob.Column, blob.Hash, blob.Bytes);
            using (var database = SqliteDatabase.Open(staging, key))
            using (var command = database.CreateCommand())
            {
                command.CommandText = "PRAGMA integrity_check"; if (!Equals(command.ExecuteScalar(), "ok")) throw new IOException("Integridad del destino no verificada.");
                command.CommandText = "PRAGMA foreign_key_check"; using (var check = command.ExecuteReader()) if (check.Read()) throw new IOException("Relaciones del destino no conciliadas.");
                command.CommandText = "SELECT COUNT(*) FROM legacy_records"; if ((long)command.ExecuteScalar()! != snapshot.Rows.Count) throw new IOException("Archivo histórico incompleto.");
                command.CommandText = "SELECT COUNT(*) FROM legacy_blobs"; if ((long)command.ExecuteScalar()! != snapshot.Blobs.Count) throw new IOException("Fotografías históricas incompletas.");
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)"; command.ExecuteNonQuery();
            }
            var converted = store.ListProducts();
            if (converted.Count != snapshot.Plan.Products.Count || converted.Any(p => p.StockBase != snapshot.Plan.Products.Single(x => x.ProductId == p.Id).StockBase))
                throw new IOException("Inventario convertido no conciliado.");
            if (Hash(source) != snapshot.Plan.SourceSha256 || HasWal(source)) throw new OperationConflict("El origen cambió durante la conversión.");
            var result = new LegacyConversionResult(snapshot.Plan.SourceSha256, converted.Count, snapshot.Rows.Count, snapshot.Blobs.Count, store.PendingCount());
            File.Move(staging, absolute, overwrite: false);
            return result;
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(staging + suffix)) File.Delete(staging + suffix);
        }
    }

    private static Snapshot Read(string source, LegacyStockEncoding encoding, IReadOnlyDictionary<string, LegacyStockEncoding>? byBarcode)
    {
        source = Path.GetFullPath(source);
        if (!File.Exists(source)) throw new FileNotFoundException("No existe la copia SQLite de origen.");
        if (new FileInfo(source).Length > 268_435_456) throw new RuleViolation("La copia supera el límite de este conversor (256 MiB).");
        if (HasWal(source)) throw new RuleViolation("El origen tiene WAL activo. Use una copia consolidada con la API de backup.");
        if (!Enum.IsDefined(encoding)) throw new RuleViolation("Convención de stock inválida.");
        var hash = Hash(source); var issues = new List<ConversionIssue>();
        var overrides = (byBarcode ?? new Dictionary<string, LegacyStockEncoding>()).OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var counts = new Dictionary<string, long>(); var rows = new List<Raw>(); var blobs = new List<Blob>();
        using (var connection = SqliteDatabase.Open(source, readOnly: true))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            foreach (var table in Tables)
            {
                using var exists = connection.CreateCommand(); exists.Transaction = transaction;
                exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name"; exists.Parameters.AddWithValue("$name", table);
                if ((long)exists.ExecuteScalar()! == 0)
                {
                    issues.Add(new(table == "notificaciones" ? "Warning" : "Error", "MissingTable", null, "Falta tabla " + table + "."));
                    counts[table] = 0; continue;
                }
                // Table identifiers are a fixed allow-list, never input from the database or a command line.
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = $"SELECT rowid,* FROM \"{table}\" ORDER BY rowid";
                using var reader = command.ExecuteReader(); long count = 0;
                while (reader.Read())
                {
                    if (++count > 200_000) throw new RuleViolation("La tabla supera el límite de filas del conversor.");
                    var id = reader.GetInt64(0).ToString(CultureInfo.InvariantCulture);
                    var values = new Dictionary<string, string?>(); var archive = new Dictionary<string, object?>();
                    for (var column = 1; column < reader.FieldCount; column++)
                    {
                        var name = reader.GetName(column);
                        if (reader.IsDBNull(column)) { values[name] = null; archive[name] = null; continue; }
                        if (reader.GetFieldType(column) == typeof(byte[]))
                        {
                            var bytes = (byte[])reader.GetValue(column); var digest = System.Convert.ToHexString(SHA256.HashData(bytes));
                            if (bytes.Length > 16_777_216) issues.Add(new("Error", "BlobTooLarge", null, "Un adjunto supera 16 MiB."));
                            blobs.Add(new(table, id, name, digest, bytes)); values[name] = null;
                            archive[name] = new { Storage = "LocalEncryptedLegacyBlob", Sha256 = digest, Bytes = bytes.Length };
                        }
                        else { values[name] = reader.GetString(column); archive[name] = values[name]; }
                    }
                    var raw = new Raw(table, id, values, JsonSerializer.Serialize(archive)); rows.Add(raw);
                    var payload = new LegacyRecordPayload(hash, table, id, raw.Json);
                    if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(payload)) > 261_888)
                        issues.Add(new("Error", "ArchiveTooLarge", null, "Un registro histórico supera el tamaño admitido por evento."));
                    try { BusinessEvents.Legacy(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 1, "LegacyRecordArchived", JsonSerializer.Serialize(payload), DateTimeOffset.UtcNow)); }
                    catch (RuleViolation ex) { issues.Add(new("Error", "InvalidArchive", null, ex.Message)); }
                }
                counts[table] = count;
            }
        }
        if (Hash(source) != hash || HasWal(source)) throw new OperationConflict("El origen cambió durante la lectura.");
        var plans = new List<LegacyProductPlan>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.Where(r => r.Table == "productos"))
        {
            var barcode = Value(row, "codigo_barras") ?? ""; var rawStock = Value(row, "stock") ?? "";
            var unitText = Value(row, "unidad_comprada") ?? ""; var mode = overrides.GetValueOrDefault(barcode, encoding);
            try
            {
                if (string.IsNullOrWhiteSpace(barcode) || barcode.Length > 128 || !seen.Add(barcode)) throw new RuleViolation("Código de barras ausente, demasiado largo o duplicado.");
                var unit = Unit(unitText); var dimension = unit is SaleUnit.Piece or SaleUnit.Hundred ? ProductDimension.Count : ProductDimension.Weight;
                var quantity = Number(rawStock); if (quantity < 0) throw new RuleViolation("Existencia negativa.");
                long? stock = null;
                if (mode == LegacyStockEncoding.Unspecified) issues.Add(new("Error", "StockEncodingRequired", barcode, "Seleccione PurchasedUnit o AtomicOunceOrPiece; no se infiere a partir del número."));
                else
                {
                    if (!Enum.IsDefined(mode)) throw new RuleViolation("Convención de stock inválida.");
                    var sourceUnit = mode == LegacyStockEncoding.PurchasedUnit ? unit : dimension == ProductDimension.Weight ? SaleUnit.Ounce : SaleUnit.Piece;
                    stock = quantity == 0 ? 0 : Quantities.ToBase(dimension, sourceUnit, quantity);
                }
                var prices = new List<PriceInput>();
                foreach (var price in rows.Where(r => r.Table == "unidades_venta" && Value(r, "codigo_barras") == barcode))
                {
                    var saleUnit = Unit(Value(price, "unidad_venta") ?? "");
                    Quantities.ToBase(dimension, saleUnit, 1);
                    prices.Add(new(saleUnit.ToString(), Money(Value(price, "precio_venta") ?? "")));
                }
                var description = Value(row, "descripcion"); var type = Value(row, "tipo"); var brand = Value(row, "marca");
                var name = !string.IsNullOrWhiteSpace(description) && description.Length <= 200 ? description : $"{type} {brand}".Trim();
                var details = new ProductDetails(barcode, type, brand, description);
                var product = new ProductView(Id(hash + ":product:" + barcode), name, dimension.ToString(),
                    dimension == ProductDimension.Weight ? "micro-pound" : "piece", stock ?? 0, prices, details);
                BusinessEvents.Product(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 1, "ProductRegistered", JsonSerializer.Serialize(product), DateTimeOffset.UtcNow));
                plans.Add(new(product.Id, Id(hash + ":opening:" + barcode), barcode, unitText, rawStock, mode, stock, product.Dimension, name, details, prices));
            }
            catch (Exception ex) when (ex is RuleViolation or OverflowException)
            { issues.Add(new("Error", "InvalidProduct", barcode, ex is OverflowException ? "Cantidad o importe fuera de rango." : ex.Message)); }
        }
        foreach (var row in rows.Where(r => r.Table == "unidades_venta"))
            if (!seen.Contains(Value(row, "codigo_barras") ?? "")) issues.Add(new("Error", "OrphanPrice", Value(row, "codigo_barras"), "Precio sin producto."));
        foreach (var item in overrides)
            if (!seen.Contains(item.Key)) issues.Add(new("Error", "UnknownOverride", item.Key, "La selección de convención no corresponde a un producto."));
        long salesTotal = 0;
        foreach (var row in rows.Where(r => r.Table == "ventas"))
        {
            try { salesTotal = checked(salesTotal + Money(Value(row, "total") ?? "")); }
            catch (Exception ex) when (ex is RuleViolation or OverflowException)
            { issues.Add(new("Error", "HistoricalTotal", null, "Total histórico inválido o con precisión ambigua; no se redondea automáticamente.")); }
        }
        if (counts.GetValueOrDefault("ventas") > 0) issues.Add(new("Warning", "UnstructuredSales", null, "Ventas antiguas se conservan en archivo histórico; no se reconstruyen líneas ni se descuenta stock nuevamente."));
        if (plans.Count > 0) issues.Add(new("Warning", "PurchaseCostSemantics", null, "El costo de compra se conserva en el registro original; su significado por unidad o total requiere conciliación antes de valorar costos."));
        if (blobs.Count > 0) issues.Add(new("Warning", "PhotosLocalOnly", null, "Fotos conservadas en archivo cifrado local; subida y descarga mediante almacenamiento de objetos pendientes."));
        return new(new(1, hash, new FileInfo(source).Length, encoding, overrides, counts, plans, issues, blobs.Count, salesTotal), rows, blobs);
    }
    private static bool HasWal(string source) => File.Exists(source + "-wal") && new FileInfo(source + "-wal").Length > 0;
    private static string Hash(string source) { using var file = File.OpenRead(source); return System.Convert.ToHexString(SHA256.HashData(file)); }
    private static string? Value(Raw row, string name) => row.Values.GetValueOrDefault(name);
    private static decimal Number(string text) => decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var value)
        ? value : throw new RuleViolation("Número ausente o ambiguo; se requiere formato numérico invariante.");
    private static long Money(string text)
    {
        var value = Number(text); var minor = checked(value * 100);
        if (value < 0 || minor != decimal.Truncate(minor)) throw new RuleViolation("Importe negativo o con fracciones de centavo.");
        return checked((long)minor);
    }
    private static SaleUnit Unit(string text) => text.Trim().ToLowerInvariant().Replace("(es)", "es", StringComparison.Ordinal).Replace("(s)", "s", StringComparison.Ordinal) switch
    {
        "unidad" or "unidades" => SaleUnit.Piece,
        "ciento" or "cientos" => SaleUnit.Hundred,
        "onza" or "onzas" => SaleUnit.Ounce,
        "libra" or "libras" => SaleUnit.Pound,
        "arroba" or "arrobas" => SaleUnit.Arroba,
        "quintal" or "quintales" => SaleUnit.Quintal,
        _ => throw new RuleViolation("Unidad antigua desconocida; no se presume equivalencia.")
    };
    private static Guid Id(string text) => new(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 16));
}

public sealed partial class SqlitePosStore
{
    public LegacyRecordPayload ArchiveLegacyRecord(Guid operationId, LegacyRecordPayload record) =>
        Transact(operationId, "LegacyRecordArchived", record, (connection, transaction, now) =>
        {
            var envelope = new SyncEnvelope(operationId, identity.TenantId, identity.StoreId, identity.DeviceId, 1, 1,
                "LegacyRecordArchived", JsonSerializer.Serialize(record), now);
            var result = ReplayLegacy(connection, transaction, envelope);
            return (result, (object)result);
        }, null);

    public void ArchiveLegacyBlob(string source, string table, string row, string column, string hash, byte[] bytes)
    {
        if (System.Convert.ToHexString(SHA256.HashData(bytes)) != hash) throw new RuleViolation("Adjunto histórico alterado.");
        using var connection = Open(); using var transaction = connection.BeginTransaction(); RequireWritable(connection, transaction);
        Execute(connection, transaction, "INSERT INTO legacy_blobs VALUES($source,$table,$key,$column,$hash,$bytes)",
            ("$source", source), ("$table", table), ("$key", row), ("$column", column), ("$hash", hash), ("$bytes", bytes));
        transaction.Commit();
    }
}
