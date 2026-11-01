using System.Security.Cryptography;
using Agropecuaria.Application;
using Agropecuaria.Domain;
using Agropecuaria.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Agropecuaria.Tests;

public sealed class LegacyConversionTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), "agro-conversion-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    private readonly InstallationIdentity identity = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "GTQ");
    private string Source(decimal stock = 0.77m, decimal price = 5m, string unit = "Quintal(es)")
    {
        Directory.CreateDirectory(path); var file = Path.Combine(path, "original-" + Guid.NewGuid().ToString("N") + ".db");
        using var connection = SqliteDatabase.Open(file); using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE productos(codigo_barras TEXT PRIMARY KEY,tipo TEXT,marca TEXT,descripcion TEXT,unidad_comprada TEXT,stock NUMERIC,preciocompra NUMERIC,foto BLOB);
            CREATE TABLE unidades_venta(codigo_barras TEXT,unidad_venta TEXT,precio_venta NUMERIC);
            CREATE TABLE clientes(nit TEXT PRIMARY KEY,nombre TEXT);
            CREATE TABLE ventas(codigo_id INTEGER PRIMARY KEY,ventashechas TEXT,productosvendidos TEXT,total NUMERIC,totalganancias NUMERIC,monto NUMERIC,cambio NUMERIC,nitcliente TEXT,nombrecliente TEXT,fecha TEXT,hora TEXT);
            CREATE TABLE notificaciones(codigo_barras TEXT PRIMARY KEY,marca TEXT,descripcion TEXT,stock NUMERIC,fecha_hora TEXT);
            INSERT INTO productos VALUES('001234567890','Alimento','Marca','Concentrado',$unit,$stock,250,$photo);
            INSERT INTO unidades_venta VALUES('001234567890','Libra(s)',$price);
            INSERT INTO clientes VALUES('CF','Cliente ficticio');
            INSERT INTO ventas VALUES(1,'Texto antiguo con separadores~sin identificadores','Recibo original',90,10,100,10,'CF','Cliente ficticio','2024-01-01','12:00:00');
            """;
        command.Parameters.AddWithValue("$unit", unit); command.Parameters.AddWithValue("$stock", stock); command.Parameters.AddWithValue("$price", price);
        command.Parameters.AddWithValue("$photo", new byte[] { 1, 2, 3, 4, 5 }); command.ExecuteNonQuery(); return file;
    }
    [Theory]
    [InlineData("0.77", LegacyStockEncoding.PurchasedUnit, 77_000_000L)]
    [InlineData("8000", LegacyStockEncoding.AtomicOunceOrPiece, 500_000_000L)]
    [InlineData("5", LegacyStockEncoding.PurchasedUnit, 500_000_000L)]
    public void ExplicitEncodingProducesExactBaseStock(string stock, LegacyStockEncoding encoding, long expected)
    {
        var source = Source(decimal.Parse(stock, System.Globalization.CultureInfo.InvariantCulture)); var before = File.ReadAllBytes(source);
        var plan = LegacyConversion.Analyze(source, encoding);
        Assert.DoesNotContain(plan.Issues, i => i.Severity == "Error");
        Assert.Equal(expected, Assert.Single(plan.Products).StockBase); Assert.Equal(before, File.ReadAllBytes(source));
    }
    [Fact]
    public void MissingEncodingAndAmbiguousPricesPreventConversion()
    {
        var source = Source(); var plan = LegacyConversion.Analyze(source);
        Assert.Contains(plan.Issues, i => i.Code == "StockEncodingRequired");
        var output = Path.Combine(path, "must-not-exist.db");
        Assert.Throws<RuleViolation>(() => LegacyConversion.ConvertCopy(source, output, identity, key, plan));
        Assert.False(File.Exists(output));
        var badPrice = LegacyConversion.Analyze(Source(price: 0.005m), LegacyStockEncoding.PurchasedUnit);
        Assert.Contains(badPrice.Issues, i => i.Code == "InvalidProduct");
    }
    [Fact]
    public void EncryptedCopyPreservesBarcodesPhotosAndRawHistoryWithoutDoubleDiscounting()
    {
        var source = Source(); var original = File.ReadAllBytes(source);
        var plan = LegacyConversion.Analyze(source, LegacyStockEncoding.PurchasedUnit);
        var output = Path.Combine(path, "new.secure.db"); var result = LegacyConversion.ConvertCopy(source, output, identity, key, plan);
        Assert.Equal(1, result.ProductCount); Assert.Equal(4, result.HistoricalRecordCount); Assert.Equal(1, result.PhotoCount); Assert.Equal(5, result.PendingEvents);
        Assert.Throws<SqliteException>(() => SqliteDatabase.Open(output));
        var store = new SqlitePosStore(output, identity, key); store.Initialize();
        Assert.Equal(77_000_000, Assert.Single(store.ListProducts()).StockBase);
        Assert.Equal("001234567890", Assert.Single(store.ListProducts()).Details!.Barcode);
        Assert.Empty(store.ListSales()); Assert.Equal(90_00, plan.HistoricalSalesTotalMinor);
        using (var connection = SqliteDatabase.Open(output, key))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT bytes FROM legacy_blobs"; Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, (byte[])command.ExecuteScalar()!);
            command.CommandText = "SELECT record_json FROM legacy_records WHERE table_name='ventas'";
            Assert.Contains("Texto antiguo con separadores", (string)command.ExecuteScalar()!);
        }
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Throws<IOException>(() => LegacyConversion.ConvertCopy(source, output, identity, key, plan));
        Assert.DoesNotContain("Concentrado", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(output)));
    }
    [Fact]
    public void ChangedSourceAndEditedPlanNeverPublishDestination()
    {
        var source = Source(); var plan = LegacyConversion.Analyze(source, LegacyStockEncoding.PurchasedUnit);
        var output = Path.Combine(path, "not-published.db");
        var edited = plan with { Products = [plan.Products[0] with { StockBase = 1 }] };
        Assert.Throws<OperationConflict>(() => LegacyConversion.ConvertCopy(source, output, identity, key, edited));
        using (var connection = SqliteDatabase.Open(source))
        using (var command = connection.CreateCommand())
        { command.CommandText = "INSERT INTO clientes VALUES('1','Otro cliente ficticio')"; command.ExecuteNonQuery(); }
        Assert.Throws<OperationConflict>(() => LegacyConversion.ConvertCopy(source, output, identity, key, plan));
        Assert.False(File.Exists(output)); Assert.Empty(Directory.GetFiles(path, "*.conversion-*"));
    }
    [Fact]
    public void PerProductEncodingOverridesAndUnknownUnitsAreExplicit()
    {
        var source = Source(8000);
        var plan = LegacyConversion.Analyze(source, LegacyStockEncoding.Unspecified,
            new Dictionary<string, LegacyStockEncoding> { ["001234567890"] = LegacyStockEncoding.AtomicOunceOrPiece });
        Assert.DoesNotContain(plan.Issues, i => i.Severity == "Error"); Assert.Equal(500_000_000, Assert.Single(plan.Products).StockBase);
        var unknown = LegacyConversion.Analyze(Source(unit: "Quintal métrico"), LegacyStockEncoding.PurchasedUnit);
        Assert.Contains(unknown.Issues, i => i.Code == "InvalidProduct");
    }
    [Fact]
    public void LiveWalIsRejectedInsteadOfReadingAnIncompleteFileCopy()
    {
        var source = Source(); using var connection = SqliteDatabase.Open(source); using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; INSERT INTO clientes VALUES('2','Escritura pendiente de checkpoint');"; command.ExecuteNonQuery();
        Assert.True(new FileInfo(source + "-wal").Length > 0);
        Assert.Throws<RuleViolation>(() => LegacyConversion.Analyze(source, LegacyStockEncoding.PurchasedUnit));
    }
    public void Dispose() { CryptographicOperations.ZeroMemory(key); if (Directory.Exists(path)) Directory.Delete(path, true); }
}
