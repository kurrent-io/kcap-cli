namespace Capacitor.Cli.Core.LocalEdition;

/// <param name="Version">Null when the copy did not report one.</param>
public sealed record ClientCopy(ClientCopyKind Kind, string Path, string? Version);
