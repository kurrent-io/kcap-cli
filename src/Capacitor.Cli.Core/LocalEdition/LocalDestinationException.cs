namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>A request refused before it left the process, because its origin is a purged local installation's or
/// cannot be told apart from one. A transport failure to its callers, so a recording is kept rather than
/// dropped.</summary>
public sealed class LocalDestinationException(string message) : HttpRequestException(message);
