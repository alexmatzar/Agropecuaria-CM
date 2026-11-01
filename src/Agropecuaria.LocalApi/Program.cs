using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Agropecuaria.Application;
using Agropecuaria.Contracts;
using Agropecuaria.Domain;
using Agropecuaria.Infrastructure;
using Agropecuaria.LocalApi;

var workspace = Environment.GetEnvironmentVariable("AGROPECUARIA_WORKSPACE") ?? Directory.GetCurrentDirectory();
var dataDirectory = Path.Combine(workspace, ".data");
var settingsPath = Path.Combine(dataDirectory, "development.json");
if (args.Contains("--init-dev"))
{
    if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") != "Development")
        throw new InvalidOperationException("--init-dev requiere ASPNETCORE_ENVIRONMENT=Development.");
    Directory.CreateDirectory(dataDirectory);
    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    // CreateNew preserves existing credentials and installation identity.
    using var file = new FileStream(settingsPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(settingsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    await JsonSerializer.SerializeAsync(file, new
    {
        LocalApi = new { AccessToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) },
        Installation = new { TenantId = Guid.NewGuid(), StoreId = Guid.NewGuid(), DeviceId = Guid.NewGuid(), Currency = "GTQ" },
        Sync = new { Enabled = false, Endpoint = "http://127.0.0.1:5188/sync/events", AccessToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) }
    }, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine("Configuración creada. Complete el aprovisionamiento seguro mediante Security.Cli antes de arrancar.");
    return;
}

// A service reads configuration at startup. Avoid recursive filesystem watchers
// (including macOS sandbox FSEvents) and require a restart for configuration changes.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = [.. args, "--hostBuilder:reloadConfigOnChange=false"], ContentRootPath = AppContext.BaseDirectory
});
if (!builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Publicación bloqueada: faltan HTTPS local, ACL/identidad de servicio e instalador validados en Windows. Use Development solo con datos de prueba.");
builder.Configuration.AddJsonFile(settingsPath, optional: false, reloadOnChange: false).AddEnvironmentVariables();
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, builder.Configuration.GetValue<int>("LocalApi:Port", 5187));
    options.Limits.MaxRequestBodySize = 256 * 1024;
});
builder.Services.AddWindowsService(options => options.ServiceName = "Agropecuaria.LocalApi");
builder.Services.AddProblemDetails();
var identity = builder.Configuration.GetSection("Installation").Get<InstallationIdentity>()
    ?? throw new InvalidOperationException("Falta identidad de instalación.");
if (identity.TenantId == Guid.Empty || identity.StoreId == Guid.Empty || identity.DeviceId == Guid.Empty || identity.Currency != "GTQ")
    throw new InvalidOperationException("Identidad o moneda inválida para este piloto.");
var keyPath = Path.Combine(dataDirectory, "security", "keyring.json");
if (!File.Exists(keyPath)) throw new InvalidOperationException("Faltan claves locales. Ejecute scripts/setup-security.sh y prepare la identidad central según docs/cloud-identity.md. No se abrirá local.db sin cifrar.");
var databaseKey = KeyVault.Unlock(keyPath, Environment.GetEnvironmentVariable("AGROPECUARIA_MASTER_PASSWORD"));
var store = new SqlitePosStore(Path.Combine(dataDirectory, "local.secure.db"), identity, databaseKey);
store.Initialize();
builder.Services.AddSingleton<IPosStore>(store);
builder.Services.AddSingleton<IOutbox>(store);
builder.Services.AddSingleton<IOperatorAccounts>(store);
builder.Services.AddSingleton<ICloudSessionCache>(store);
builder.Services.AddSingleton<IReplicationStore>(store);
var legacyIdentity = builder.Configuration["Identity:Mode"] == "LocalDevelopment";
if (legacyIdentity && !builder.Configuration.GetValue<bool>("Identity:AllowLegacyLocalForTests"))
    throw new InvalidOperationException("LocalDevelopment está reservado a pruebas explícitas; la autenticación normal requiere Firebase y autoridad de nube.");
builder.Services.AddSingleton(new IdentityMode(legacyIdentity));
builder.Services.AddSingleton(identity);
Uri? cloudEndpoint = Uri.TryCreate(builder.Configuration["Identity:AuthorityEndpoint"], UriKind.Absolute, out var configuredAuthority) ? configuredAuthority : null;
if (cloudEndpoint != null && (cloudEndpoint.UserInfo.Length > 0 || (cloudEndpoint.Scheme != "https" && !(cloudEndpoint.Scheme == "http" && cloudEndpoint.IsLoopback))))
    throw new InvalidOperationException("La autoridad de identidad requiere HTTPS; HTTP solo se permite en loopback de desarrollo.");
Uri? firebaseEmulator = Uri.TryCreate(builder.Configuration["Firebase:EmulatorEndpoint"], UriKind.Absolute, out var configuredEmulator) ? configuredEmulator : null;
if (firebaseEmulator != null && (!firebaseEmulator.IsLoopback || firebaseEmulator.Scheme != "http"))
    throw new InvalidOperationException("Firebase Emulator solo está permitido en loopback de desarrollo.");
builder.Services.AddSingleton<IFirebaseSignIn>(new FirebaseSignIn(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }, builder.Configuration["Firebase:ApiKey"] ?? "", firebaseEmulator));
builder.Services.AddSingleton<ICloudAuthority>(new HttpCloudAuthority(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }, cloudEndpoint));
builder.Services.AddSingleton<CloudOperatorService>();
builder.Services.AddHostedService<CloudSessionWorker>();
builder.Services.AddAuthentication("LocalOperator")
    .AddScheme<AuthenticationSchemeOptions, OperatorAuthenticationHandler>("LocalOperator", _ => { });
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    options.AddPolicy("Ready", p => p.RequireAuthenticatedUser().RequireClaim("password_change_required", "false"));
    options.AddPolicy("Admin", p => p.RequireAuthenticatedUser().RequireClaim("password_change_required", "false").RequireRole("Admin"));
    options.AddPolicy("Sell", p => p.RequireAuthenticatedUser().RequireClaim("password_change_required", "false").RequireRole("Admin", "Cashier"));
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("credentials", limiter =>
    { limiter.PermitLimit = 20; limiter.Window = TimeSpan.FromMinutes(1); limiter.QueueLimit = 0; limiter.AutoReplenishment = true; });
});
var syncEnabled = builder.Configuration.GetValue<bool>("Sync:Enabled");
var commercialSync = builder.Configuration["Sync:Mode"] == "Cloud";
Uri? endpoint = syncEnabled && !commercialSync ? new Uri(builder.Configuration["Sync:Endpoint"] ?? throw new InvalidOperationException("Falta Sync:Endpoint.")) : null;
if (endpoint != null && endpoint.Scheme != "https" && !(endpoint.Scheme == "http" && endpoint.IsLoopback))
    throw new InvalidOperationException("La sincronización remota requiere HTTPS.");
if (endpoint != null && (string.IsNullOrWhiteSpace(builder.Configuration["Sync:AccessToken"]) || endpoint.UserInfo.Length > 0))
    throw new InvalidOperationException("Configuración de sincronización inválida.");
if (commercialSync)
    builder.Services.AddSingleton<ISyncTransport>(new CloudSyncTransport(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }, syncEnabled ? cloudEndpoint : null, identity, store));
else
    builder.Services.AddSingleton<ISyncTransport>(new HttpSyncTransport(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }, endpoint, builder.Configuration["Sync:AccessToken"]));
builder.Services.AddSingleton<SyncCoordinator>();
builder.Services.AddHostedService<SyncWorker>();

var app = builder.Build();
app.Lifetime.ApplicationStopped.Register(() => CryptographicOperations.ZeroMemory(databaseKey));
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    try { await next(context); }
    catch (RuleViolation ex) { await Results.Problem(ex.Message, statusCode: 400).ExecuteAsync(context); }
    catch (OperationConflict ex) { await Results.Problem(ex.Message, statusCode: 409).ExecuteAsync(context); }
    catch (LoginRejected ex) { await Results.Problem(ex.Message, statusCode: 401).ExecuteAsync(context); }
    catch (AccessDenied ex) { await Results.Problem(ex.Message, statusCode: 403).ExecuteAsync(context); }
    catch (CloudUnavailable ex) { await Results.Problem(ex.Message, statusCode: 503).ExecuteAsync(context); }
    catch (CloudIdentityRejected ex) { await Results.Problem(ex.Message, statusCode: 401).ExecuteAsync(context); }
    catch (OverflowException) { await Results.Problem("Cantidad o importe fuera de rango.", statusCode: 400).ExecuteAsync(context); }
});
app.UseRouting(); app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization();
app.MapGet("/health", () => Results.Ok(new { Status = "LocalReady", DevelopmentOnly = true })).AllowAnonymous();
app.MapPost("/api/auth/login", async (LoginRequest request, IOperatorAccounts accounts, CloudOperatorService cloud, CancellationToken ct) =>
    Results.Ok(legacyIdentity ? accounts.Login(request) : await cloud.LoginAsync(request, ct)))
    .AllowAnonymous().RequireRateLimiting("credentials");
app.MapGet("/api/auth/me", (HttpContext context) =>
{
    var actor = Actor(context);
    return Results.Ok(new UserView(actor.Id, actor.Username, actor.DisplayName, actor.Role.ToString(), true, actor.MustChangePassword));
}).RequireAuthorization();
app.MapPost("/api/auth/logout", (HttpContext context, IOperatorAccounts accounts) =>
{ accounts.Logout(context.Request.Headers.Authorization.ToString()[7..]); return Results.NoContent(); }).RequireAuthorization();
app.MapPost("/api/auth/revalidate", async (HttpContext context, CloudOperatorService cloud, CancellationToken ct) =>
{
    if (!legacyIdentity) await cloud.RevalidateSessionAsync(Token(context), ct);
    return Results.NoContent();
}).RequireAuthorization();
app.MapPost("/api/auth/password", async (ChangePasswordRequest request, HttpContext context, IOperatorAccounts accounts, CloudOperatorService cloud, CancellationToken ct) =>
{
    if (legacyIdentity) accounts.ChangePassword(Actor(context), request);
    else await cloud.ChangePasswordAsync(Token(context), request, ct);
    return Results.NoContent();
}).RequireAuthorization().RequireRateLimiting("credentials");
var admin = app.MapGroup("/api/admin").RequireAuthorization("Admin");
admin.MapPost("/sync/run", async (SyncCoordinator sync, CancellationToken ct) =>
{ await sync.RunOnceAsync(ct); return Results.Ok(sync.Status); });
admin.MapPost("/sync/enroll", async (EnrollSyncRequest request, HttpContext context, CloudOperatorService cloud, CancellationToken ct) =>
{
    if (legacyIdentity || !commercialSync) throw new RuleViolation("El registro requiere identidad Firebase y Sync:Mode=Cloud.");
    var registration = store.PrepareEnrollment(request.CanWrite, request.ReplaceWriter);
    var device = await cloud.RegisterDeviceAsync(Token(context), registration, ct);
    store.EnrollDevice(device);
    return Results.Ok(new { Enrolled = true, device.CanWrite, Restoring = device.HeadCursor > 0 });
});
admin.MapGet("/users", async (HttpContext context, IOperatorAccounts accounts, CloudOperatorService cloud, CancellationToken ct) =>
    Results.Ok(legacyIdentity ? accounts.ListUsers(Actor(context)) : await cloud.ListUsersAsync(Token(context), ct)));
admin.MapPost("/users", async (CreateUserRequest request, HttpContext context, IOperatorAccounts accounts, CloudOperatorService cloud, CancellationToken ct) =>
    Results.Ok(legacyIdentity ? accounts.CreateUser(Actor(context), request) : await cloud.CreateUserAsync(Token(context), request, ct)));
admin.MapPatch("/users/{id:guid}", async (Guid id, UpdateUserRequest request, HttpContext context, IOperatorAccounts accounts, CloudOperatorService cloud, CancellationToken ct) =>
    Results.Ok(legacyIdentity ? accounts.UpdateUser(Actor(context), id, request) : await cloud.UpdateUserAsync(Token(context), id, request, ct)));
admin.MapPost("/users/{id:guid}/reset-password", async (Guid id, ResetPasswordRequest request, HttpContext context, IOperatorAccounts accounts, CloudOperatorService cloud, CancellationToken ct) =>
{
    if (legacyIdentity) accounts.ResetPassword(Actor(context), id, request.TemporaryPassword);
    else await cloud.ResetPasswordAsync(Token(context), id, request.TemporaryPassword, ct);
    return Results.NoContent();
});
admin.MapGet("/audit", (HttpContext context, IOperatorAccounts accounts) => Results.Ok(accounts.Audit(Actor(context))));
admin.MapGet("/cloud-audit", async (HttpContext context, CloudOperatorService cloud, CancellationToken ct) =>
    Results.Ok(await cloud.AuditAsync(Token(context), ct)));
app.MapGet("/api/products", (IPosStore pos) =>
{ if (!store.IsReady()) throw new OperationConflict("Enlace o restauración pendiente; consulte el estado de sincronización."); return Results.Ok(pos.ListProducts()); }).RequireAuthorization("Ready");
app.MapPost("/api/products", (CreateProductRequest request, HttpContext context, IPosStore pos) => Results.Ok(pos.CreateProduct(request, Actor(context)))).RequireAuthorization("Admin");
app.MapPost("/api/sales", (CreateSaleRequest request, HttpContext context, IPosStore pos) => Results.Ok(pos.CompleteSale(request, Actor(context)))).RequireAuthorization("Sell");
app.MapGet("/api/sales", () =>
{ if (!store.IsReady()) throw new OperationConflict("Enlace o restauración pendiente; consulte el estado de sincronización."); return Results.Ok(store.ListSales()); }).RequireAuthorization("Ready");
app.MapGet("/api/sync/status", (SyncCoordinator sync) => Results.Ok(sync.Status)).RequireAuthorization("Ready");
app.Logger.LogWarning("Desarrollo: SQLite cifrada y sesiones locales. HTTP solo loopback; HTTPS e instalador de producción pendientes.");
await app.RunAsync();

static OperatorIdentity Actor(HttpContext context) => (OperatorIdentity)context.Items["operator"]!;
static string Token(HttpContext context) => context.Request.Headers.Authorization.ToString()[7..];
