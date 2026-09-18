namespace Capacitor.Cli.Commands;

/// <summary>DiscoverAsync's totalized result: <see cref="Result"/> is null only when
/// <see cref="Fault"/> is set.</summary>
internal sealed record SetupImportDiscovery(ImportCommand.ImportDiscoveryResult? Result, Exception? Fault);
