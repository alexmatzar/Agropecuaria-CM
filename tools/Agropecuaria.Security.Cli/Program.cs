using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agropecuaria.Application;
using Agropecuaria.Infrastructure;

var workspace = Environment.GetEnvironmentVariable("AGROPECUARIA_WORKSPACE") ?? Directory.GetCurrentDirectory();
var data = Path.Combine(workspace, ".data");
var keyPath = Path.Combine(data, "security", "keyring.json");
var dbPath = Path.Combine(data, "local.secure.db");
var master = Environment.GetEnvironmentVariable("AGROPECUARIA_MASTER_PASSWORD");
bool dpapi = args.Contains("--windows-dpapi");
string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new ArgumentException($"Falta {name}.");
string Arg(int index) => args.Length > index && !args[index].StartsWith("--", StringComparison.Ordinal) ? args[index] : throw new ArgumentException("Falta ruta requerida.");
byte[]? key = null;
try
{
    switch (args.FirstOrDefault())
    {
        case "legacy-plan":
            if (!Enum.TryParse<LegacyStockEncoding>(Arg(3), true, out var encoding) || !Enum.IsDefined(encoding))
                throw new ArgumentException("Convención: Unspecified, PurchasedUnit o AtomicOunceOrPiece.");
            var overrides = args.Length > 4 ? JsonSerializer.Deserialize<Dictionary<string, LegacyStockEncoding>>(File.ReadAllText(Arg(4))) : null;
            var plan = LegacyConversion.Analyze(Arg(1), encoding, overrides);
            WriteNewJson(Arg(2), plan);
            Console.WriteLine($"Plan creado: {plan.Products.Count} productos; {plan.Issues.Count(i => i.Severity == "Error")} errores; {plan.PhotoCount} adjuntos. Revise el informe antes de convertir.");
            if (plan.Issues.Any(i => i.Severity == "Error")) Environment.ExitCode = 3;
            break;
        case "legacy-import":
            var reviewed = JsonSerializer.Deserialize<LegacyConversionPlan>(File.ReadAllText(Arg(3))) ?? throw new ArgumentException("Informe inválido.");
            var destinationFolder = Path.GetFullPath(Arg(2));
            if (Directory.Exists(destinationFolder) || File.Exists(destinationFolder)) throw new IOException("El destino debe ser una carpeta nueva.");
            key = KeyVault.Unlock(keyPath, master);
            var installation = JsonNode.Parse(File.ReadAllText(Path.Combine(data, "development.json")))!["Installation"]!.Deserialize<InstallationIdentity>()!;
            Directory.CreateDirectory(destinationFolder);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destinationFolder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var convertedPath = Path.Combine(destinationFolder, "local.secure.db");
            var conversion = LegacyConversion.ConvertCopy(Arg(1), convertedPath, installation, key, reviewed);
            KeyVault.ExportRecoveryEnvelope(keyPath, Path.Combine(destinationFolder, "recovery-envelope.json"));
            WriteNewJson(Path.Combine(destinationFolder, "installation.json"), installation);
            using (var convertedFile = File.OpenRead(convertedPath))
                WriteNewJson(Path.Combine(destinationFolder, "manifest.json"), new { Version = 1, Kind = "LegacyConversion", Conversion = conversion,
                    DatabaseSha256 = Convert.ToHexString(SHA256.HashData(convertedFile)) });
            Console.WriteLine("Copia cifrada convertida y conciliada. El original y la instalación actual permanecen intactos. No está adoptada automáticamente.");
            break;
        case "enroll":
            if (File.Exists(dbPath)) throw new IOException("No se modifica el negocio de una base existente. Use una instalación nueva para reemplazo o recuperación.");
            var endpoint = new Uri(Required("Identity__AuthorityEndpoint"));
            if (endpoint.Scheme != "https" && !(endpoint.Scheme == "http" && endpoint.IsLoopback)) throw new ArgumentException("La autoridad debe usar HTTPS.");
            var emulatorText = Environment.GetEnvironmentVariable("Firebase__EmulatorEndpoint");
            var emulator = emulatorText == null ? null : new Uri(emulatorText);
            if (emulator != null && (!emulator.IsLoopback || emulator.Scheme != "http")) throw new ArgumentException("Emulador no permitido.");
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
            {
                var provider = new FirebaseSignIn(http, Required("Firebase__ApiKey"), emulator);
                var authority = new HttpCloudAuthority(http, endpoint);
                var credentials = await provider.SignInAsync(Required("AGROPECUARIA_ENROLL_EMAIL"), Required("AGROPECUARIA_ENROLL_PASSWORD"), default);
                var memberships = await authority.DiscoverAsync(credentials.IdToken, default);
                var selectedId = Environment.GetEnvironmentVariable("AGROPECUARIA_ENROLL_STORE_ID");
                var selected = memberships.Count == 1 && selectedId == null ? memberships[0] : memberships.SingleOrDefault(m => m.StoreId.ToString() == selectedId);
                if (selected == null) throw new ArgumentException("No hay una sucursal única. Configure AGROPECUARIA_ENROLL_STORE_ID con una sucursal autorizada.");
                var settingsFile = Path.Combine(data, "development.json");
                var settings = JsonNode.Parse(File.ReadAllText(settingsFile))!;
                settings["Installation"] = JsonSerializer.SerializeToNode(new InstallationIdentity(selected.TenantId, selected.StoreId, Guid.NewGuid(), "GTQ"));
                var temporary = settingsFile + ".enrollment-" + Guid.NewGuid().ToString("N");
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var file = new FileStream(temporary, options)) await JsonSerializer.SerializeAsync(file, settings);
                File.Move(temporary, settingsFile, overwrite: true);
                Console.WriteLine("Equipo vinculado a una sucursal autorizada. No se guardó la contraseña ni se descargaron datos de negocio.");
            }
            break;
        case "init":
            if (!Arg(1).EndsWith(".key", StringComparison.OrdinalIgnoreCase) && !Arg(1).EndsWith(".recovery-key", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Use extensión .key o .recovery-key para el material de recuperación.");
            if (File.Exists(dbPath)) throw new IOException("Ya existe una base segura; no se reemplazan sus claves.");
            key = KeyVault.Create(keyPath, Arg(1), master, dpapi);
            Console.WriteLine("Sobre de clave y material de recuperación creados. Guarde la recuperación fuera del equipo.");
            break;
        case "recover":
            KeyVault.Recover(Arg(1), Arg(2), Arg(3), master, dpapi);
            Console.WriteLine("Nuevo sobre creado. No se modificaron la base ni el sobre original.");
            break;
        case "bootstrap-admin":
        case "backup":
        case "recover-admin":
            if (args[0] is "bootstrap-admin" or "recover-admin" && Environment.GetEnvironmentVariable("AGROPECUARIA_ALLOW_LEGACY_LOCAL_TESTS") != "true")
                throw new InvalidOperationException("Comando de cuentas locales reservado a pruebas legacy. Las cuentas activas se administran en la autoridad de nube.");
            key = args[0] == "recover-admin" ? KeyVault.UnlockWithRecovery(keyPath, Arg(1)) : KeyVault.Unlock(keyPath, master);
            using (var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "development.json"))))
            {
                var identity = settings.RootElement.GetProperty("Installation").Deserialize<InstallationIdentity>()!;
                if (args[0] is "bootstrap-admin" or "recover-admin")
                {
                    var store = new SqlitePosStore(dbPath, identity, key); store.Initialize();
                    if (args[0] == "bootstrap-admin")
                        store.BootstrapAdministrator(Required("AGROPECUARIA_ADMIN_USERNAME"),
                            Environment.GetEnvironmentVariable("AGROPECUARIA_ADMIN_NAME") ?? "Administrador", Required("AGROPECUARIA_ADMIN_PASSWORD"));
                    else store.RecoverAdministrator(Required("AGROPECUARIA_ADMIN_USERNAME"), Required("AGROPECUARIA_ADMIN_PASSWORD"));
                    Console.WriteLine(args[0] == "bootstrap-admin" ? "Administrador inicial creado. No hay alta inicial por HTTP." : "Administrador recuperado. Se revocaron todas las sesiones; deberá cambiar la contraseña temporal.");
                }
                else
                {
                    var destination = Path.GetFullPath(Arg(1));
                    if (Directory.Exists(destination)) throw new IOException("La carpeta de respaldo ya existe.");
                    Directory.CreateDirectory(destination);
                    SqliteDatabase.Backup(dbPath, Path.Combine(destination, "local.secure.db"), key);
                    KeyVault.ExportRecoveryEnvelope(keyPath, Path.Combine(destination, "recovery-envelope.json"));
                    File.WriteAllText(Path.Combine(destination, "installation.json"), JsonSerializer.Serialize(identity));
                    using var databaseFile = File.OpenRead(Path.Combine(destination, "local.secure.db"));
                    File.WriteAllText(Path.Combine(destination, "manifest.json"), JsonSerializer.Serialize(new
                    { Version = 1, CreatedAtUtc = DateTimeOffset.UtcNow, DatabaseSha256 = Convert.ToHexString(SHA256.HashData(databaseFile)) }));
                    Console.WriteLine("Respaldo cifrado verificado. La clave de recuperación se guarda por separado.");
                }
            }
            break;
        default:
            Console.Error.WriteLine("Uso: enroll | init <recovery.key> [--windows-dpapi] | backup <carpeta-nueva> | recover <sobre> <recovery.key> <nuevo-sobre> [--windows-dpapi] | legacy-plan <copia.db> <informe.json> <convención> [selección-por-código.json] | legacy-import <copia.db> <carpeta-nueva> <informe.json>. Cuentas locales legacy: solo pruebas explícitas.");
            Environment.ExitCode = 2;
            break;
    }
}
catch (Exception ex) when (ex is ArgumentException or IOException or CryptographicException or InvalidOperationException or FormatException or JsonException or OverflowException or Microsoft.Data.Sqlite.SqliteException or Agropecuaria.Domain.RuleViolation or Agropecuaria.Domain.OperationConflict or CloudUnavailable or CloudIdentityRejected)
{
    // Never include keys, passwords or database connection strings in CLI diagnostics.
    Console.Error.WriteLine(ex is CryptographicException or Microsoft.Data.Sqlite.SqliteException ? "No se pudo desbloquear o abrir la base: secreto incorrecto, archivo alterado o configuración inválida." : ex.Message);
    Environment.ExitCode = 1;
}
finally { if (key != null) CryptographicOperations.ZeroMemory(key); }

static void WriteNewJson<T>(string path, T value)
{
    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
    if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    using var file = new FileStream(path, options);
    JsonSerializer.Serialize(file, value, new JsonSerializerOptions { WriteIndented = true });
}
