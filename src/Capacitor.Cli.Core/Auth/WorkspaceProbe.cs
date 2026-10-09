using System.Net;

namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// Asks a workspace whether it is there, through the one route every server serves without a
/// credential. Only a 404 counts as gone: a 5xx or a dropped connection is an outage, and treating
/// it as a missing workspace would offer to create a second one.
/// </summary>
public static class WorkspaceProbe {
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task<WorkspaceAnswer> AskAsync(
            HttpClient anonymous, string origin, TimeProvider time, CancellationToken ct = default) {
        using var timeout = new CancellationTokenSource(Timeout, time);
        using var cts     = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try {
            using var response = await anonymous.GetAsync($"{origin.TrimEnd('/')}/auth/config", cts.Token);

            return response.IsSuccessStatusCode                   ? WorkspaceAnswer.Live
                 : response.StatusCode == HttpStatusCode.NotFound ? WorkspaceAnswer.Gone
                 : WorkspaceAnswer.NoAnswer;
        } catch (Exception ex) when (ex is HttpRequestException or IOException
                                     || (ex is OperationCanceledException && !ct.IsCancellationRequested)) {
            return WorkspaceAnswer.NoAnswer;
        }
    }
}
