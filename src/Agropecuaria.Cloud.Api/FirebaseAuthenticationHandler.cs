using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Agropecuaria.Application;
using FirebaseAdmin.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Agropecuaria.Cloud.Api;

public sealed class FirebaseAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, FirebaseAuth firebase)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || header.Length > 16_384)
            return AuthenticateResult.NoResult();
        if (string.IsNullOrWhiteSpace(header[7..])) return AuthenticateResult.Fail("Falta token.");
        try
        {
            var token = await firebase.VerifyIdTokenAsync(header[7..], checkRevoked: true, Context.RequestAborted);
            var record = await firebase.GetUserAsync(token.Uid, Context.RequestAborted);
            if (record.Disabled) return AuthenticateResult.Fail("Cuenta no habilitada.");
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, token.Uid),
                new Claim("auth_time", Convert.ToInt64(token.Claims["auth_time"], CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture))
            ], Scheme.Name)), Scheme.Name));
        }
        catch (FirebaseAuthException ex) when (ex.AuthErrorCode is AuthErrorCode.InvalidIdToken or AuthErrorCode.ExpiredIdToken
            or AuthErrorCode.RevokedIdToken or AuthErrorCode.UserNotFound or AuthErrorCode.TenantIdMismatch)
        { return AuthenticateResult.Fail("Token de Firebase inválido o revocado."); }
        // A provider/certificate/network failure is not a revocation. Preserve the existing
        // offline lease on the POS, without granting or renewing any authorization.
        catch (FirebaseAuthException) { throw new CloudUnavailable(); }
        catch (HttpRequestException) { throw new CloudUnavailable(); }
        catch (ArgumentException) { return AuthenticateResult.Fail("Token inválido."); }
    }
}
