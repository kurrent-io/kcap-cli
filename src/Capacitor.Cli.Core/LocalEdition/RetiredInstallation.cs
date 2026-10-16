namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>A purged installation. Kept so that anything still pointed at its URL is refused rather
/// than delivered to whatever takes the port next, and so a new installation never reuses the port.</summary>
public sealed record RetiredInstallation(string Id, int Port, string ServerUrl);
