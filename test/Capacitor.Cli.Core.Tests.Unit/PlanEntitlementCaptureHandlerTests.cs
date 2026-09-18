using System.Net;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// The passive capture path: a response carrying <c>X-Kcap-Plan</c> lands in
/// <see cref="PlanEntitlementStore"/>; one without it leaves the last answer alone. Drives the
/// internal <c>PlanEntitlementCaptureHandler</c> over a stub inner handler (no network).
/// </summary>
public class PlanEntitlementCaptureHandlerTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(response);
    }

    static async Task SendThrough(string serverUrl, HttpResponseMessage response, ConfigRoot config) {
        var capture = new PlanEntitlementCaptureHandler(serverUrl, config, TimeProvider.System) { InnerHandler = new StubHandler(response) };
        using var client = new HttpClient(capture);
        using var _ = await client.GetAsync(serverUrl);
    }

    static string NewUrl() => $"https://cap-{Guid.NewGuid():N}.example.com";

    static HttpResponseMessage WithPlan(string? value) {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        if (value is not null) response.Headers.Add(HttpClientExtensions.PlanHeader, value);
        return response;
    }

    [Test]
    public async Task Response_WithHeader_CapturesEntitlements() {
        var url = NewUrl();

        await SendThrough(url, WithPlan("work_items=0,projects=1"), Config.Root);

        var plan = PlanEntitlementStore.Get(url, Config.Root, DateTimeOffset.UtcNow);
        await Assert.That(plan.Allows(PlanFeature.WorkItems)).IsFalse();
        await Assert.That(plan.Allows(PlanFeature.Projects)).IsTrue();
    }

    [Test]
    public async Task Response_WithoutHeader_LeavesTheLastAnswerAlone() {
        // A server predating the feature, or an intermediary that strips unknown headers, must not
        // look like an upgrade — otherwise every such response re-enables the nudge.
        var url = NewUrl();
        await SendThrough(url, WithPlan("work_items=0"), Config.Root);

        await SendThrough(url, WithPlan(null), Config.Root);

        await Assert.That(PlanEntitlementStore.Get(url, Config.Root, DateTimeOffset.UtcNow).Allows(PlanFeature.WorkItems)).IsFalse();
    }

    [Test]
    public async Task Response_WithoutHeader_AndNothingCached_AllowsEverything() {
        var url = NewUrl();

        await SendThrough(url, WithPlan(null), Config.Root);

        await Assert.That(PlanEntitlementStore.Get(url, Config.Root, DateTimeOffset.UtcNow).Allows(PlanFeature.WorkItems)).IsTrue();
    }

    [Test]
    public async Task Response_WithAnAllowingHeader_ClearsAnEarlierDenial() {
        var url = NewUrl();
        await SendThrough(url, WithPlan("work_items=0"), Config.Root);

        await SendThrough(url, WithPlan("work_items=1"), Config.Root);

        await Assert.That(PlanEntitlementStore.Get(url, Config.Root, DateTimeOffset.UtcNow).Allows(PlanFeature.WorkItems)).IsTrue();
    }
}
