namespace Capacitor.App.Services;

/// The source PTY's size, not a viewer's viewport.
public sealed record TerminalSize(string AgentId, int Cols, int Rows);
