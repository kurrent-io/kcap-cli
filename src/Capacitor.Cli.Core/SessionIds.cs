using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Cli.Core;

/// The session id the server files a session under: a GUID in any spelling collapses to its 32-hex
/// form, and an opaque vendor id (an ACP `sess-1`) is kept as written — stripping its dashes would
/// name a session the server has never seen.
public static class SessionIds {
    public static string? Canonical(string? id) {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var trimmed = id.Trim();
        return Guid.TryParse(trimmed, out var g) ? g.ToString("N") : trimmed;
    }

    /// Whether the session id a server reported names the session sent as <paramref name="sent"/>. A server
    /// with ACP id encoding on files a hyphenated opaque id under a token of it, and reports that token.
    public static bool SameSession(string? reported, string? sent) =>
        Canonical(reported) is { } id && Canonical(sent) is { } own && (id == own || id == ServerToken(own));

    /// Must match the server's encoding byte for byte: `senc1` and the first 27 lowercase base32 characters
    /// of the id's SHA-256.
    public static string ServerToken(string id) {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        var token = new StringBuilder("senc1", 32);
        int buffer = 0, bits = 0;
        foreach (var b in digest) {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5 && token.Length < 32) {
                bits -= 5;
                token.Append(alphabet[(buffer >> bits) & 31]);
            }
        }
        return token.ToString();
    }
}
