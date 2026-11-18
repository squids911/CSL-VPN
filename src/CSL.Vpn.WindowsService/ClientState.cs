namespace CSL.Vpn.WindowsService;

/// <summary>
/// Volatile client status snapshot, mirrored to a JSON state file so the
/// tray app (and operators) can see what the service is doing.
/// </summary>
public sealed class ClientState
{
    public string Status { get; private set; } = "starting";

    public string? Address { get; private set; }

    public Guid? SessionId { get; private set; }

    public string? Message { get; private set; }

    public DateTimeOffset? LastHeartbeatUtc { get; private set; }

    /// <summary>Data-plane state: "up", "down", "error", or null when the tunnel is disabled.</summary>
    public string? Tunnel { get; private set; }

    public void SetTunnel(string? tunnel)
    {
        Tunnel = tunnel;
    }

    public void Update(
        string status,
        string? address = null,
        Guid? sessionId = null,
        string? message = null,
        bool heartbeat = false)
    {
        Status = status;
        Address = address ?? Address;
        SessionId = sessionId ?? SessionId;
        Message = message;
        if (heartbeat)
        {
            LastHeartbeatUtc = DateTimeOffset.UtcNow;
        }
    }

    public object Snapshot() => new
    {
        status = Status,
        address = Address,
        sessionId = SessionId,
        message = Message,
        lastHeartbeatUtc = LastHeartbeatUtc,
        tunnel = Tunnel,
    };
}
