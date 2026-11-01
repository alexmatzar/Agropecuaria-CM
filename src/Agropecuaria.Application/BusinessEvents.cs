using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agropecuaria.Contracts;
using Agropecuaria.Domain;

namespace Agropecuaria.Application;

public static class BusinessEvents
{
    public static byte[] Hash(SyncEnvelope envelope) => SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope)));
    public static void ValidateEnvelope(SyncEnvelope envelope)
    {
        if (envelope is null) throw new RuleViolation("Falta evento de negocio.");
        if (envelope.EventId == Guid.Empty || envelope.TenantId == Guid.Empty || envelope.StoreId == Guid.Empty ||
            envelope.DeviceId == Guid.Empty || envelope.Sequence <= 0 || envelope.SchemaVersion != 1 ||
            string.IsNullOrWhiteSpace(envelope.Payload) || Encoding.UTF8.GetByteCount(envelope.Payload) > 262_144 ||
            envelope.Kind is not ("ProductRegistered" or "SaleCompleted" or "LegacyRecordArchived"))
            throw new RuleViolation("Evento de negocio inválido o versión no admitida.");
    }
    public static T Payload<T>(SyncEnvelope envelope)
    {
        ValidateEnvelope(envelope);
        try { return JsonSerializer.Deserialize<T>(envelope.Payload) ?? throw new RuleViolation("Falta contenido de evento."); }
        catch (JsonException) { throw new RuleViolation("Contenido de evento inválido."); }
    }
    public static ProductView Product(SyncEnvelope envelope)
    {
        var product = Payload<ProductView>(envelope);
        if (product.Id == Guid.Empty || string.IsNullOrWhiteSpace(product.Name) || product.Name.Length > 200 ||
            !Enum.TryParse<ProductDimension>(product.Dimension, out var dimension) || !Enum.IsDefined(dimension) ||
            product.BaseUnit != (dimension == ProductDimension.Weight ? "micro-pound" : "piece") || product.StockBase < 0 ||
            product.Prices is not { Count: > 0 and <= 6 } ||
            product.Prices.Any(p => p == null || p.UnitPriceMinor < 0) ||
            product.Prices.Select(p => p.Unit).Distinct().Count() != product.Prices.Count)
            throw new RuleViolation("Producto replicado inválido.");
        foreach (var price in product.Prices)
        {
            if (!Enum.TryParse<SaleUnit>(price.Unit, out var unit) || !Enum.IsDefined(unit)) throw new RuleViolation("Unidad inválida.");
            Quantities.ToBase(dimension, unit, 1);
        }
        ValidateDetails(product.Details);
        return product;
    }
    public static void ValidateDetails(ProductDetails? details)
    {
        if (details is null) return;
        if (details.Barcode is { Length: > 128 } || details.Barcode != null && string.IsNullOrWhiteSpace(details.Barcode) ||
            details.Type is { Length: > 200 } || details.Brand is { Length: > 200 } || details.Description is { Length: > 2000 })
            throw new RuleViolation("Metadatos de producto inválidos.");
    }
    public static SaleCompletedPayload Sale(SyncEnvelope envelope)
    {
        var sale = Payload<SaleCompletedPayload>(envelope);
        if (sale.Sale == null || sale.Sale.SaleId == Guid.Empty || sale.Sale.Currency != "GTQ" || sale.Sale.TotalMinor < 0 ||
            sale.Sale.RecordedAtUtc != envelope.CreatedAtUtc || sale.Lines is not { Count: > 0 and <= 100 })
            throw new RuleViolation("Venta replicada inválida.");
        long total = 0;
        foreach (var line in sale.Lines)
        {
            if (line == null || line.ProductId == Guid.Empty || string.IsNullOrWhiteSpace(line.Name) || line.Name.Length > 200 ||
                line.QuantityBase <= 0 || line.UnitPriceMinor < 0 || line.TotalMinor != Quantities.LineTotalMinor(line.UnitPriceMinor, line.Quantity))
                throw new RuleViolation("Línea de venta replicada inválida.");
            total = checked(total + line.TotalMinor);
        }
        if (total != sale.Sale.TotalMinor) throw new RuleViolation("Total de venta no conciliado.");
        return sale;
    }
    public static LegacyRecordPayload Legacy(SyncEnvelope envelope)
    {
        var record = Payload<LegacyRecordPayload>(envelope);
        if (record.SourceHash is not { Length: 64 } || !record.SourceHash.All(Uri.IsHexDigit) ||
            record.TableName is not ("productos" or "unidades_venta" or "clientes" or "ventas" or "notificaciones") ||
            string.IsNullOrWhiteSpace(record.RowKey) || record.RowKey.Length > 256 || string.IsNullOrWhiteSpace(record.RecordJson))
            throw new RuleViolation("Archivo histórico inválido.");
        try { using var json = JsonDocument.Parse(record.RecordJson); if (json.RootElement.ValueKind != JsonValueKind.Object) throw new RuleViolation("Registro histórico inválido."); }
        catch (JsonException) { throw new RuleViolation("Registro histórico inválido."); }
        return record;
    }
}
