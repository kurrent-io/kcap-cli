using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>Plays kcap-web's /api/signup/* for the create-a-workspace flow, recording what each provision call sent.</summary>
sealed class ProvisioningStubHandler : HttpMessageHandler {
    public const string BaseUrl = "https://signup.example";

    public List<string>    Paths                 { get; } = [];
    public bool            AvailabilityAvailable { get; init; } = true;
    public string?         AvailabilityReason    { get; init; }
    public HttpStatusCode  AvailabilityStatus    { get; init; } = HttpStatusCode.OK;
    public HttpStatusCode  ProvisionStatus       { get; init; } = HttpStatusCode.OK;
    public string?         StatusState           { get; init; }
    public List<string>    ProvisionTiers        { get; } = [];
    public List<string?>   ProvisionCodes        { get; } = [];
    public bool            OmitRequestUrl        { get; init; }

    /// <summary>Answers for successive provision calls; once used up, <see cref="ProvisionStatus"/> answers.</summary>
    public List<(HttpStatusCode Status, string Reason)> ProvisionReplies { get; init; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
        var path = request.RequestUri!.AbsolutePath;
        Paths.Add(path);

        if (path == "/api/signup/availability") {
            var available = AvailabilityAvailable && AvailabilityReason is null;

            return Json(AvailabilityStatus,
                $$"""{"available":{{(available ? "true" : "false")}},"reason":{{Quote(AvailabilityReason)}}}""");
        }

        if (path == "/api/signup/status")
            return Json(HttpStatusCode.OK,
                $$"""{"state":{{Quote(StatusState)}},"workosOrgId":"org_123","url":"https://acme.kcap.ai"}""");

        var sent = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
        ProvisionTiers.Add(sent["tier"]!.GetValue<string>());
        ProvisionCodes.Add(sent["code"]?.GetValue<string>());

        if (ProvisionTiers.Count <= ProvisionReplies.Count) {
            var (status, reason) = ProvisionReplies[ProvisionTiers.Count - 1];

            return !OmitRequestUrl && reason is "invite_required" or "invalid_code" or "code_exhausted"
                ? Json(status, $$"""{"reason":"{{reason}}","requestUrl":"{{BaseUrl}}/get-started#request"}""")
                : Json(status, $$"""{"reason":"{{reason}}"}""");
        }

        // 202 means "accepted, come back" and carries no org id, which is what sends the run into the poll.
        return ProvisionStatus == HttpStatusCode.Accepted
            ? Json(ProvisionStatus, """{"slug":"acme","state":"provisioning"}""")
            : Json(ProvisionStatus, """{"workosOrgId":"org_123","url":"https://acme.kcap.ai"}""");
    }

    static string Quote(string? value) => value is null ? "null" : $"\"{value}\"";

    static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
