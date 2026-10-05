using System.Text.Json;

namespace Capacitor.Cli.Core.Auth;

/// Reads claims out of a JWT payload WITHOUT validating the signature — the server validates via
/// JWKS; a client only ever uses these values for display, diagnostics and row classification,
/// never for authorization.
public static class JwtClaims {
    public static string? TryGetString(string accessToken, string claimName) =>
        TryReadPayload(accessToken, root => root.Str(claimName));

    /// <summary>A NumericDate claim (<c>exp</c>, <c>iat</c>), or null when absent or not a number.</summary>
    public static DateTimeOffset? TryGetTime(string accessToken, string claimName) =>
        TryReadPayload(accessToken, root => root.Num(claimName) is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : (DateTimeOffset?)null);

    static T? TryReadPayload<T>(string accessToken, Func<JsonElement, T?> read) {
        var parts = accessToken.Split('.');
        if (parts.Length < 2) return default;

        try {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = (payload.Length % 4) switch {
                2 => payload + "==",
                3 => payload + "=",
                1 => throw new FormatException("truncated base64url"),
                _ => payload,
            };
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return read(doc.RootElement);
        } catch (Exception e) when (e is FormatException or JsonException or ArgumentOutOfRangeException) {
            return default;
        }
    }
}
