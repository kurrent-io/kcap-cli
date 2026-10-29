namespace Capacitor.Cli.Core.Plans;

/// One read of a session's plans, totalized. The server lists them most recently touched first.
public sealed record SessionPlansRead(SessionPlansReadKind Kind, IReadOnlyList<SessionPlanDto> Plans) {
    public static SessionPlansRead Of(SessionPlansReadKind kind) => new(kind, []);
}
