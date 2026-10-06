namespace Capacitor.Cli.Policy;

using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Policy;

/// <summary>
/// A hook's way to the judge: builds the authenticated client inside the same budget the
/// consultation spends, so a slow credential resolution eats into the judge's time rather than
/// past the hook's ceiling. Nothing here reaches the network unless a seam actually consults.
/// </summary>
internal sealed class PolicyJudgeGateway(Func<Task<AuthAttempt>> clientFactory, string? serverUrl, TimeProvider time) {
    public const string ClientUnavailable = "judge_client_unavailable";

    public static PolicyJudgeGateway ForHook(ICapacitorHttpClient http, string? serverUrl, TimeProvider time) =>
        new(() => http.ForHookAsync(), serverUrl, time);

    public async Task<PolicyJudgeResult> ConsultAsync(Func<int, PolicyJudgeRequestV1> request, TimeSpan budget) {
        if (!HookHttp.IsPostable(serverUrl)) return PolicyJudgeResult.PassThrough(ClientUnavailable);

        if (budget <= TimeSpan.Zero) return PolicyJudgeResult.PassThrough(PolicyJudgeResult.Timeout);

        var started = time.GetTimestamp();
        var attempt = await BoundedAuth.CreateClientWithinAsync(clientFactory, budget, time);
        if (attempt is not { } a)
            return PolicyJudgeResult.PassThrough(
                time.GetElapsedTime(started) >= budget ? PolicyJudgeResult.Timeout : ClientUnavailable);
        using var client = a.Client;
        if (!a.Usable) return PolicyJudgeResult.PassThrough(ClientUnavailable);

        var remaining = budget - time.GetElapsedTime(started);
        return await new PolicyJudgeClient(client, serverUrl!, time).ConsultAsync(request, remaining);
    }
}
