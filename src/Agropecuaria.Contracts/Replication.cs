namespace Agropecuaria.Contracts;

public sealed record RegisterDeviceRequest(Guid DeviceId, bool CanWrite, bool ReplaceWriter, bool HasLocalData, string Secret);
public sealed record EnrollSyncRequest(bool CanWrite, bool ReplaceWriter);
public sealed record RegisteredDevice(Guid DeviceId, string Secret, bool CanWrite, long HeadCursor);
public sealed record CloudChange(long Cursor, SyncEnvelope Envelope);
public sealed record CloudChangePage(long FromCursor, long ThroughCursor, long HeadCursor, List<CloudChange> Changes);
public sealed record SaleLineSnapshot(Guid ProductId, string Name, string Unit, decimal Quantity,
    long QuantityBase, long UnitPriceMinor, long TotalMinor);
public sealed record SaleCompletedPayload(SaleResult Sale, Guid? OperatorId, List<SaleLineSnapshot> Lines);
public sealed record LegacyRecordPayload(string SourceHash, string TableName, string RowKey, string RecordJson);
public sealed record ProductDetails(string? Barcode, string? Type, string? Brand, string? Description);
