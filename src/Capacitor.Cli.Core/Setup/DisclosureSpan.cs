namespace Capacitor.Cli.Core.Setup;

/// A run of disclosure text; a code run is a path or command the UI sets in a monospace font.
public readonly record struct DisclosureSpan(string Text, bool Code = false);
