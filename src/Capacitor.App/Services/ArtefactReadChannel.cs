using Capacitor.Cli.Core.Pages;
using Capacitor.Cli.Core.Plans;

namespace Capacitor.App.Services;

/// The Artefacts tab's two server reads over one authenticated client, so a sign-in lapse retires both.
public sealed class ArtefactReadChannel(HttpClient http, string serverUrl) {
    public PlanArtifactsClient Plans { get; } = new(http, serverUrl);
    public SessionPagesClient Pages { get; } = new(http, serverUrl);
}
