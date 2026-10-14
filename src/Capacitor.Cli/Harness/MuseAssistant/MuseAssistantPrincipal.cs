namespace Capacitor.Cli.Harness.MuseAssistant;

/// <summary>The tenant and the signed-in kcap user every file, grant and session id is bound to.</summary>
public sealed record MuseAssistantPrincipal(string ServerUrl, string UserId);
