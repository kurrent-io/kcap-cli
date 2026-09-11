using System.Globalization;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using AcpInteractionOption = Capacitor.Remote.Models.AcpInteractionOption;

namespace Capacitor.App.Services;

/// One pending prompt, keyed by lane so the two lanes' independently allocated ids never
/// collide. RequestId is the delivering lane's own id and the only id ever sent back on it.
/// ServerRequestId on a local item is the daemon's mapping, written in place when it arrives so
/// the card built over this instance survives.
public sealed class PendingPermissionRequest {
    string? _serverRequestId;
    string _agentId;

    internal PendingPermissionRequest(PermissionPendingDto dto) {
        Lane = PermissionLane.Local;
        Key = KeyFor(PermissionLane.Local, dto.RequestId);
        RequestId = dto.RequestId;
        SessionId = dto.SessionId;
        _agentId = dto.AgentId;
        Vendor = dto.Vendor;
        ToolName = dto.ToolName;
        ToolInputJson = dto.ToolInput?.GetRawText();
        ToolInputOmitted = dto.ToolInputOmitted;
        ToolUseId = dto.ToolUseId;
        RequestedAt = ParseTime(dto.RequestedAt);
        Questions = dto.Vendor == "claude" && dto.ToolName == ClaudeElicitation.ToolName && !dto.ToolInputOmitted
            ? ClaudeElicitation.TryParse(ToolInputJson) : null;
        _serverRequestId = dto.ServerRequestId;
    }

    PendingPermissionRequest(string requestId, string sessionId, string vendor, string toolName, string? toolInputJson,
            DateTimeOffset requestedAt, ElicitationQuestions? questions, AcpElicitation? acpQuestion, IReadOnlyList<AcpInteractionOption>? options) {
        Lane = PermissionLane.Server;
        Key = KeyFor(PermissionLane.Server, requestId);
        RequestId = requestId;
        SessionId = sessionId;
        _agentId = "";
        Vendor = vendor;
        ToolName = toolName;
        ToolInputJson = toolInputJson;
        RequestedAt = requestedAt;
        Questions = questions;
        AcpQuestion = acpQuestion;
        Options = options;
        _serverRequestId = requestId;
    }

    internal static PendingPermissionRequest FromServer(ServerPermissionRequest r, string vendor, DateTimeOffset at) {
        var inputJson = r.ToolInput?.GetRawText();
        var questions = r.Options is null && r.ToolName == ClaudeElicitation.ToolName ? ClaudeElicitation.TryParse(inputJson) : null;
        return new(r.RequestId, r.SessionId, vendor, r.ToolName, inputJson, at, questions, null, r.Options);
    }

    internal static PendingPermissionRequest FromServer(ServerElicitationRequest r, DateTimeOffset at) {
        var bounds = r.Options.FirstOrDefault(o => o is { MinSelections: not null, MaxSelections: not null });
        return new(r.RequestId, r.SessionId, "", ClaudeElicitation.ToolName, null, at, null,
            new AcpElicitation(r.Prompt, r.Options, r.IsMultiSelect, bounds?.MinSelections, bounds?.MaxSelections), null);
    }

    /// Null for a transcript-recorded question: nothing the response route can settle.
    internal static PendingPermissionRequest? FromReconciled(string sessionId, PendingInterrupt p) => p.Kind switch {
        PendingInterruptKind.ClaudePermission => FromReconciledClaude(sessionId, p),
        PendingInterruptKind.AcpPermission => new(p.RequestId, sessionId, "", p.ToolName, p.ToolInput?.GetRawText(), p.RequestedAt, null, null, p.Options),
        PendingInterruptKind.AcpQuestion => new(p.RequestId, sessionId, "", ClaudeElicitation.ToolName, null, p.RequestedAt, null,
            new AcpElicitation(p.Prompt, p.Options, p.IsMultiSelect, p.MinSelections, p.MaxSelections), null),
        _ => null,
    };

    /// A permission-kind interrupt naming the elicitation tool still carries the questions payload,
    /// and its answer has to be an updated_input: rendered as a generic Allow it would settle the
    /// request with no answer in it at all. Classified here the same way the live factory does.
    static PendingPermissionRequest FromReconciledClaude(string sessionId, PendingInterrupt p) {
        var inputJson = p.ToolInput?.GetRawText();
        return new(p.RequestId, sessionId, "claude", p.ToolName, inputJson, p.RequestedAt,
            p.ToolName == ClaudeElicitation.ToolName ? ClaudeElicitation.TryParse(inputJson) : null, null, null);
    }

    public static string KeyFor(PermissionLane lane, string requestId) => lane == PermissionLane.Local ? $"local:{requestId}" : $"server:{requestId}";

    public string Key { get; }
    public PermissionLane Lane { get; }
    public string RequestId { get; }
    public string SessionId { get; }
    public string AgentId { get => Volatile.Read(ref _agentId); internal set => Volatile.Write(ref _agentId, value); }
    public string Vendor { get; }
    public string ToolName { get; }
    public string? ToolInputJson { get; }
    public bool ToolInputOmitted { get; }
    public string? ToolUseId { get; }
    public DateTimeOffset RequestedAt { get; }
    public ElicitationQuestions? Questions { get; }
    public AcpElicitation? AcpQuestion { get; }
    public IReadOnlyList<AcpInteractionOption>? Options { get; }
    public string? ServerRequestId { get => Volatile.Read(ref _serverRequestId); internal set => Volatile.Write(ref _serverRequestId, value); }
    /// When this server item last landed, on PermissionService's own counter — read and written
    /// under that service's lock, and only there. It is what tells a reconciliation whether an
    /// item it does not hold is stale or newer than the snapshot it fetched.
    internal long LiveSequence { get; set; }
    public bool IsQuestion => Questions is not null || AcpQuestion is not null;

    static DateTimeOffset ParseTime(string s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : DateTimeOffset.MinValue;
}
