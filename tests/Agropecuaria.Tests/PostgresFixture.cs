using System.Text.Json;
using Npgsql;

namespace Agropecuaria.Tests;

internal sealed class PostgresFixture : IDisposable
{
    private readonly string schema = "agro_test_" + Guid.NewGuid().ToString("N");
    private readonly string adminConnection;
    public string ConnectionString { get; }
    public PostgresFixture()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "Agropecuaria.slnx"))) root = root.Parent!;
        var file = Environment.GetEnvironmentVariable("AGROPECUARIA_POSTGRES_CONFIG") ?? Path.Combine(root.FullName, ".data", "postgres-dev.json");
        if (!File.Exists(file)) throw new InvalidOperationException("Falta PostgreSQL de pruebas: ejecute scripts/postgres-dev.sh docker-start.");
        using var settings = JsonDocument.Parse(File.ReadAllText(file));
        var value = settings.RootElement;
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1", Port = value.GetProperty("port").GetInt32(), Username = value.GetProperty("username").GetString(),
            Password = value.GetProperty("password").GetString(), Database = value.GetProperty("database").GetString(),
            Pooling = false, Timeout = 5, CommandTimeout = 15, IncludeErrorDetail = false
        };
        adminConnection = builder.ConnectionString;
        using var connection = new NpgsqlConnection(adminConnection); connection.Open();
        using var command = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", connection); command.ExecuteNonQuery();
        builder.SearchPath = schema; ConnectionString = builder.ConnectionString;
    }
    public NpgsqlConnection Open() { var connection = new NpgsqlConnection(ConnectionString); connection.Open(); return connection; }
    public T Scalar<T>(string sql)
    { using var connection = Open(); using var command = new NpgsqlCommand(sql, connection); return (T)command.ExecuteScalar()!; }
    public void Dispose()
    {
        using var connection = new NpgsqlConnection(adminConnection); connection.Open();
        using var command = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", connection); command.ExecuteNonQuery();
    }
}
