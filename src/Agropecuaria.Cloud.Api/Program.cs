using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Security.Claims;
using Agropecuaria.Application;
using Agropecuaria.Cloud.Api;
using Agropecuaria.Cloud.Infrastructure;
using Agropecuaria.Contracts;
using Agropecuaria.Domain;
using Agropecuaria.Infrastructure;
using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Google.Apis.Auth.OAuth2;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Npgsql;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = [.. args.Where(a => a is not ("--bootstrap-owner" or "--migrate")), "--hostBuilder:reloadConfigOnChange=false"], ContentRootPath = AppContext.BaseDirectory
});
if (!builder.Environment.IsDevelopment()) throw new InvalidOperationException("Cloud.Api: despliegue real bloqueado hasta validar PostgreSQL, configuración y protección del entorno.");
var emulator = Environment.GetEnvironmentVariable("FIREBASE_AUTH_EMULATOR_HOST");
var projectId = builder.Configuration["Firebase:ProjectId"] ?? "demo-agropecuaria-cm";
if (emulator != null && (!Uri.TryCreate("http://" + emulator, UriKind.Absolute, out var emulatorUri) || !emulatorUri.IsLoopback || !projectId.StartsWith("demo-", StringComparison.Ordinal)))
    throw new InvalidOperationException("El emulador se permite únicamente en loopback y con un proyecto demo.");
var firebaseApp = FirebaseApp.Create(new AppOptions
{
    ProjectId = projectId,
    Credential = emulator != null ? GoogleCredential.FromAccessToken("emulator-only") : GoogleCredential.GetApplicationDefault()
});
var firebase = FirebaseAuth.GetAuth(firebaseApp);
builder.Services.AddSingleton(firebase);
builder.Services.AddProblemDetails();
var sqlite = builder.Configuration.GetValue<bool>("Cloud:UseSqliteDevelopment");
var path = builder.Configuration["Cloud:DevelopmentDatabase"] ?? Path.Combine(Directory.GetCurrentDirectory(), ".data", "cloud-memberships.dev.db");
if (sqlite) Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
var connectionString = builder.Configuration.GetConnectionString("Cloud");
var localPostgres = builder.Configuration["Cloud:DevelopmentPostgresConfig"];
if (!sqlite && connectionString == null && localPostgres != null)
{
    using var file = System.Text.Json.JsonDocument.Parse(File.ReadAllText(localPostgres));
    var settings = file.RootElement;
    connectionString = new NpgsqlConnectionStringBuilder
    {
        Host = "127.0.0.1", Port = settings.GetProperty("port").GetInt32(),
        Username = settings.GetProperty("username").GetString(), Password = settings.GetProperty("password").GetString(),
        Database = settings.GetProperty("database").GetString(), IncludeErrorDetail = false
    }.ConnectionString;
}
if (!sqlite && string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("Falta ConnectionStrings:Cloud (PostgreSQL). SQLite requiere selección explícita de Development.");
var directory = new CloudDirectory(() =>
{
    if (sqlite) return SqliteDatabase.Open(path);
    var connection = new NpgsqlConnection(connectionString); connection.Open(); return connection;
}, postgres: !sqlite);
directory.Initialize(); builder.Services.AddSingleton(directory);
builder.Services.AddSingleton<ICloudProvisioningJournal>(directory);
builder.Services.AddSingleton<ICloudAccountProvider, FirebaseAccountProvider>();
builder.Services.AddSingleton<CloudAccountsService>();
if (args.Contains("--migrate")) { Console.WriteLine("Migraciones PostgreSQL verificadas/aplicadas."); firebaseApp.Delete(); return; }
CloudReplication? replication = null;
if (!sqlite)
{
    replication = new CloudReplication(() => { var connection = new NpgsqlConnection(connectionString); connection.Open(); return connection; });
    builder.Services.AddSingleton(replication);
}

if (args.Contains("--bootstrap-owner"))
{
    var email = Email(builder.Configuration["Bootstrap:Email"]!);
    var password = Environment.GetEnvironmentVariable("AGROPECUARIA_CLOUD_OWNER_PASSWORD") ?? throw new InvalidOperationException("Falta contraseña del propietario.");
    ValidatePassword(password);
    UserRecord user;
    try { user = await firebase.GetUserByEmailAsync(email); }
    catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
    { user = await firebase.CreateUserAsync(new UserRecordArgs { Email = email, Password = password, DisplayName = "Propietario" }); }
    directory.Bootstrap(Guid.Parse(builder.Configuration["Bootstrap:TenantId"]!), Guid.Parse(builder.Configuration["Bootstrap:StoreId"]!), user.Uid, email, "Propietario");
    Console.WriteLine("Propietario vinculado en la autoridad central. No se mostraron credenciales.");
    firebaseApp.Delete(); return;
}
builder.WebHost.ConfigureKestrel(o => { o.Listen(IPAddress.Loopback, builder.Configuration.GetValue<int>("Cloud:Port", 5189)); o.Limits.MaxRequestBodySize = 768 * 1024; });
var authentication = builder.Services.AddAuthentication("Firebase").AddScheme<AuthenticationSchemeOptions, FirebaseAuthenticationHandler>("Firebase", _ => { });
if (!sqlite) authentication.AddScheme<AuthenticationSchemeOptions, DeviceAuthenticationHandler>("Device", _ => { });
builder.Services.AddAuthorization(o =>
{
    o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    o.AddPolicy("Device", new AuthorizationPolicyBuilder("Device").RequireAuthenticatedUser().Build());
});
var app = builder.Build(); app.Lifetime.ApplicationStopped.Register(firebaseApp.Delete);
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    try { await next(context); }
    catch (AccessDenied ex) { await Results.Problem(ex.Message, statusCode: 403).ExecuteAsync(context); }
    catch (RuleViolation ex) { await Results.Problem(ex.Message, statusCode: 400).ExecuteAsync(context); }
    catch (OperationConflict ex) { await Results.Problem(ex.Message, statusCode: 409).ExecuteAsync(context); }
    catch (CloudUnavailable ex) { await Results.Problem(ex.Message, statusCode: 503).ExecuteAsync(context); }
    catch (System.Text.Json.JsonException) { await Results.Problem("Evento JSON inválido.", statusCode: 400).ExecuteAsync(context); }
    catch (OverflowException) { await Results.Problem("Cantidad o importe fuera de rango.", statusCode: 400).ExecuteAsync(context); }
    catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
    { await Results.Problem("Conflicto de identidad o registro duplicado.", statusCode: 409).ExecuteAsync(context); }
    catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.EmailAlreadyExists)
    { await Results.Problem("La cuenta ya existe; se requiere un flujo de invitación, sin reemplazar su contraseña.", statusCode: 409).ExecuteAsync(context); }
});
app.UseAuthentication(); app.UseAuthorization();
app.MapGet("/health", () => Results.Ok(new { Status = "CloudAuthorityDevelopment", Directory = sqlite ? "SqliteDevelopmentOnly" : "PostgreSQL" })).AllowAnonymous();
if (replication != null) ReplicationEndpoints.Map(app, directory, replication);
app.MapGet("/v1/auth/stores", (HttpContext context) => Results.Ok(directory.Discover(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!)));
app.MapPost("/v1/auth/authorize", (CloudAuthorizationRequest request, HttpContext context) =>
{
    var membership = Member(context, request.TenantId, request.StoreId, false);
    var now = DateTimeOffset.UtcNow;
    return Results.Ok(new CloudGrant(membership.TenantId, membership.StoreId, membership.Uid, membership.View, membership.Version, now, now.AddHours(24)));
});
var group = app.MapGroup("/v1/tenants/{tenant:guid}/stores/{store:guid}/users");
group.MapGet("", (Guid tenant, Guid store, HttpContext context) => Results.Ok(directory.List(Member(context, tenant, store, true))));
group.MapPost("", async (Guid tenant, Guid store, CreateUserRequest request, HttpContext context, CloudAccountsService accounts, CancellationToken ct) =>
{
    var actor = Member(context, tenant, store, true);
    return Results.Ok(await accounts.CreateAsync(new(actor.TenantId, actor.StoreId, actor.Id, actor.Uid), request, ct));
});
app.MapGet("/v1/tenants/{tenant:guid}/stores/{store:guid}/audit", (Guid tenant, Guid store, HttpContext context) =>
    Results.Ok(directory.AccountAudit(Member(context, tenant, store, true))));
group.MapPatch("/{id:guid}", (Guid tenant, Guid store, Guid id, UpdateUserRequest request, HttpContext context) =>
    Results.Ok(directory.Update(Member(context, tenant, store, true), id, request).View));
group.MapPost("/{id:guid}/reset-password", async (Guid tenant, Guid store, Guid id, ResetPasswordRequest request, HttpContext context) =>
{
    var actor = Member(context, tenant, store, true); ValidatePassword(request.TemporaryPassword);
    var target = directory.GetById(tenant, store, id) ?? throw new AccessDenied();
    if (directory.MembershipCount(target.Uid) > 1) throw new RuleViolation("Una cuenta compartida entre negocios debe restablecer su propia contraseña mediante verificación de identidad.");
    directory.SetPasswordChange(actor, id, true); // Fail closed if the provider call fails.
    await firebase.UpdateUserAsync(new UserRecordArgs { Uid = target.Uid, Password = request.TemporaryPassword });
    await firebase.RevokeRefreshTokensAsync(target.Uid);
    directory.RecordAccountEvent(actor, "PasswordResetCompleted", target.Id);
    return Results.Ok(new { Updated = true });
});
group.MapPost("/me/password", async (Guid tenant, Guid store, ResetPasswordRequest request, HttpContext context) =>
{
    var actor = Member(context, tenant, store, false); ValidatePassword(request.TemporaryPassword);
    var authTime = long.Parse(context.User.FindFirstValue("auth_time")!, CultureInfo.InvariantCulture);
    if (DateTimeOffset.FromUnixTimeSeconds(authTime) < DateTimeOffset.UtcNow.AddMinutes(-5)) throw new AccessDenied();
    await firebase.UpdateUserAsync(new UserRecordArgs { Uid = actor.Uid, Password = request.TemporaryPassword });
    await firebase.RevokeRefreshTokensAsync(actor.Uid);
    directory.SetPasswordChange(actor, actor.Id, false);
    directory.RecordAccountEvent(actor, "PasswordChanged", actor.Id);
    return Results.Ok(new { Updated = true });
});
await app.RunAsync();

Membership Member(HttpContext context, Guid tenant, Guid store, bool admin)
{
    var member = directory.Get(tenant, store, context.User.FindFirstValue(ClaimTypes.NameIdentifier)!) ?? throw new AccessDenied();
    if (!member.Active || (admin && (member.Role != "Admin" || member.MustChange))) throw new AccessDenied();
    return member;
}
static string Email(string value)
{
    if (value is null || value.Length > 254 || !MailAddress.TryCreate(value, out var email) || email.Address != value) throw new RuleViolation("Correo inválido.");
    return email.Address.ToLowerInvariant();
}
static void ValidatePassword(string value)
{ if (value is null || value.Length is < 15 or > 128 || string.IsNullOrWhiteSpace(value)) throw new RuleViolation("La contraseña requiere entre 15 y 128 caracteres."); }
