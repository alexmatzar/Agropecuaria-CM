using Agropecuaria.Application;
using FirebaseAdmin.Auth;

namespace Agropecuaria.Cloud.Api;

public sealed class FirebaseAccountProvider(FirebaseAuth firebase) : ICloudAccountProvider
{
    public async Task EnsureAsync(ProvisioningTicket ticket, string temporaryPassword, CancellationToken ct)
    {
        try
        {
            if (await Matches(ticket, ct)) return;
            try
            {
                await firebase.CreateUserAsync(new UserRecordArgs
                { Uid = ticket.FirebaseUid, Email = ticket.Email, DisplayName = ticket.DisplayName, Password = temporaryPassword }, ct);
            }
            catch (FirebaseAuthException ex) when (ex.AuthErrorCode is AuthErrorCode.UidAlreadyExists or AuthErrorCode.EmailAlreadyExists)
            {
                if (!await Matches(ticket, ct)) throw new CloudAccountConflict();
                // Retry after a lost response: never reset an existing identity's password.
            }
        }
        catch (FirebaseAuthException) { throw new CloudUnavailable("No se completó el proveedor de cuentas; la solicitud permanece registrada para reintentar."); }
        catch (HttpRequestException) { throw new CloudUnavailable(); }
    }
    private async Task<bool> Matches(ProvisioningTicket ticket, CancellationToken ct)
    {
        UserRecord existing;
        try { existing = await firebase.GetUserAsync(ticket.FirebaseUid, ct); }
        catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound) { return false; }
        if (existing.Disabled || !string.Equals(existing.Email, ticket.Email, StringComparison.OrdinalIgnoreCase)) throw new CloudAccountConflict();
        return true;
    }
}
