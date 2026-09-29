using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>The slice of an agent the title resolver reads; snapshotted per tick.</summary>
internal sealed record TitleAgentView(
    string Id, string Vendor, string? Prompt, string? SessionId, string? TranscriptPath, DateTime CreatedAt);

/// <summary>The server's title surface for one session: read the current title, push a
/// harness-native one (with its kind and change time) via the harness-title path.</summary>
internal interface ITitleServerPort {
    Task<string?> GetTitleAsync(string sessionId, CancellationToken ct);
    Task<HarnessTitleOutcome> PushTitleAsync(string sessionId, HarnessTitlePost post, CancellationToken ct);
}

/// <summary>
/// Resolves a display title per hosted agent, one ladder per tick: the vendor's native
/// transcript title first, the server's real title as the authority once one exists, and a
/// single local generation as the late fallback.
///
/// <para>The native lane's (title, kind, changed-at) is pushed to the server whenever it
/// changes — a rename or a revised auto title — regardless of what the server currently holds:
/// the harness's own store is authoritative for what the harness itself calls the session,
/// independent of any title the server or another viewer has set. A locally generated title,
/// by contrast, only converges to the server while the server verifiably has no title of its
/// own and no native title exists — generation is a fallback, not an authority.</para>
///
/// <para>A server title that merely echoes the launch prompt is the watcher's initial
/// truncated-prompt title, not a real one: adopting it would overwrite a better native title
/// with the string the seed already shows, and treating it as real would block both the
/// generated push and the generation fallback.</para>
///
/// <para>The ladder never downgrades: a lane that stops producing (a transient read failure,
/// a server hiccup) keeps the last applied title rather than blanking it.</para>
/// </summary>
internal sealed class TitleResolveLoop {
    /// Generation costs a headless LLM call, and for a recorded session the watcher is already
    /// making one — it typically lands within a minute. Generate only after the server has
    /// stayed silent this long.
    static readonly TimeSpan GenerationGrace = TimeSpan.FromMinutes(5);

    sealed class AgentTitleState {
        public string? Applied;
        /// Latest non-null native title, with its kind and change time. Cached so a transient
        /// extraction gap (an unreadable file moment) cannot demote the ladder to the generated
        /// fallback.
        public HarnessTitlePost? Native;
        public string? Generated;
        /// The exact native post last successfully pushed — a later native read equal to this
        /// is not re-pushed.
        public HarnessTitlePost? PushedNative;
        /// The exact generated title last successfully pushed.
        public string? PushedGenerated;
        /// Every title a push was ATTEMPTED with, native or generated, confirmed or not. An
        /// attempt whose response was lost may still have committed and surface on a later
        /// read — even after newer attempts — so each must keep counting as "ours" when the
        /// server echoes it, or the loop would adopt its own stale title as independent
        /// authority.
        public readonly BoundedAttemptSet PushAttempts = new();
        /// The authoritative server title as of the last SUCCESSFUL read. Held across failed
        /// reads so an outage tick cannot demote the applied title down the ladder; cleared
        /// only by a successful read proving the server silent.
        public string? ServerTitle;
        public bool GenerationAttempted;
        /// The transcript path, length and last-write time the native lane was last invoked
        /// against — a match skips re-invoking it (a JSON parse of the whole file) this tick.
        /// Scoped to this agent's state so it is dropped with the agent rather than growing
        /// unbounded across a long-lived daemon's lifetime-total sessions.
        public string? NativeStatPath;
        public long NativeStatLength;
        public DateTime NativeStatLastWriteUtc;
        public HarnessTitlePost? NativeStatResult;
    }

    /// <summary>
    /// Push-attempt provenance with bounded memory: at capacity, adding a NEW title evicts only
    /// the oldest one, and re-adding a known title evicts nothing. A native title revises at
    /// most once per tick, so the window only re-opens an echo more than 32 revisions stale.
    /// </summary>
    internal sealed class BoundedAttemptSet {
        const int Capacity = 32;

        readonly Queue<string> _order = new();
        readonly HashSet<string> _set = new(StringComparer.Ordinal);

        public bool Contains(string title) => _set.Contains(title);

        public void Add(string title) {
            if (!_set.Add(title)) return;

            _order.Enqueue(title);
            if (_order.Count > Capacity) _set.Remove(_order.Dequeue());
        }
    }

    readonly Func<IReadOnlyList<TitleAgentView>> _agents;
    readonly Action<string, string> _apply;
    readonly ITitleServerPort _server;
    readonly Func<TitleAgentView, HarnessTitlePost?> _nativeLane;
    readonly Func<TitleAgentView, CancellationToken, Task<string?>> _generateLane;
    readonly Func<string, string, CancellationToken, Task<bool>> _postGenerated;
    readonly TimeProvider _time;
    readonly ILogger _logger;
    readonly Dictionary<string, AgentTitleState> _states = [];

    public TitleResolveLoop(
            Func<IReadOnlyList<TitleAgentView>> agents,
            Action<string, string> apply,
            ITitleServerPort server,
            Func<TitleAgentView, HarnessTitlePost?> nativeLane,
            Func<TitleAgentView, CancellationToken, Task<string?>> generateLane,
            Func<string, string, CancellationToken, Task<bool>> postGenerated,
            TimeProvider time,
            ILogger logger) {
        _agents        = agents;
        _apply         = apply;
        _server        = server;
        _nativeLane    = nativeLane;
        _generateLane  = generateLane;
        _postGenerated = postGenerated;
        _time          = time;
        _logger        = logger;
    }

    public async Task TickAsync(CancellationToken ct) {
        var agents = _agents();

        var live = new HashSet<string>(agents.Select(a => a.Id), StringComparer.Ordinal);
        foreach (var gone in _states.Keys.Where(id => !live.Contains(id)).ToList()) _states.Remove(gone);

        foreach (var agent in agents) {
            ct.ThrowIfCancellationRequested();

            if (!_states.TryGetValue(agent.Id, out var state)) _states[agent.Id] = state = new AgentTitleState();

            try {
                await ResolveOneAsync(agent, state, ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Title resolution failed for agent {AgentId} — keeping current title", agent.Id);
            }
        }
    }

    async Task ResolveOneAsync(TitleAgentView agent, AgentTitleState state, CancellationToken ct) {
        try {
            if (ExtractNative(agent, state) is { } extracted) state.Native = extracted;
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Native title extraction failed for agent {AgentId}", agent.Id);
        }
        var native = state.Native;

        var (serverReadOk, serverReal) = await ReadServerAsync(agent, state, ct);

        // An unreadable server is not a silent one: generation must not spend an LLM call on a
        // session whose watcher-made title merely couldn't be fetched.
        if (serverReadOk && serverReal is null && native is null && !state.GenerationAttempted
         && !string.IsNullOrWhiteSpace(agent.Prompt)
         && _time.GetUtcNow() - DateTime.SpecifyKind(agent.CreatedAt, DateTimeKind.Utc) >= GenerationGrace) {
            state.GenerationAttempted = true;
            try {
                state.Generated = Normalize(await _generateLane(agent, ct));
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Title generation failed for agent {AgentId}", agent.Id);
            }

            // The model call runs long enough for an independent title to land meanwhile —
            // re-check so the fallback can neither display over it nor converge over it. A
            // failed re-check blocks the push (not the local display) until the next tick.
            if (state.Generated is not null) (serverReadOk, serverReal) = await ReadServerAsync(agent, state, ct);
        }

        // On a failed read the last successfully-read authority stands in, so an outage tick
        // cannot demote the applied title down the ladder.
        var best = (serverReadOk ? serverReal : state.ServerTitle) ?? Normalize(native?.Title) ?? state.Generated;

        if (best is not null && best != state.Applied) {
            _apply(agent.Id, best);
            state.Applied = best;
        }

        // The harness's own title is pushed whenever it changes, independent of the server's
        // current title — it is authoritative for what the harness itself calls the session.
        if (native is not null && !native.Equals(state.PushedNative) && agent.SessionId is { } sid) {
            // A server read comes back display-capped, so the capped form is what an echo of this push looks like.
            state.PushAttempts.Add(Normalize(native.Title)!);

            var outcome = HarnessTitleOutcome.Failed;
            try {
                outcome = await _server.PushTitleAsync(sid, native, ct);
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Native title push failed for session {SessionId}", sid);
            }

            // A refusal (blank title, unsafe id, not the owner) is a verdict on this exact
            // value, not a transient hiccup — retrying it every tick forever would spend a
            // request for nothing. Failed/SessionNotFound stay retryable.
            if (outcome is HarnessTitleOutcome.Posted or HarnessTitleOutcome.Refused) state.PushedNative = native;
        }

        // A locally generated title only converges to the server while it verifiably has no
        // real title and no native title exists — generation is a fallback, not an authority.
        if (serverReadOk && serverReal is null && native is null && state.Generated is { } generated
         && generated != state.PushedGenerated && agent.SessionId is { } genSid) {
            state.PushAttempts.Add(generated);

            var posted = false;
            try {
                posted = await _postGenerated(genSid, generated, ct);
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Generated title push failed for session {SessionId}", genSid);
            }

            if (posted) state.PushedGenerated = generated;
        }
    }

    /// <summary>One server read plus the bookkeeping it settles: what counts as independent
    /// authority, the retained-across-outages <see cref="AgentTitleState.ServerTitle"/>, and
    /// whether the last confirmed push is still what the server holds.</summary>
    async Task<(bool Ok, string? Real)> ReadServerAsync(TitleAgentView agent, AgentTitleState state, CancellationToken ct) {
        if (agent.SessionId is not { } sessionId) return (true, null); // an unrecorded agent has no server to ask

        try {
            var serverTitle = Normalize(await _server.GetTitleAsync(sessionId, ct));

            // A title the loop itself pushed (or may have pushed — an unacknowledged attempt
            // can still have committed) coming back is not an independent server title:
            // treating it as one would freeze the ladder on our own echo and a later native
            // revision could never advance past it.
            string? serverReal = null;
            if (serverTitle is not null && !state.PushAttempts.Contains(serverTitle)
             && !IsPromptEcho(serverTitle, agent.Prompt)) {
                serverReal = serverTitle;
            }

            state.ServerTitle = serverReal;

            return (true, serverReal);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Server title read failed for session {SessionId}", sessionId);

            return (false, null);
        }
    }

    /// <summary>Invokes the native lane, skipping it when the agent's transcript file's length
    /// and last-write time still match the last invocation — the daemon polls every agent on a
    /// short tick, and a session between ticks almost never gains a new line, let alone a new
    /// title line. No stat to compare against (no transcript path, or the file is missing) just
    /// calls the lane every time.</summary>
    HarnessTitlePost? ExtractNative(TitleAgentView agent, AgentTitleState state) {
        if (agent.TranscriptPath is not { } path) return TrimPost(_nativeLane(agent));

        FileInfo? info;
        try {
            info = new FileInfo(path);
        } catch {
            info = null;
        }

        if (info is not { Exists: true }) {
            state.NativeStatPath = null;
            return TrimPost(_nativeLane(agent));
        }

        if (state.NativeStatPath == path && state.NativeStatLength == info.Length
         && state.NativeStatLastWriteUtc == info.LastWriteTimeUtc) {
            return state.NativeStatResult;
        }

        var result = TrimPost(_nativeLane(agent));
        state.NativeStatPath         = path;
        state.NativeStatLength       = info.Length;
        state.NativeStatLastWriteUtc = info.LastWriteTimeUtc;
        state.NativeStatResult       = result;

        return result;
    }

    static string? Normalize(string? title) {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var trimmed = title.Trim();
        return trimmed.Length > 120 ? trimmed[..120] : trimmed;
    }

    /// <summary>Trimmed but not capped: the pushed value must match what every other sender of the same rename
    /// sends, and the server applies its own clamp. The display cap belongs to <see cref="Normalize"/> alone.</summary>
    static HarnessTitlePost? TrimPost(HarnessTitlePost? post) =>
        post is null || string.IsNullOrWhiteSpace(post.Title) ? null : post with { Title = post.Title.Trim() };

    /// <summary>
    /// The watcher's initial title and the daemon's seed take exactly two forms: the launch
    /// prompt's first non-blank line verbatim (when short enough), or a prefix of it with a
    /// trailing ellipsis marking the cut. Only those forms are echoes — a bare prefix without
    /// the ellipsis can be a genuine generated title that happens to open like the prompt, and
    /// discarding it would trigger a duplicate local generation.
    /// </summary>
    internal static bool IsPromptEcho(string title, string? prompt) {
        if (string.IsNullOrWhiteSpace(prompt)) return false;

        var t         = title.TrimEnd();
        var truncated = false;
        if (t.EndsWith('…')) { t = t[..^1]; truncated = true; }
        else if (t.EndsWith("...", StringComparison.Ordinal)) { t = t[..^3]; truncated = true; }
        t = t.TrimEnd();

        if (t.Length == 0) return true;

        foreach (var raw in prompt.Split('\n')) {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            return truncated ? line.StartsWith(t, StringComparison.Ordinal) : line == t;
        }

        return false;
    }
}
