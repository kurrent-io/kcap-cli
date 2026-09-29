namespace Capacitor.Cli.Core.Auth;

/// What a guarded token write did. An unreadable config is its own outcome: the fresh default
/// <c>TryLoadPure</c> hands back would pass most guards for the wrong reason.
public enum GuardedWriteOutcome { Written, GuardRefused, ConfigUnreadable }
