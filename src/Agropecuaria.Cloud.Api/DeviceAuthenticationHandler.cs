using System.Security.Claims;
using System.Text.Encodings.Web;
using Agropecuaria.Cloud.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Agropecuaria.Cloud.Api;

public sealed class DeviceAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, CloudReplication replication)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Device ", StringComparison.OrdinalIgnoreCase) || header.Length > 128 ||
            !Guid.TryParse(Request.RouteValues["tenant"]?.ToString(), out var tenant) ||
            !Guid.TryParse(Request.RouteValues["store"]?.ToString(), out var store) ||
            !Guid.TryParse(Request.RouteValues["device"]?.ToString(), out var device))
            return Task.FromResult(AuthenticateResult.NoResult());
        var actor = replication.Authenticate(tenant, store, device, header[7..]);
        if (actor == null) return Task.FromResult(AuthenticateResult.Fail("Credencial de dispositivo inválida o revocada."));
        Context.Items["device"] = actor;
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor.DeviceId.ToString())], Scheme.Name)), Scheme.Name)));
    }
}
