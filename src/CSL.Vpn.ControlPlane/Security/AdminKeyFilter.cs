using System.Security.Cryptography;
using System.Text;

namespace CSL.Vpn.ControlPlane.Security;

/// <summary>
/// Requires the X-Admin-Key header to match the configured AdminApiKey.
/// Keys are compared as SHA-256 hashes in constant time. When AdminApiKey
/// is not configured, the admin surface stays disabled.
/// </summary>
public sealed class AdminKeyFilter(IConfiguration configuration) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var configuredKey = configuration["AdminApiKey"];
        if (string.IsNullOrEmpty(configuredKey))
        {
            return Results.Json(
                new { error = "admin_surface_disabled" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var providedKey = context.HttpContext.Request.Headers["X-Admin-Key"].ToString();
        var authorized = providedKey.Length > 0
            && CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(providedKey)),
                SHA256.HashData(Encoding.UTF8.GetBytes(configuredKey)));

        return authorized
            ? await next(context)
            : Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);
    }
}
