namespace Capacitor.App.ViewModels;

/// One session status. Earlier values outrank later ones: a row resolves to exactly one of these.
public enum AgentStatusKind {
    Failed,
    Answer,
    NeedsYou,
    Starting,
    Working,
    Idle,
    Done,
    Other,
}

/// Glyph, word, and hover text for one resolved session status. The accessible name is the
/// tooltip's first line, so a screen reader and a hover agree.
public sealed record AgentStatusPresentation(
        AgentStatusKind Kind, string Label, string Tip, bool Pulses, IReadOnlyList<AgentStatusFact> Facts) {
    public static AgentStatusPresentation None { get; } =
        new(AgentStatusKind.Other, "", "", false, []);

    public string AccessibleName => Tip.Length == 0 ? "" : Tip.Split('\n')[0];

    public bool HasLabel => Label.Length > 0;
    public bool IsInFlight => Kind is AgentStatusKind.Working or AgentStatusKind.Starting;
    public bool IsWarning => Kind is AgentStatusKind.Idle or AgentStatusKind.NeedsYou or AgentStatusKind.Answer;
    public bool IsDanger => Kind == AgentStatusKind.Failed;
    public bool ShowsPulse => Kind is AgentStatusKind.Working or AgentStatusKind.Starting;
    public bool ShowsClock => Kind == AgentStatusKind.Idle;
    public bool ShowsAsk => Kind == AgentStatusKind.Answer;
    public bool ShowsBang => Kind == AgentStatusKind.NeedsYou;
    public bool ShowsCross => Kind == AgentStatusKind.Failed;
    public bool ShowsDash => Kind is AgentStatusKind.Done or AgentStatusKind.Other;
}
