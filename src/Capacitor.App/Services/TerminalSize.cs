namespace Capacitor.App.Services;

/// The source PTY's size, as TerminalDimensions reports it to a viewer.
public sealed record TerminalSize(string AgentId, int Cols, int Rows);
