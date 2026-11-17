namespace CslVpn.Shared.Contracts;

/// <summary>Shared control-plane DTOs used by both the API and the Windows client.</summary>
public sealed record EnrollRequest(string? Code, string? DisplayName, Guid? DeviceId);

public sealed record EnrollResponse(Guid DeviceId, string DisplayName, string Address, string Credential);

public sealed record HeartbeatResponse(
    Guid SessionId,
    DateTimeOffset StartedAt,
    DateTimeOffset LastHeartbeat,
    string? Address,
    string Status);

public sealed record DeviceStatusResponse(
    Guid DeviceId,
    string DisplayName,
    DateTimeOffset EnrolledAt,
    DateTimeOffset? LastSeenAt,
    string? Address,
    bool SessionActive);

public sealed record ErrorResponse(string Error);
