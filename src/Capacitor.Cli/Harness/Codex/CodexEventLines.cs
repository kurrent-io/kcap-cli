using System.Text.Json;

namespace Capacitor.Cli.Harness.Codex;

/// <summary>
/// Which Codex rollout lines the server's normalizer can turn into a stored event: the session header,
/// response items and compactions. <c>event_msg</c> and <c>turn_context</c> lines only adjust events the
/// same batch already holds, and every rollout turn ends with them, so they must not decide that a
/// session is unfinished.
/// </summary>
/// <remarks>Mirrors the server's Codex normalizer, erring towards yes: a wrong yes costs a re-sent tail,
/// a wrong no leaves a line unsent.</remarks>
internal static class CodexEventLines {
    public static bool CanAddEvents(string line) {
        try {
            using var doc = JsonDocument.Parse(line);

            return doc.RootElement.Str("type") is "session_meta" or "response_item" or "compacted";
        } catch (JsonException) {
            return false;
        }
    }
}
