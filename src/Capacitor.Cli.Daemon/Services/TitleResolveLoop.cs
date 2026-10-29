using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>The slice of an agent the title resolver reads; snapshotted per tick.</summary>
internal sealed record TitleAgentView(
    string Id, string Vendor, string? Prompt, string? SessionId, string? TranscriptPath, DateTime CreatedAt,
    AgentStartTitle? StartTitle = null);

/// <summary>The server's title surface for one session: read the current title, push a
/// harness-native one (with its kind and change time) via the harness-title path.</summary>
internal interface ITitleServerPort {
    Task<string?> GetTitleAsync(string sessionId, CancellationToken ct);
    Task<HarnessTitleOutcome> PushTitleAsync(string sessionId, HarnessTitlePost post, CancellationToken ct);
    Task<TitleRenameOutcome> RenameAsync(string sessionId, string title, CancellationToken ct);
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
/// <para>The agent's own start title is an echo in the same sense. When the caller chose it, the
/// server keeps it over every generated title but lets a rename replace it: native titles are
/// still pushed, generation is skipped, and the chosen title shows until a rename or another
/// server title replaces it.</para>
///
/// <para>The ladder never downgrades: a lane that stops producing (a transient read failure,
/// a server hiccup) keeps the last applied title rather than blanking it.</para>
/// </summary>
internal sealed class TitleResolveLoop {
    /// Generation costs a headless LLM call, and for a recorded session the watcher is already
    /// making one — it typically lands within a minute. Generate only after the server has
    /// stayed silent this long.
    static readonly TimeSpan GenerationGrace = TimeSpan.FromMinutes(5);

    /// Projection lag is seconds; the bound stops a rename the server superseded before showing
    /// it from being held forever.
    static readonly TimeSpan RenameLagHold = TimeSpan.FromMinutes(2);

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
        /// Set once any title other than the start title has been applied. From then on a server
        /// title equal to the start title is a rename back to it, not the registration echo.
        public bool StartTitleSuperseded;
        /// The transcript path, length and last-write time the native lane was last invoked
        /// against — a match skips re-invoking it (a JSON parse of the whole file) this tick.
        /// Scoped to this agent's state so it is dropped with the agent rather than growing
        /// unbounded across a long-lived daemon's lifetime-total sessions.
        public string? NativeStatPath;
        public long NativeStatLength;
        public DateTime NativeStatLastWriteUtc;
        public HarnessTitlePost? NativeStatResult;
        /// Bumped by every applied rename; a tick that started on an older revision discards
        /// its apply and its push, since what it read predates the rename.
        public int Revision;
        /// A rename the daemon applied. Authority until the server shows a different title, even
        /// when it matches the prompt or one of our push attempts.
        public string? Renamed;
        /// Whether a server read has returned <see cref="Renamed"/>. The server's title is a
        /// projected read model, so until it shows the rename any other title is a lagging read.
        public bool RenameSeen;
        public DateTimeOffset RenamedAt;
        /// The push attempts made before <see cref="Renamed"/>: a late echo of one is a stale read,
        /// never a newer title, so it cannot retire the rename.
        public HashSet<string> PreRenameAttempts = new(StringComparer.Ordinal);
        /// The tick epoch the state was created in; a removal pass spares a state created after
        /// its snapshot, whose agent the snapshot could not have seen.
        public long Born;
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

        public HashSet<string> Snapshot() => new(_set, StringComparer.Ordinal);

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
    /// Guards <see cref="_states"/> and every state field <see cref="ApplyRename"/> touches.
    /// Never held across an await.
    readonly Lock _gate = new();
    readonly Dictionary<string, AgentTitleState> _states = [];
    long _epoch;

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

    /// <summary>Records a rename the server accepted and shows it at once. Safe to call while
    /// <see cref="TickAsync"/> is in flight.</summary>
    public void ApplyRename(string agentId, string title) {
        if (Normalize(title) is not { } renamed) return;

        lock (_gate) {
            var state = StateFor(agentId);
            state.Renamed              = renamed;
            state.RenameSeen           = false;
            state.RenamedAt            = _time.GetUtcNow();
            state.PreRenameAttempts    = state.PushAttempts.Snapshot();
            state.ServerTitle          = renamed;
            state.Applied              = renamed;
            state.StartTitleSuperseded = true;
            state.Revision++;
            _apply(agentId, renamed);
        }
    }

    public async Task TickAsync(CancellationToken ct) {
        long epoch;
        lock (_gate) epoch = ++_epoch;

        var agents = _agents();

        var live = new HashSet<string>(agents.Select(a => a.Id), StringComparer.Ordinal);
        lock (_gate) {
            foreach (var gone in _states.Where(kv => !live.Contains(kv.Key) && kv.Value.Born < epoch).Select(kv => kv.Key).ToList())
                _states.Remove(gone);
        }

        foreach (var agent in agents) {
            ct.ThrowIfCancellationRequested();

            AgentTitleState state;
            lock (_gate) state = StateFor(agent.Id);

            try {
                await ResolveOneAsync(agent, state, ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Title resolution failed for agent {AgentId} — keeping current title", agent.Id);
            }
        }
    }

    AgentTitleState StateFor(string agentId) {
        if (!_states.TryGetValue(agentId, out var state)) _states[agentId] = state = new AgentTitleState { Born = _epoch };
        return state;
    }

    async Task ResolveOneAsync(TitleAgentView agent, AgentTitleState state, CancellationToken ct) {
        int revision;
        lock (_gate) revision = state.Revision;

        var chosen = agent.StartTitle is { Derived: false } start ? Normalize(start.Text) : null;

        try {
            if (ExtractNative(agent, state) is { } extracted) state.Native = extracted;
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Native title extraction failed for agent {AgentId}", agent.Id);
        }
        var native = state.Native;

        var (serverReadOk, serverReal, current) = await ReadServerAsync(agent, state, revision, ct);
        if (!current) return;

        bool renamed;
        lock (_gate) renamed = state.Renamed is not null;

        // An unreadable server is not a silent one: generation must not spend an LLM call on a
        // session whose watcher-made title merely couldn't be fetched.
        if (chosen is null && serverReadOk && serverReal is null && native is null && !renamed && !state.GenerationAttempted
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
            if (state.Generated is not null) {
                (serverReadOk, serverReal, current) = await ReadServerAsync(agent, state, revision, ct);
                if (!current) return;
            }
        }

        // Only a rename outranks a chosen start title; the server refuses an auto native title over it.
        var local = chosen is not null && native?.Kind != HarnessTitleKind.Rename ? chosen : Normalize(native?.Title) ?? chosen;

        HarnessTitlePost? nativePush = null;
        string? nativeSid = null, generatedPush = null, generatedSid = null;
        lock (_gate) {
            if (state.Revision != revision) return;

            // On a failed read the last successfully-read authority stands in, so an outage tick
            // cannot demote the applied title down the ladder.
            Apply(agent, state, (serverReadOk ? serverReal : state.ServerTitle) ?? state.Renamed ?? local ?? state.Generated);

            // The harness's own title is pushed whenever it changes, independent of the server's
            // current title — it is authoritative for what the harness itself calls the session.
            if (native is not null && !native.Equals(state.PushedNative) && agent.SessionId is { } sid) {
                // A server read comes back display-capped, so the capped form is what an echo of this push looks like.
                var attempted = Normalize(native.Title)!;
                state.PushAttempts.Add(attempted);
                state.PreRenameAttempts.Remove(attempted);
                nativePush = native;
                nativeSid  = sid;
            }

            // A locally generated title only converges to the server while it verifiably has no
            // real title, no native title and no standing rename — generation is a fallback, not an authority.
            if (serverReadOk && serverReal is null && native is null && state.Renamed is null && state.Generated is { } generated
             && generated != state.PushedGenerated && agent.SessionId is { } genSid) {
                state.PushAttempts.Add(generated);
                generatedPush = generated;
                generatedSid  = genSid;
            }
        }

        if (nativePush is not null) {
            var outcome = HarnessTitleOutcome.Failed;
            try {
                outcome = await _server.PushTitleAsync(nativeSid!, nativePush, ct);
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Native title push failed for session {SessionId}", nativeSid);
            }

            // A refusal (blank title, unsafe id, not the owner) is a verdict on this exact
            // value, not a transient hiccup — retrying it every tick forever would spend a
            // request for nothing. Failed/SessionNotFound stay retryable.
            if (outcome is HarnessTitleOutcome.Posted or HarnessTitleOutcome.PostedToLegacyRoute or HarnessTitleOutcome.Refused)
                lock (_gate) state.PushedNative = nativePush;
        }

        if (generatedPush is not null) {
            var posted = false;
            try {
                posted = await _postGenerated(generatedSid!, generatedPush, ct);
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Generated title push failed for session {SessionId}", generatedSid);
            }

            if (posted) lock (_gate) state.PushedGenerated = generatedPush;
        }
    }

    void Apply(TitleAgentView agent, AgentTitleState state, string? best) {
        if (best is null || best == state.Applied) return;

        _apply(agent.Id, best);
        state.Applied = best;
        if (best != Normalize(agent.StartTitle?.Text)) state.StartTitleSuperseded = true;
    }

    /// <summary>One server read plus the bookkeeping it settles: what counts as independent
    /// authority, and the retained-across-outages <see cref="AgentTitleState.ServerTitle"/>. A
    /// confirmed push is not re-armed by a read that no longer shows it: the server has no path
    /// that deletes a title, so a missing one is projection lag, and a native value re-sent over a
    /// later Regenerate would fight it. <c>Current</c> is false when a rename landed during the
    /// read, which makes the read stale.</summary>
    async Task<(bool Ok, string? Real, bool Current)> ReadServerAsync(
            TitleAgentView agent, AgentTitleState state, int revision, CancellationToken ct) {
        if (agent.SessionId is not { } sessionId) return (true, null, true); // an unrecorded agent has no server to ask

        string? serverTitle;
        try {
            serverTitle = Normalize(await _server.GetTitleAsync(sessionId, ct));
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Server title read failed for session {SessionId}", sessionId);

            lock (_gate) return (false, null, state.Revision == revision);
        }

        lock (_gate) {
            if (state.Revision != revision) return (true, null, false);

            // Once the server has shown the rename, or the lag hold has run out, a newer title
            // replaces it — a later native push included. A late echo of a push made before the
            // rename, or of the prompt, is a stale read and never does.
            if (state.Renamed is { } held && serverTitle is not null) {
                if (serverTitle == held) {
                    state.RenameSeen = true;
                } else if ((state.RenameSeen || _time.GetUtcNow() - state.RenamedAt >= RenameLagHold)
                        && !state.PreRenameAttempts.Contains(serverTitle) && !IsPromptEcho(serverTitle, agent.Prompt)) {
                    state.Renamed    = null;
                    state.RenameSeen = false;
                    state.PreRenameAttempts.Clear();
                }
            }

            // A title the loop itself pushed (or may have pushed — an unacknowledged attempt
            // can still have committed) coming back is not an independent server title:
            // treating it as one would freeze the ladder on our own echo and a later native
            // revision could never advance past it. The applied rename is exempt: it is real
            // whatever it happens to match.
            string? serverReal = null;
            if (state.Renamed is { } rename && (serverTitle == rename || !state.RenameSeen)) {
                serverReal = rename;
            } else if (serverTitle is not null && !state.PushAttempts.Contains(serverTitle)
                    && !IsPromptEcho(serverTitle, agent.Prompt)
                    && (state.StartTitleSuperseded || serverTitle != Normalize(agent.StartTitle?.Text))) {
                serverReal = serverTitle;
            }

            state.ServerTitle = serverReal;

            return (true, serverReal, true);
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
