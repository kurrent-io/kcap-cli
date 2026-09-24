using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.LocalIpc;

/// JSON payload for the DaemonStatus frame. snake_case on the wire; shared verbatim by the
/// daemon, the CLI, and the desktop app. Every field is ALWAYS emitted — absent values are
/// JSON null, never omitted (one wire shape, exact-JSON testable), so this context must never
/// gain a DefaultIgnoreCondition. Deserialization ignores unmapped members (STJ default) —
/// additive fields must never break an older client.
public sealed record DaemonStatusDto(DaemonInfoDto Daemon, List<AgentStatusDto> Agents,
    // Launches whose runtime is still starting, before an agent exists to list under Agents. Null
    // from an older daemon, which a client reads as unknown, never as "nothing starting".
    List<PendingLaunchDto>? Pending = null);

/// <summary>
/// <see cref="Connection"/> ∈ connected|connecting|reconnecting|disconnected (lowercase).
/// <see cref="ActiveAgents"/> is derived from the SAME materialized agents array it ships
/// with (Status is "Starting" or "Running"), so count and array can never disagree within
/// one payload. <see cref="Pid"/>/<see cref="InstanceId"/> are additive trailing members
/// identifying the reporting daemon process for client-side correlation — always
/// populated by a current daemon (see the "every field ALWAYS emitted" rule above); null only
/// if an old snapshot from before this field existed were ever replayed.
/// </summary>
public sealed record DaemonInfoDto(
    string Name, string Version, string ServerUrl, string Connection, int MaxAgents, int ActiveAgents,
    int? Pid = null, string? InstanceId = null,
    // Vendor tokens this daemon can host, from the runtime factories' own availability probe —
    // the same set advertised to the server on DaemonConnect. Trailing/additive: null from a
    // daemon that predates it, which a client must read as UNKNOWN, never as "hosts nothing".
    string[]? SupportedVendors = null);

/// <summary>
/// <see cref="Status"/> is the daemon's internal status string VERBATIM (PascalCase, open
/// vocabulary — clients treat unknown values as opaque display text). <see cref="Kind"/>
/// uses the KindText wire spellings (agent/review/review-flow, unknown enum names pass
/// through) — one vocabulary across AgentList and this payload. <see cref="Requester"/> is
/// the opaque server-stamped requester id, null when unknown (old servers, local spawns).
/// <see cref="RequesterDisplay"/> is the server-stamped human-readable name for it, null on
/// an old server or a local spawn; choosing which of the two to render is presentation.
/// </summary>
public sealed record AgentStatusDto(
    string Id, string Kind, string Vendor, string? RepoPath, string Status,
    string? FlowRunId, string? FlowRole, string? Requester, DateTime CreatedAt, string? Model,
    string? RequesterDisplay,
    // Whether the agent's runtime emits a PTY the app can attach to
    // (IHostedAgentRuntime.EmitsTerminalOutput). Trailing + nullable so every
    // existing positional construction stays valid; null = older daemon,
    // unknown — the app falls back to its vendor heuristic. Always emitted:
    // false is a real value, not an absence.
    bool? HasTerminal = null,
    // Display title for session rows: seeded from the launch prompt's first non-blank line and
    // upgraded by the daemon's title resolution (native transcript title, the server's title, or
    // a local generation) as one lands — so its value can change across pulses. At most 120
    // chars (the set-title cap). Trailing + nullable: null = older daemon or no goal text.
    string? Title = null,
    // Where the daemon found the agent's own transcript (Claude's project .jsonl, Codex's
    // rollout), link-resolved. Trailing + nullable: null is "older daemon", "not found yet",
    // "no transcript for this runtime", or "found nothing before the agent exited" alike —
    // a client waits, it never distinguishes them.
    string? TranscriptPath = null,
    // The checkout root the agent runs in. RepoPath is the repository it belongs to, for every
    // agent: a primary runs in an owned worktree under it, a borrowed reviewer in the worktree it
    // borrowed. Trailing + nullable: null = older daemon, which a client renders as it did before.
    string? WorktreePath = null,
    // "owned" or "borrowed"; derived from BorrowedFrom so the two never disagree. Null = older daemon.
    string? WorkLocation = null,
    // The checkout root a borrowed reviewer reviews — for a runtime that needs its own snapshot
    // this differs from WorktreePath, and it is the node the reviewer belongs under. Null unless
    // borrowed.
    string? BorrowedFrom = null,
    // The session id the daemon reports to the server: discovered from the transcript for a PTY
    // vendor, taken from the handshake for an ACP one. Null is "older daemon", "not resolved yet"
    // or "no session for this runtime" alike — a client waits, it never distinguishes them.
    string? SessionId = null,
    // The branch of the checkout the agent runs in; null from an older daemon or a launch that
    // recorded none (a borrowed in-place checkout).
    string? Branch = null,
    // The daemon's verdict that the agent finished a turn and waits on the user: true only while
    // Running, from a runtime-attested turn end or a PTY vendor's relayed Stop hook. Trailing +
    // nullable: null is an older daemon, which a client must read as unknown, never as working.
    bool? AwaitingInput = null,
    // Which reader a client uses for TranscriptPath: TranscriptFormats.Vendor for a PTY runtime's own
    // file, TranscriptFormats.Envelopes for the daemon-written envelope journal. Always emitted by a
    // current daemon, so null means an older daemon and nothing else.
    string? TranscriptFormat = null,
    // How many subagents the daemon believes are running: null until the agent's first subagent
    // report (an older daemon, a vendor whose hooks report none, or a session that has spawned
    // none yet), then a number — the clock's count while Running, zero in any other status.
    int? LiveSubagents = null,
    // A vendor usage limit matched for this live agent. Null from an older daemon, and whenever
    // nothing is matched. A blocked notice that carries options is a question the user answers;
    // it is not AwaitingInput, which means the turn ended and a prompt will be read.
    UsageLimitNoticeDto? UsageLimit = null);

/// Wire tokens for <see cref="AgentStatusDto.WorkLocation"/>, compared literally by every
/// client, so they never change.
public static class WorkLocationText {
    public const string Owned    = "owned";
    public const string Borrowed = "borrowed";
}

/// Wire tokens for <see cref="AgentStatusDto.TranscriptFormat"/>, compared literally by every client.
public static class TranscriptFormats {
    public const string Vendor    = "vendor";
    public const string Envelopes = "envelopes";
}

/// Wire tokens for <see cref="UsageLimitNoticeDto.Kind"/>.
public static class UsageLimitKinds {
    /// The vendor is waiting on a choice before the turn can continue. Chat send is refused.
    public const string Blocked  = "blocked";
    /// The vendor is retrying on its own. The composer stays usable.
    public const string Retrying = "retrying";
    /// The turn already stopped. The composer stays usable.
    public const string Failed   = "failed";
}

/// One numbered choice on a blocking usage-limit menu. <see cref="Index"/> is the digit the
/// vendor's own menu shows, which is what an answer sends.
public sealed record UsageLimitOptionDto(int Index, string Label);

/// Vendor-neutral usage-limit notice. Options are empty when the vendor is not asking a question.
/// Equality is by value: a status pulse rebuilds this object, and a surface must not treat an
/// unchanged menu as a new one.
public sealed class UsageLimitNoticeDto : IEquatable<UsageLimitNoticeDto> {
    public UsageLimitNoticeDto(string kind, string summary, string prompt, List<UsageLimitOptionDto>? options) {
        Kind    = kind;
        Summary = summary;
        Prompt  = prompt;
        Options = options ?? [];
    }

    public string Kind { get; }
    public string Summary { get; }
    public string Prompt { get; }
    public List<UsageLimitOptionDto> Options { get; }

    public static bool IsQuestion(UsageLimitNoticeDto? notice) =>
        notice is { Kind: UsageLimitKinds.Blocked, Options.Count: > 0 };

    public bool Equals(UsageLimitNoticeDto? other) {
        if (other is null || Kind != other.Kind || Summary != other.Summary || Prompt != other.Prompt
            || Options.Count != other.Options.Count) return false;
        for (var i = 0; i < Options.Count; i++)
            if (Options[i] != other.Options[i]) return false;
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as UsageLimitNoticeDto);

    public override int GetHashCode() {
        var hash = HashCode.Combine(Kind, Summary, Prompt);
        foreach (var option in Options) hash = HashCode.Combine(hash, option);
        return hash;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(DaemonStatusDto))]
public partial class StatusIpcJsonContext : JsonSerializerContext;
