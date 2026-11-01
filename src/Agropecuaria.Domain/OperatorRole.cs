namespace Agropecuaria.Domain;

public enum OperatorRole { Admin, Cashier, Viewer }
public sealed class LoginRejected() : Exception("Credenciales inválidas o acceso temporalmente bloqueado.");
public sealed class AccessDenied() : Exception("Operación no autorizada.");
