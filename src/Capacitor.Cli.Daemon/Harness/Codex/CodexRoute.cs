namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <param name="Refusal">Set when the launch must not start at all.</param>
public readonly record struct CodexRoute(bool AppServer, string? Refusal);
