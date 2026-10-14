using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Cli.Harness.MuseAssistant;

/// <summary>32 lowercase hex, derived from data every run re-reads. Persisted as the session's identity
/// on the server, so the derivation never changes.</summary>
public static class MuseAssistantSessionId {
    public static string For(string userId, string chatId, string grantId, long firstSeq) {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"muse-assistant:{userId}:{chatId}:{grantId}:{firstSeq}"));

        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }
}
