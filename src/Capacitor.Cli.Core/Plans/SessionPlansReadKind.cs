namespace Capacitor.Cli.Core.Plans;

/// <see cref="Unavailable"/> is the server's own answer that this viewer has nothing to read for the
/// session; <see cref="Unreachable"/> is no usable answer at all, so a reader may keep what it had.
public enum SessionPlansReadKind { Ready, Unavailable, SignedOut, Unreachable }
