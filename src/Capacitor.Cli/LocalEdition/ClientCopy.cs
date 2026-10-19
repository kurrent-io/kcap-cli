namespace Capacitor.Cli.LocalEdition;

/// <param name="Version">Null when the copy did not report one.</param>
sealed record ClientCopy(ClientCopyKind Kind, string Path, string? Version);
