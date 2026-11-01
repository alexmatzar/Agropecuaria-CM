using System.Security.Claims;
using System.Text.Encodings.Web;
using Agropecuaria.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Agropecuaria.LocalApi;

public sealed class OperatorAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, IOperatorAccounts accounts, CloudOperatorService cloud, IdentityMode mode)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());
        var token = header[7..];
        var user = mode.LegacyDevelopment ? accounts.Authenticate(token) : cloud.Authenticate(token);
        if (user == null) return Task.FromResult(AuthenticateResult.Fail("Sesión inválida o vencida."));
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role.ToString()), new Claim("display_name", user.DisplayName),
            new Claim("password_change_required", user.MustChangePassword ? "true" : "false")
        };
        Context.Items["operator"] = user;
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
    }
}
