namespace Capacitor.App.Services;

/// One TerminalOutput push: the agent's bytes, base64 as the wire carries them.
public sealed record TerminalOutputFrame(string AgentId, string Base64);
