using System.Net;
using System.Security.Cryptography;
using System.Text;
using Agropecuaria.Application;
using Agropecuaria.Contracts;
using Agropecuaria.Domain;
using Agropecuaria.Infrastructure;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = [.. args, "--hostBuilder:reloadConfigOnChange=false"], ContentRootPath = AppContext.BaseDirectory
});
if (!builder.Environment.IsDevelopment()) throw new InvalidOperationException("Receptor exclusivamente de desarrollo.");
var workspace = Environment.GetEnvironmentVariable("AGROPECUARIA_WORKSPACE") ?? Directory.GetCurrentDirectory();
builder.Configuration.AddJsonFile(Path.Combine(workspace, ".data", "development.json"), false).AddEnvironmentVariables();
builder.WebHost.ConfigureKestrel(options => { options.Listen(IPAddress.Loopback, builder.Configuration.GetValue<int>("SyncReceiver:Port", 5188)); options.Limits.MaxRequestBodySize = 256 * 1024; });
var token = builder.Configuration["Sync:AccessToken"];
if (string.IsNullOrWhiteSpace(token) || token.Length < 64) throw new InvalidOperationException("Falta token de desarrollo.");
var identity = builder.Configuration.GetSection("Installation").Get<InstallationIdentity>()!;
var inbox = new DevelopmentInbox(Path.Combine(workspace, ".data", "receiver.db"), identity);
inbox.Initialize();
var app = builder.Build();
app.Use(async (context, next) =>
{
    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()), Encoding.UTF8.GetBytes("Bearer " + token)))
    { context.Response.StatusCode = 401; return; }
    try { await next(context); }
    catch (RuleViolation ex) { await Results.Problem(ex.Message, statusCode: 400).ExecuteAsync(context); }
    catch (OperationConflict ex) { await Results.Problem(ex.Message, statusCode: 409).ExecuteAsync(context); }
});
app.MapGet("/health", () => Results.Ok(new { Status = "DevelopmentReceiver", Events = inbox.Count() }));
app.MapPost("/sync/events", (SyncEnvelope envelope) => Results.Ok(inbox.Accept(envelope)));
await app.RunAsync();
