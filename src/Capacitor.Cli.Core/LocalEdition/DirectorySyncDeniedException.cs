namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>A directory could not be opened for syncing because the caller may not read it.</summary>
sealed class DirectorySyncDeniedException(string message) : IOException(message);
