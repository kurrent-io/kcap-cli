using Capacitor.Cli.Capture;

namespace Capacitor.Cli.Commands;

public sealed record CaptureLineLost(string SessionId, string? AgentId, int LineNumber, RedactionLossReason Reason)
    : ImportWarning(SessionId, AgentId) {
    public override string Message =>
        $"{Scope}line {LineNumber} could not be captured: {CaptureLossMarker.ReasonName(Reason).Replace('_', ' ')}; loss marker queued";
}
