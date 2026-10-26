namespace Club.Server;

public sealed record Account(string Id, string Username, string Role, string PasswordHash, long BalanceMinor, bool Disabled = false, long AuthRevision = 0);
public sealed record PasswordChangeRequest(string CurrentPassword, string NewPassword, string ConfirmPassword);
public sealed record RegisterRequest(string Username, string Password);
public sealed record OwnerSetupRequest(string Username, string Password, string ConfirmPassword);
public sealed record ComputerRequest(string Name, string OperationId);
public sealed record PackageRequest(string? Id, string Name, int Minutes, long PriceMinor, bool Active, string OperationId);
public sealed record TopUpRequest(string CustomerId, long AmountMinor, string Method, string OperationId);
public sealed record PurchaseRequest(string ComputerId, string PackageId, string OperationId);
public sealed record SessionActionRequest(string Reason, string OperationId);
public sealed record FreeSessionRequest(string CustomerId, string ComputerId, int Minutes, string Reason, string OperationId);
public sealed record RefundRequest(string SessionId, long AmountMinor, string Method, string Reason, string OperationId);
public sealed record SessionRow(string Id, string CustomerId, string ComputerId, string PackageName,
    long PriceMinor, long TotalMs, long RemainingMs, string Status, long LastTickMs, long CreatedMs);
public sealed class RuleException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed record StaffAccessRequest(bool Disabled, string Reason);
public sealed record StaffPasswordRequest(string NewPassword, string ConfirmPassword, string Reason);

public sealed record CustomerPasswordRequest(string NewPassword, string ConfirmPassword, string Reason);
