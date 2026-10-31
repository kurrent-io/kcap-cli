namespace Capacitor.Cli.Commands;

/// <summary>A working directory whose sessions discovery could not attribute to a repository.
/// <paramref name="Folder"/> is null for sessions whose folder is not known.</summary>
public sealed record ImportUnmatchedFolder(string? Folder, int Sessions);
