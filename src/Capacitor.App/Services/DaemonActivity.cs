namespace Capacitor.App.Services;

/// What the attached daemon last reported it was running.
public sealed record DaemonActivity(int ActiveAgents, bool Busy);
