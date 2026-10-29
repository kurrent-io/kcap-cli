namespace Capacitor.App.ViewModels;

/// Glyph, word, and hover text for one resolved session status. The accessible name is the
/// tooltip's first line, so a screen reader and a hover agree.
public sealed record AgentStatusPresentation(
        AgentStatusKind Kind, string Label, string Tip, bool Pulses, IReadOnlyList<AgentStatusFact> Facts) {
    public static AgentStatusPresentation None { get; } =
        new(AgentStatusKind.Other, "", "", false, []);

    /// The session is over, so a daemon word it last reported no longer means it is live.
    public bool Ended { get; init; }

    public string AccessibleName => Tip.Length == 0 ? "" : Tip.Split('\n')[0];

    public bool HasLabel => Label.Length > 0;
    /// Working, and the daemon has proof a turn is running rather than an idle prompt that never
    /// reported a turn end. Only this makes a mid-turn keystroke like Escape safe to send.
    public bool InForegroundTurn { get; init; }

    /// The daemon says Running but the turn is unknown (a remote row): live, yet not claimed as Working.
    public bool IsRunningUnknownTurn => !Ended && Kind == AgentStatusKind.Other && Label == "Running";
    public bool IsInFlight => Kind is AgentStatusKind.Working or AgentStatusKind.Starting or AgentStatusKind.Resuming || IsRunningUnknownTurn;
    public bool IsWarning => Kind is AgentStatusKind.Idle or AgentStatusKind.NeedsYou or AgentStatusKind.Answer or AgentStatusKind.Interrupted;
    public bool IsDanger => Kind == AgentStatusKind.Failed;
    public bool ShowsRing => IsInFlight;
    public bool ShowsClock => Kind == AgentStatusKind.Idle;
    public bool ShowsAsk => Kind == AgentStatusKind.Answer;
    public bool ShowsBang => Kind == AgentStatusKind.NeedsYou;
    public bool ShowsPause => Kind == AgentStatusKind.Interrupted;
    public bool ShowsCross => Kind == AgentStatusKind.Failed;
    public bool ShowsDash => Kind is AgentStatusKind.Done or AgentStatusKind.Other && !IsRunningUnknownTurn;
}
