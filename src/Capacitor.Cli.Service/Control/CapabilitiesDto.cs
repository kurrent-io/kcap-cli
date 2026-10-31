namespace Capacitor.Cli.Service.Control;

public sealed record CapabilitiesDto(string Version, int Protocol, IReadOnlyList<string> Verbs);
