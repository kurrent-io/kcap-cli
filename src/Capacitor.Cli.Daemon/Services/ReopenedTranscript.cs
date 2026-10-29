namespace Capacitor.Cli.Daemon.Services;

/// <summary>Whether the server records a session a restart resume reopened.</summary>
internal enum ReopenedTranscript { None, Pending, Accepted, Refused }
