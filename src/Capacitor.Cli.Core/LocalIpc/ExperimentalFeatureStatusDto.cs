namespace Capacitor.Cli.Core.LocalIpc;

/// <summary>One experimental feature as the daemon resolved it. <see cref="ServerAcknowledged"/> is whether
/// a registration has carried <see cref="Generation"/> to the server.</summary>
public sealed record ExperimentalFeatureStatusDto(
    string Id, bool ResolvedSaved, bool Active, long Generation, bool ServerAcknowledged, string? Reason);
