namespace Capacitor.App.ViewModels;

/// One row of a status tooltip. Caption names the value (Status, Session, a checkout path).
public sealed record AgentStatusFact(string Text, string? Caption) {
    public bool HasCaption => Caption is { Length: > 0 };
}
