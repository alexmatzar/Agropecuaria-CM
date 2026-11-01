using Agropecuaria.Contracts;
using Agropecuaria.Domain;

namespace Agropecuaria.Application;

public sealed record OperatorIdentity(Guid Id, string Username, string DisplayName, OperatorRole Role, bool MustChangePassword);
public interface IOperatorAccounts
{
    UserView BootstrapAdministrator(string username, string displayName, string password);
    LoginResult Login(LoginRequest request);
    OperatorIdentity? Authenticate(string token);
    void Logout(string token);
    IReadOnlyList<UserView> ListUsers(OperatorIdentity actor);
    UserView CreateUser(OperatorIdentity actor, CreateUserRequest request);
    UserView UpdateUser(OperatorIdentity actor, Guid userId, UpdateUserRequest request);
    void ResetPassword(OperatorIdentity actor, Guid userId, string newPassword);
    void ChangePassword(OperatorIdentity actor, ChangePasswordRequest request);
    IReadOnlyList<AuditView> Audit(OperatorIdentity actor);
}
