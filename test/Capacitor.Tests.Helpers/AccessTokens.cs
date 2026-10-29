using System.Buffers.Text;
using System.Text;

namespace Capacitor.Tests.Helpers;

/// <summary>
/// Access tokens shaped like the ones a client reads claims out of. The signature is a placeholder:
/// a client never validates it — the server does over JWKS — so a real key would prove nothing, but
/// the payload must be genuine base64url JSON, because the claim reader really decodes it.
/// </summary>
public static class AccessTokens {
    /// <summary>A token whose <c>sub</c> claim names <paramref name="subject"/> — the account a
    /// stored credential authenticates as.</summary>
    public static string ForSubject(string subject) =>
        $"{Segment("""{"alg":"RS256","typ":"JWT"}""")}.{Segment($$"""{"sub":"{{subject}}"}""")}.signature";

    static string Segment(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
}
