namespace Agropecuaria.Contracts;

// These DTOs contain no database or domain implementation dependencies.
public sealed record PriceInput(string Unit, long UnitPriceMinor);
public sealed record CreateProductRequest(Guid OperationId, Guid ProductId, string Name,
    string Dimension, string OpeningUnit, decimal OpeningQuantity, List<PriceInput> Prices, ProductDetails? Details = null);
public sealed record ProductView(Guid Id, string Name, string Dimension, string BaseUnit,
    long StockBase, List<PriceInput> Prices, ProductDetails? Details = null);
public sealed record SaleLineInput(Guid ProductId, string Unit, decimal Quantity);
public sealed record CreateSaleRequest(Guid OperationId, List<SaleLineInput> Lines);
public sealed record SaleResult(Guid SaleId, long TotalMinor, string Currency, DateTimeOffset RecordedAtUtc);
public sealed record SyncEnvelope(Guid EventId, Guid TenantId, Guid StoreId, Guid DeviceId,
    long Sequence, int SchemaVersion, string Kind, string Payload, DateTimeOffset CreatedAtUtc);
public sealed record SyncAcknowledgement(Guid EventId);
public sealed record SyncStatus(string State, long PendingCount, DateTimeOffset? LastSuccessUtc,
    string? ErrorCode);

public sealed record LoginRequest(string Username, string Password);
public sealed record LoginResult(string AccessToken, DateTimeOffset ExpiresAtUtc, UserView User);
public sealed record UserView(Guid Id, string Username, string DisplayName, string Role, bool IsActive, bool MustChangePassword);
public sealed record CreateUserRequest(string Username, string DisplayName, string Role, string TemporaryPassword, Guid? OperationId = null);
public sealed record CloudAuditView(long Sequence, string ActorUid, string Action, Guid? SubjectId, Guid? OperationId, DateTimeOffset AtUtc, string DetailsJson);
public sealed record UpdateUserRequest(string Role, bool IsActive);
public sealed record ResetPasswordRequest(string TemporaryPassword);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record AuditView(long Sequence, Guid? ActorId, string Action, Guid? SubjectId, DateTimeOffset AtUtc);
public sealed record CloudAuthorizationRequest(Guid TenantId, Guid StoreId, Guid DeviceId);
public sealed record StoreAccess(Guid TenantId, Guid StoreId, string Role);
public sealed record CloudGrant(Guid TenantId, Guid StoreId, string FirebaseUid, UserView User,
    long MembershipVersion, DateTimeOffset ValidatedAtUtc, DateTimeOffset OfflineUntilUtc);
