namespace Capacitor.Cli.Daemon.Services;

/// <summary>The server's verdict on a rename. <see cref="Reason"/> is a
/// <c>SessionRenameReasons</c> value when the rename did not apply, and null when it did.</summary>
internal sealed record TitleRenameOutcome(bool Applied, string? Title, string? Reason);
