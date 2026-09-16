namespace Capacitor.Cli.Core.Http;

/// <summary>
/// Records the tenant's plan entitlements from each response's
/// <see cref="HttpClientExtensions.PlanHeader"/> into <see cref="PlanEntitlementStore"/>, so the
/// SessionStart nudges can skip a tool the plan refuses. Sits beside
/// <see cref="ServerVersionCaptureHandler"/> and for the same reason — OUTERMOST, so it observes the
/// final response after a recovery resend; best-effort, and never alters either.
///
/// <para>Capturing here rather than at the hook's own response is what makes ONE answer serve every
/// harness: eight of the nine discard the session-start response body, but they all send on a lane
/// this handler is registered against.</para>
/// </summary>
internal sealed class PlanEntitlementCaptureHandler(string serverUrl, ConfigRoot config, TimeProvider time) : DelegatingHandler {
    public PlanEntitlementCaptureHandler(CapacitorServer server, TimeProvider time) : this(server.Url, server.Config, time) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
        var response = await base.SendAsync(request, ct);

        try {
            // Only a PRESENT header is an answer. An absent one leaves the last known value alone —
            // it is what a server predating the feature sends, and forgetting on it would re-nudge
            // every session against any intermediary that strips unknown headers.
            if (response.Headers.TryGetValues(HttpClientExtensions.PlanHeader, out var values))
                PlanEntitlementStore.Set(serverUrl, values.FirstOrDefault(), config, time.GetUtcNow());
        } catch {
            // Header capture must never affect the response the caller gets back.
        }

        return response;
    }
}
