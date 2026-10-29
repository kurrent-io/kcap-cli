namespace Capacitor.Cli.Core.Auth;

public sealed class CommitPreconditionFailedException(string message) : InvalidOperationException(message);
