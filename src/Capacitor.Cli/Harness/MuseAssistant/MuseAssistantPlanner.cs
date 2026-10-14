using System.Text.Json;

namespace Capacitor.Cli.Harness.MuseAssistant;

/// <summary>
/// Cuts a chat's items into sessions. Between items the boundary is a pure function of the data: a gap of
/// <see cref="Gap"/> or more between consecutive selected items. Idle closure compares a VM-clock fetch time
/// with a store-clock item time, so it carries a 30-minute margin: with skew under that it never closes a
/// session a later item would have continued.
/// </summary>
public static class MuseAssistantPlanner {
    public static readonly TimeSpan Gap       = TimeSpan.FromHours(2);
    public static readonly TimeSpan IdleClose = TimeSpan.FromMinutes(150);
    public const int MaxBatchLines = 100;
    public const int MaxBatchBytes = 4 * 1024 * 1024;
    /// <summary>What a batch's lines may add up to: the cap less room for the rest of the request — the
    /// session id, up to <see cref="MaxBatchLines"/> line numbers below 1e9 and the fixed fields, about 1.2 KB.</summary>
    public const int MaxLinesBytes = MaxBatchBytes - 4 * 1024;

    /// <summary>When <paramref name="open"/> last had a selected item. The state's last-selected time can
    /// still be the previous session's — a run that stopped after this session's start, or its first batch,
    /// was accepted — so it never counts from before this session began.</summary>
    public static long SessionLastSelectedAtMs(MuseAssistantChatState state, MuseAssistantOpenSession open) =>
        Math.Max(state.LastSelectedAtMs ?? open.StartedAtMs, open.StartedAtMs);

    /// <summary>The end timestamp to post for <paramref name="open"/>: the recorded one, else its last
    /// selected item's.</summary>
    public static long SessionEndedAtMs(MuseAssistantChatState state, MuseAssistantOpenSession open) =>
        open.EndedAtMs ?? SessionLastSelectedAtMs(state, open);

    /// <summary>What a line adds to a transcript batch request: its JSON-escaped form as a string value
    /// (the serializer's default encoder, so a non-ASCII character costs its <c>\uXXXX</c> escape, not its
    /// UTF-8 bytes), the quotes around it and the comma after it.</summary>
    public static int WireSize(string line) => JsonEncodedText.Encode(line).EncodedUtf8Bytes.Length + 3;

    /// <summary>Null when every item the walk would send fits in a batch of its own; otherwise names the
    /// first one that does not, which no split can help.</summary>
    public static string? OversizeProblem(MuseAssistantWalk walk, MuseAssistantChatState state) {
        foreach (var item in walk.Items) {
            if (state.Watermark is { } w && item.Seq <= w) continue;
            if (!MuseAssistantRoles.IsSelected(item.Role) || state.Grant.Floor is { } f && item.Seq <= f) continue;

            if (WireSize(item.Line) is var size && size > MaxLinesBytes)
                return $"item seq {item.Seq} in turn {item.TurnId} is too large to send ({size} bytes; a request carries at most 4 MiB)";
        }

        return null;
    }

    /// <summary>Null when the walk reaches the chat's beginning, the watermark, or — before any
    /// watermark — the grant's floor; otherwise why the watermark may not advance.</summary>
    public static string? CoverageProblem(MuseAssistantWalk walk, MuseAssistantChatState state) {
        if (!walk.OldestHasMore) return null;

        var floor = state.Watermark ?? state.Grant.Floor;
        if (floor is { } f && walk.OldestPageMinSeq is { } min && min <= f) return null;

        return (state.Watermark, state.Grant.Floor) switch {
            (null, null) => "the walk stopped before the chat's beginning (the oldest page has has_more:true) and this grant covers the whole chat",
            (null, _)    => "the walk stopped before reaching the grant's after_seq (the oldest page has has_more:true and no item at or below it)",
            _            => "the walk stopped before reaching the watermark (the oldest page has has_more:true and no item at or below it)"
        };
    }

    public static IReadOnlyList<MuseAssistantAction> Plan(MuseAssistantChatState state, MuseAssistantWalk walk, string userId, string homeDir) {
        var actions = new List<MuseAssistantAction>();
        var gapMs   = (long)Gap.TotalMilliseconds;
        var idleMs  = (long)IdleClose.TotalMilliseconds;

        state = Recover(state, actions);

        var from    = state.Watermark;
        var floor   = state.Grant.Floor;
        var lines   = new List<string>();
        var seqs    = new List<int>();
        var bytes   = 0;
        var covered = state.Watermark;
        // Tracks the state's last-selected time as if every line so far were accepted.
        var lastSel = state.Open is { } current ? SessionLastSelectedAtMs(state, current) : state.LastSelectedAtMs;

        foreach (var item in walk.Items) {
            if (from is { } w && item.Seq <= w) continue;

            var selected = MuseAssistantRoles.IsSelected(item.Role) && (floor is not { } f || item.Seq > f);
            if (!selected) {
                covered = item.Seq;
                continue;
            }

            if (state.Open is { } open && lastSel is { } last && item.OccurredAtMs - last >= gapMs) {
                Flush();
                state = End(state, open, last, actions);
            }

            if (state.Open is null) {
                var session = new MuseAssistantOpenSession(
                    MuseAssistantSessionId.For(userId, walk.ChatId, state.Grant.Id, item.Seq),
                    item.Seq, item.OccurredAtMs, walk.IsMain, homeDir, MuseAssistantSessionPhases.PendingStart, null);
                var intent = state with { Open = session };
                state = state with { Open = session with { Phase = MuseAssistantSessionPhases.Started } };
                actions.Add(new MuseAssistantStartSession(session, intent, state));
            }

            var size = WireSize(item.Line);
            if (lines.Count > 0 && (lines.Count >= MaxBatchLines || bytes + size > MaxLinesBytes)) Flush();

            lines.Add(item.Line);
            seqs.Add(checked((int)item.Seq));
            bytes  += size;
            covered = item.Seq;
            lastSel = item.OccurredAtMs;
        }

        Flush();

        if (covered != state.Watermark) {
            state = state with { Watermark = covered };
            actions.Add(new MuseAssistantAdvanceWatermark(state));
        }

        if (state.Open is { } idle && lastSel is { } at && walk.FetchedAtMs - at >= idleMs)
            End(state, idle, at, actions);

        return actions;

        void Flush() {
            if (lines.Count == 0) return;

            state = state with { Watermark = covered, LastSelectedAtMs = lastSel };
            actions.Add(new MuseAssistantSendLines(state.Open!.SessionId, [.. lines], [.. seqs], state));
            lines.Clear();
            seqs.Clear();
            bytes = 0;
        }
    }

    /// <summary>Ends whatever the state holds open — replaying a pending start first, because an empty
    /// session with both lifecycle events is deterministic where a dangling start is not.</summary>
    public static IReadOnlyList<MuseAssistantAction> Closure(MuseAssistantChatState state) {
        var actions = new List<MuseAssistantAction>();
        if (state.Open is null) return actions;

        state = Recover(state, actions);
        if (state.Open is { } open) End(state, open, SessionEndedAtMs(state, open), actions);

        return actions;
    }

    /// <summary>Only what a crashed run left recorded: re-post a pending start, complete a pending end.</summary>
    public static IReadOnlyList<MuseAssistantAction> Replay(MuseAssistantChatState state) {
        var actions = new List<MuseAssistantAction>();
        Recover(state, actions);

        return actions;
    }

    static MuseAssistantChatState Recover(MuseAssistantChatState state, List<MuseAssistantAction> actions) {
        switch (state.Open) {
            case { Phase: MuseAssistantSessionPhases.PendingStart } pending:
                state = state with { Open = pending with { Phase = MuseAssistantSessionPhases.Started } };
                actions.Add(new MuseAssistantStartSession(pending, Intent: null, After: state));
                return state;
            case { Phase: MuseAssistantSessionPhases.PendingEnd } ending:
                var endedAtMs = SessionEndedAtMs(state, ending);
                state = state with { Open = null };
                actions.Add(new MuseAssistantEndSession(ending, endedAtMs, Intent: null, After: state));
                return state;
            default:
                return state;
        }
    }

    static MuseAssistantChatState End(MuseAssistantChatState state, MuseAssistantOpenSession open, long endedAtMs, List<MuseAssistantAction> actions) {
        var ended  = open with { Phase = MuseAssistantSessionPhases.PendingEnd, EndedAtMs = endedAtMs };
        var intent = state with { Open = ended };
        var after  = state with { Open = null };
        actions.Add(new MuseAssistantEndSession(ended, endedAtMs, intent, after));

        return after;
    }
}
