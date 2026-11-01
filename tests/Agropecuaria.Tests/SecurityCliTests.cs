using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Agropecuaria.Application;
using Agropecuaria.Contracts;
using Agropecuaria.Infrastructure;
using Xunit;

namespace Agropecuaria.Tests;

public sealed class SecurityCliTests
{
    [Fact]
    public async Task CliProvisionsBacksUpAndRecoversWithoutPrintingSecrets()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "Agropecuaria.slnx"))) root = root.Parent!;
        var workspace = Path.Combine(Path.GetTempPath(), "agropecuaria-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspace, ".data"));
        var identity = new InstallationIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "GTQ");
        await File.WriteAllTextAsync(Path.Combine(workspace, ".data", "development.json"), JsonSerializer.Serialize(new { Installation = identity }));
        var master = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        try
        {
            async Task<int> Run(params string[] args)
            {
                var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
                var info = new ProcessStartInfo("dotnet") { WorkingDirectory = root.FullName, RedirectStandardError = true, RedirectStandardOutput = true };
                info.ArgumentList.Add(Path.Combine(root.FullName, $"tools/Agropecuaria.Security.Cli/bin/{configuration}/net10.0/Agropecuaria.Security.Cli.dll"));
                foreach (var arg in args) info.ArgumentList.Add(arg);
                info.Environment["AGROPECUARIA_WORKSPACE"] = workspace;
                info.Environment["AGROPECUARIA_ALLOW_LEGACY_LOCAL_TESTS"] = "true";
                info.Environment["AGROPECUARIA_MASTER_PASSWORD"] = master;
                info.Environment["AGROPECUARIA_ADMIN_USERNAME"] = "owner";
                info.Environment["AGROPECUARIA_ADMIN_PASSWORD"] = password;
                using var process = Process.Start(info)!;
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await process.WaitForExitAsync(timeout.Token); }
                finally { if (!process.HasExited) { process.Kill(true); process.WaitForExit(); } }
                var output = await stdout + await stderr;
                Assert.DoesNotContain(master, output); Assert.DoesNotContain(password, output);
                return process.ExitCode;
            }
            var recovery = Path.Combine(workspace, "recovery.key");
            Assert.Equal(0, await Run("init", recovery));
            Assert.Equal(1, await Run("init", recovery));
            Assert.Equal(0, await Run("bootstrap-admin"));
            Assert.Equal(1, await Run("bootstrap-admin"));
            var settingsBefore = await File.ReadAllTextAsync(Path.Combine(workspace, ".data", "development.json"));
            var databaseBefore = await File.ReadAllBytesAsync(Path.Combine(workspace, ".data", "local.secure.db"));
            Assert.Equal(1, await Run("enroll")); // Never reassign a terminal with business data.
            Assert.Equal(settingsBefore, await File.ReadAllTextAsync(Path.Combine(workspace, ".data", "development.json")));
            Assert.Equal(databaseBefore, await File.ReadAllBytesAsync(Path.Combine(workspace, ".data", "local.secure.db")));
            var backup = Path.Combine(workspace, "backup"); Assert.Equal(0, await Run("backup", backup));
            Assert.True(File.Exists(Path.Combine(backup, "manifest.json")));
            var restoredEnvelope = Path.Combine(workspace, "new-envelope.json");
            Assert.Equal(0, await Run("recover", Path.Combine(backup, "recovery-envelope.json"), recovery, restoredEnvelope));
            var key = KeyVault.Unlock(restoredEnvelope, master);
            try
            {
                var restored = new SqlitePosStore(Path.Combine(backup, "local.secure.db"), identity, key); restored.Initialize();
                Assert.Equal("Admin", restored.Login(new LoginRequest("owner", password)).User.Role);
            }
            finally { CryptographicOperations.ZeroMemory(key); }
            var legacy = Path.Combine(workspace, "legacy.db");
            using (var connection = SqliteDatabase.Open(legacy))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    CREATE TABLE productos(codigo_barras TEXT PRIMARY KEY,tipo TEXT,marca TEXT,descripcion TEXT,unidad_comprada TEXT,stock NUMERIC,preciocompra NUMERIC);
                    CREATE TABLE unidades_venta(codigo_barras TEXT,unidad_venta TEXT,precio_venta NUMERIC);
                    CREATE TABLE clientes(nit TEXT,nombre TEXT);
                    CREATE TABLE ventas(codigo_id INTEGER,total NUMERIC);
                    CREATE TABLE notificaciones(codigo_barras TEXT);
                    INSERT INTO productos VALUES('001','Alimento','Marca','Concentrado','Quintal',5,250);
                    INSERT INTO unidades_venta VALUES('001','Libra',5);
                    """;
                command.ExecuteNonQuery();
            }
            var originalLegacy = await File.ReadAllBytesAsync(legacy);
            var report = Path.Combine(workspace, "conversion.json");
            Assert.Equal(0, await Run("legacy-plan", legacy, report, "PurchasedUnit"));
            Assert.Equal(1, await Run("legacy-plan", legacy, report, "PurchasedUnit")); // No report overwrite.
            var converted = Path.Combine(workspace, "converted");
            Assert.Equal(0, await Run("legacy-import", legacy, converted, report));
            Assert.True(File.Exists(Path.Combine(converted, "manifest.json")));
            Assert.True(File.Exists(Path.Combine(converted, "recovery-envelope.json")));
            Assert.Equal(originalLegacy, await File.ReadAllBytesAsync(legacy));
            Assert.Equal(1, await Run("legacy-import", legacy, converted, report));
            password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
            Assert.Equal(0, await Run("recover-admin", recovery));
            key = KeyVault.UnlockWithRecovery(Path.Combine(workspace, ".data", "security", "keyring.json"), recovery);
            try
            {
                var recovered = new SqlitePosStore(Path.Combine(workspace, ".data", "local.secure.db"), identity, key); recovered.Initialize();
                Assert.True(recovered.Login(new("owner", password)).User.MustChangePassword);
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        finally { Directory.Delete(workspace, true); }
    }
}
