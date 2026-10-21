namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>A lifecycle command cannot proceed. The message is for the user and names what to do.</summary>
public sealed class LocalLifecycleException(string message, Exception? inner = null) : Exception(message, inner);
