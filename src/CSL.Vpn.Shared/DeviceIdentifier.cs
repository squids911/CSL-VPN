namespace CslVpn.Shared;

/// <summary>
/// A stable, randomly generated identifier for one device installation identity.
/// The caller must persist it securely; this helper does not store it.
/// </summary>
public static class DeviceIdentifier
{
    public static Guid Create() => Guid.NewGuid();

    public static bool TryParse(string? value, out Guid identifier)
    {
        if (Guid.TryParseExact(value, "D", out identifier) && identifier != Guid.Empty)
        {
            return true;
        }

        identifier = Guid.Empty;
        return false;
    }
}
