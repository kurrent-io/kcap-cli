using Capacitor.Cli.Core.Pages;
using Capacitor.Cli.Core.Plans;

namespace Capacitor.App.Services;

/// The Artefacts tab's server calls over one authenticated client, so a sign-in lapse retires them all.
public sealed class ArtefactReadChannel(HttpClient http, string serverUrl) {
    public PlanArtifactsClient Plans { get; } = new(http, serverUrl);
    public SessionPagesClient Pages { get; } = new(http, serverUrl);
    public PageContentClient PageContent { get; } = new(http, serverUrl);
}
