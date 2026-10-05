using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Cli.Harness.GrokBot;

/// <summary>
/// Cuts one Bot's endless thread into sessions: a session opens at the first entry after an idle gap and
/// ends once the gap has passed with no new entry and no turn running. Boundaries depend only on entry
/// timestamps, so a restarted poller derives the same sessions with the same ids.
///
/// <para>An entry is sent only once it has settled — not streaming, and a question or permission ask only
/// once answered or followed by a later entry — because the server keeps the first copy of a line number and
/// drops a resend. An entry older than the gap counts as settled whatever its state.</para>
/// </summary>
public static class GrokBotSessionizer {
    public static IReadOnlyList<GrokBotAction> Plan(
            string                    agentId,
            GrokBotBotState           state,
            IEnumerable<GrokBotEntry> entries,
            long                      nowMs,
            bool                      isRunningTurn,
            TimeSpan                  gap,
            int                       maxBatch = 100
        ) {
        var gapMs   = (long)gap.TotalMilliseconds;
        var actions = new List<GrokBotAction>();
        var lines   = new List<string>();
        var seqs    = new List<int>();
        var pending = state.LastSeq;

        var fresh = entries.Where(e => e.Seq > state.LastSeq).DistinctBy(e => e.Seq).OrderBy(e => e.Seq).ToList();

        for (var i = 0; i < fresh.Count; i++) {
            var entry   = fresh[i];
            var hasNext = i < fresh.Count - 1;
            var stale   = nowMs - entry.TimestampMs >= gapMs;

            if (!stale && (entry.IsStreaming || (entry.IsAwaitingAnswer && !hasNext))) break;

            if (state.Open is { } open && entry.TimestampMs - open.LastEntryMs >= gapMs) {
                Flush();
                state = state with { Open = null };
                actions.Add(new GrokBotEndSession(open.SessionId, open.LastEntryMs, state));
            }

            if (state.Open is null) {
                state = state with { Open = new GrokBotOpenSession(SessionId(agentId, entry.Id), entry.Id, entry.TimestampMs, entry.TimestampMs) };
                actions.Add(new GrokBotStartSession(state.Open.SessionId, entry.TimestampMs, state));
            }

            lines.Add(entry.Line);
            seqs.Add(entry.Seq);
            pending = entry.Seq;
            state   = state with { Open = state.Open with { LastEntryMs = entry.TimestampMs } };

            if (lines.Count >= maxBatch) Flush();
        }

        Flush();

        if (!isRunningTurn && state.Open is { } idle && nowMs - idle.LastEntryMs >= gapMs) {
            state = state with { Open = null };
            actions.Add(new GrokBotEndSession(idle.SessionId, idle.LastEntryMs, state));
        }

        return actions;

        void Flush() {
            if (lines.Count == 0) return;

            state = state with { LastSeq = pending };
            actions.Add(new GrokBotSendLines(state.Open!.SessionId, [.. lines], [.. seqs], state));
            lines.Clear();
            seqs.Clear();
        }
    }

    /// <summary>32 lowercase hex: a session id the server accepts, derived from data a restarted poller
    /// re-reads. Persisted as the session's identity, so the derivation never changes.</summary>
    public static string SessionId(string agentId, string firstEntryId) {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"grok-bot:{agentId}:{firstEntryId}"));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }
}
