namespace Capacitor.Cli.Core.Policy;

using System.Text;
using System.Text.Json;

/// <summary>
/// Consults the server's policy judge once, inside the caller's budget. Every answer other than a
/// parsed allow, ask or deny is pass-through — <c>uncertain</c>, any non-200 (an older or gated
/// server answers 404 or 405), a timeout, a transport error — because the vendor's own behaviour
/// is what pass-through lands on.
/// </summary>
public sealed class PolicyJudgeClient(HttpClient http, string serverUrl, TimeProvider time) {
    public const string Route = "/api/policy/judge";

    /// <summary>Kept back from the budget the server is told, so its answer can still cross the
    /// network before the caller's own deadline.</summary>
    static readonly TimeSpan TransitAllowance = TimeSpan.FromMilliseconds(250);

    /// <summary>Builds the request with <paramref name="budget"/> stamped as the server's
    /// <c>budget_ms</c>, then sends it.</summary>
    public async Task<PolicyJudgeResult> ConsultAsync(
            Func<int, PolicyJudgeRequestV1> request, TimeSpan budget, CancellationToken ct = default) {
        if (budget <= TimeSpan.Zero) return PolicyJudgeResult.PassThrough(PolicyJudgeResult.Timeout);

        var serverBudget = (int)Math.Max(0, (budget - TransitAllowance).TotalMilliseconds);
        var body = JsonSerializer.Serialize(request(serverBudget), CapacitorJsonContext.Default.PolicyJudgeRequestV1);

        using var deadline = new CancellationTokenSource(budget, time);
        using var linked   = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try {
            using var content  = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync($"{serverUrl.TrimEnd('/')}{Route}", content, linked.Token);
            if (!response.IsSuccessStatusCode) return PolicyJudgeResult.PassThrough(PolicyJudgeResult.HttpError);

            var json = await response.Content.ReadAsStringAsync(linked.Token);
            return Interpret(json);
        } catch (OperationCanceledException) when (deadline.IsCancellationRequested) {
            return PolicyJudgeResult.PassThrough(PolicyJudgeResult.Timeout);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch {
            return PolicyJudgeResult.PassThrough(PolicyJudgeResult.TransportError);
        }
    }

    internal static PolicyJudgeResult Interpret(string json) {
        PolicyJudgeResponseV1? response;
        try {
            response = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.PolicyJudgeResponseV1);
        } catch (JsonException) {
            return PolicyJudgeResult.PassThrough(PolicyJudgeResult.MalformedResponse);
        }
        if (response is null) return PolicyJudgeResult.PassThrough(PolicyJudgeResult.MalformedResponse);

        var consultation = PolicyJudgeConsultationV1.Of(response);
        return response.Outcome switch {
            "allow" => new(PolicyOutcome.Allow, consultation, null, response.Rationale),
            "ask"   => new(PolicyOutcome.Ask, consultation, null, response.Rationale),
            "deny"  => new(PolicyOutcome.Deny, consultation, null, response.Rationale),
            "uncertain" => PolicyJudgeResult.PassThrough(PolicyJudgeResult.Uncertain, consultation),
            _ => PolicyJudgeResult.PassThrough(PolicyJudgeResult.MalformedResponse, consultation),
        };
    }
}
