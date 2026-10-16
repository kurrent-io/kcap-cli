namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>The installation the machine records to. <paramref name="ServerUrl"/> is its canonical
/// URL, <c>http://localhost:&lt;port&gt;</c>, and so its server identity.</summary>
public sealed record LocalInstallation(string Id, int Port, string ServerUrl, string Version);
