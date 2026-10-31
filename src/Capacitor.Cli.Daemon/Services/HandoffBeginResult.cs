using Capacitor.Cli.Core.Handoff;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>A begin the server answered, one it cannot answer because it lacks the method, or one that did not
/// reach an answer. Only <see cref="Unsupported"/> is permanent.</summary>
internal abstract record HandoffBeginResult {
    HandoffBeginResult() { }

    public sealed record Answered(HandoffTakeoverAdmission Admission) : HandoffBeginResult;

    public sealed record Unsupported : HandoffBeginResult;

    public sealed record Unreachable : HandoffBeginResult;
}
