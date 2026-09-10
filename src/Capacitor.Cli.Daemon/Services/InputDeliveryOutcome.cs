namespace Capacitor.Cli.Daemon.Services;

/// <summary>How <see cref="AgentOrchestrator.DeliverInputAsync"/> ended. <c>QuitRequested</c> is not
/// a failure: the text was a quit command a TUI-less runtime cannot interpret, and stopping the
/// agent belongs to the caller, which owns the lane the stop must ride.</summary>
internal enum InputDeliveryKind { Delivered, QuitRequested, Dropped }

/// <summary>One delivery attempt's result. <see cref="Reason"/> carries an
/// <see cref="AgentOrchestrator.SendInputDropReason"/> token when the input was dropped — it is what
/// a caller reports to whoever typed the message; <see cref="Error"/> is the runtime's own message
/// for the fault behind it, for a log rather than a wire token.</summary>
internal readonly record struct InputDeliveryOutcome(InputDeliveryKind Kind, string? Reason = null, string? Error = null) {
    public static readonly InputDeliveryOutcome Delivered     = new(InputDeliveryKind.Delivered);
    public static readonly InputDeliveryOutcome QuitRequested = new(InputDeliveryKind.QuitRequested);

    public static InputDeliveryOutcome Drop(string reason, string? error = null) =>
        new(InputDeliveryKind.Dropped, reason, error);
}
