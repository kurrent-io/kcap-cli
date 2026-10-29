namespace Capacitor.App.Services;

/// Ordered: a session claimed on more than one lane resolves to the lowest. Server is a session
/// the server lists with no daemon row behind it: live in a terminal, or ended.
public enum AgentOrigin { Local, Remote, Pending, Server }
