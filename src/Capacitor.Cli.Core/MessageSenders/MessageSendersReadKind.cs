namespace Capacitor.Cli.Core.MessageSenders;

/// <see cref="Unavailable"/> is the server's answer that this viewer cannot read the session;
/// <see cref="Unreachable"/> is no usable answer at all, worth asking again.
public enum MessageSendersReadKind { Ready, Unavailable, SignedOut, Unreachable }
