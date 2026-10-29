namespace Capacitor.Cli.Services;

sealed record ServicePipeCapabilities(string Version, int Protocol, IReadOnlyList<string> Verbs);
