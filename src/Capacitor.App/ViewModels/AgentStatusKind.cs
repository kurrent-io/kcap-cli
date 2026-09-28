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
