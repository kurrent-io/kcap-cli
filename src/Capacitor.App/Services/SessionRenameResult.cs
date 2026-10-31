namespace Capacitor.App.Services;

/// A remote rename's outcome. Reason is one of <see cref="Capacitor.Cli.Core.LocalIpc.SessionRenameReasons"/>
/// when Applied is false; Title is the server's shown title when it reported one.
public sealed record SessionRenameResult(bool Applied, string? Title, string? Reason);
