namespace Capacitor.Cli.Core.LocalIpc;

public enum WorkLocation : byte { BorrowedCwd = 0, OwnedWorktree = 1 }

/// A decoded frame. Exactly one of the payload properties is meaningful per Type;
/// the codec owns which. Bytes are raw (Stdin/Stdout); strings are UTF-8.
public sealed record LocalFrame(FrameType Type) {
    public byte[]       Bytes    { get; init; } = [];
    public string       Text     { get; init; } = "";
    public ushort       Cols     { get; init; }
    public ushort       Rows     { get; init; }
    public WorkLocation Work     { get; init; }
    public int          ExitCode { get; init; }
    public DateTimeOffset StartedAt { get; init; }

    public static LocalFrame Stdin(byte[] b)            => new(FrameType.Stdin)  { Bytes = b };
    public static LocalFrame Stdout(byte[] b)           => new(FrameType.Stdout) { Bytes = b };
    public static LocalFrame Resize(ushort c, ushort r) => new(FrameType.Resize) { Cols = c, Rows = r };
    public static LocalFrame Detach()                   => new(FrameType.Detach);
    public static LocalFrame Exited(int code)           => new(FrameType.Exited) { ExitCode = code };
    public static LocalFrame Error(string m)            => new(FrameType.Error)  { Text = m };
    public static LocalFrame RuntimeStarted(DateTimeOffset at) => new(FrameType.RuntimeStarted) { StartedAt = at };
    public static LocalFrame AccountsSubscribe()        => new(FrameType.AccountsSubscribe);

    /// <summary>The refusal for an id the daemon is not running; an attach to a resuming agent retries on
    /// this text and on no other.</summary>
    public const string NoSuchAgentPrefix = "no such agent";

    public static LocalFrame NoSuchAgent(string agentId)  => Error($"{NoSuchAgentPrefix} {agentId}");
    public static LocalFrame Restart(string mode)       => new(FrameType.Restart)    { Text = mode };
    public static LocalFrame RestartAck(string status)  => new(FrameType.RestartAck) { Text = status };
    public static LocalFrame Shutdown(ShutdownRequestDto dto) => new(FrameType.Shutdown)       { Text = ShutdownIpc.Serialize(dto) };
    public static LocalFrame ShutdownAck(string status) => new(FrameType.ShutdownAck)          { Text = status };
    public static LocalFrame AgentResume(string agentId) => new(FrameType.AgentResume)         { Text = agentId };
    public static LocalFrame AgentResumeAck(string status) => new(FrameType.AgentResumeAck)    { Text = status };
    public static LocalFrame AgentSession(string agentId) => new(FrameType.AgentSession)       { Text = agentId };
    public static LocalFrame AgentSessionReply(string? sessionId) => new(FrameType.AgentSessionReply) { Text = sessionId ?? "" };
    public static LocalFrame Stop(string agentId)      => new(FrameType.Stop)       { Text = agentId };
    public static LocalFrame StopAck(string payload)    => new(FrameType.StopAck)    { Text = payload };
    public static LocalFrame StopV2(bool force, string agentId) => FrameCodec.StopV2(force, agentId);

    public const string ListTitleColumn = "title";

    /// The title column is opt-in: a client that predates it accepts only 3 or 6 columns and
    /// refuses any other width, and a daemon that predates it ignores the request payload.
    public static LocalFrame ListWithTitles() => new(FrameType.List) { Text = ListTitleColumn };

    public const string ListStateColumn = "title+state";

    /// Adds the state column and the ledger rows of agents with no live process. A daemon that
    /// predates it answers as it would a bare List, with six columns.
    public static LocalFrame ListWithState() => new(FrameType.List) { Text = ListStateColumn };

    /// Constructs any of the consent control frames, whose payload is always UTF-8 JSON
    /// (snake_case via ConsentIpcJsonContext) carried in Text — see ConsentIpc.cs.
    public static LocalFrame ConsentJson(FrameType type, string json) => new(type) { Text = json };

    /// Constructs a Hello or HelloReply frame, whose payload is UTF-8 JSON
    /// (snake_case via HelloIpcJsonContext) carried in Text — see HelloIpc.cs.
    public static LocalFrame HelloJson(FrameType type, string json) => new(type) { Text = json };

    /// Constructs a DaemonStatus frame, whose payload is UTF-8 JSON
    /// (snake_case via StatusIpcJsonContext) carried in Text — see StatusIpc.cs.
    public static LocalFrame StatusJson(FrameType type, string json) => new(type) { Text = json };

    /// Constructs any of the permission control frames, whose payload is UTF-8 JSON
    /// (snake_case via PermissionIpcJsonContext) carried in Text — see PermissionIpc.cs.
    public static LocalFrame PermissionJson(FrameType type, string json) => new(type) { Text = json };

    /// Constructs a SendText or SendTextAck frame, whose payload is UTF-8 JSON (snake_case via
    /// InputIpcJsonContext) carried in Text — see InputIpc.cs.
    public static LocalFrame InputJson(FrameType type, string json) => new(type) { Text = json };

    /// Constructs a DaemonSettingsPut or DaemonSettingsAck frame, whose payload is UTF-8 JSON
    /// (snake_case via SettingsIpcJsonContext) carried in Text — see SettingsIpc.cs.
    public static LocalFrame SettingsJson(FrameType type, string json) => new(type) { Text = json };

    public static LocalFrame FenceJson(FrameType type, string json) => new(type) { Text = json };
    /// Constructs a SessionRename or SessionRenameAck frame, whose payload is UTF-8 JSON
    /// (snake_case via RenameIpcJsonContext) carried in Text — see RenameIpc.cs.
    public static LocalFrame RenameJson(FrameType type, string json) => new(type) { Text = json };

    /// Constructs a HandoffProbe or HandoffProbeReply frame, whose payload is UTF-8 JSON
    /// (snake_case via HandoffIpcJsonContext) carried in Text — see HandoffIpc.cs.
    public static LocalFrame HandoffJson(FrameType type, string json) => new(type) { Text = json };
}
