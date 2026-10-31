using System.Net;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>Waits for the local server's <c>/healthz/ready</c>, which it answers only once both stores are up and
/// the schema is migrated.</summary>
static class LocalServerReadiness {
    /// <summary>Long enough for a first start, which runs <c>initdb</c>, and for a schema migration after an
    /// upgrade.</summary>
    public static readonly TimeSpan StartBudget = TimeSpan.FromMinutes(10);

    static readonly TimeSpan PollGap        = TimeSpan.FromSeconds(1);
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <param name="http">A client that sends no credential: the probe needs none.</param>
    public static async Task WaitAsync(
            HttpClient http, LocalServerUnit unit, TimeSpan budget, TimeProvider time, CancellationToken ct) {
        var deadline = time.GetUtcNow() + budget;
        var url      = new Uri($"{unit.BaseUrl}/healthz/ready");

        while (true) {
            if (await ReadyAsync(http, url, time, ct)) return;

            if (time.GetUtcNow() >= deadline) {
                // The server logs through its own rolling log once it starts, so the output log is often empty.
                var logs = LocalLogFiles.Current(unit.Root);
                var see  = logs.Count > 0 ? string.Join(" and ", logs) : $"{unit.OutputLog} and the logs beside it";

                throw new LocalLifecycleException(
                    $"The local server did not report ready on {unit.BaseUrl} within {budget.TotalMinutes:0} minutes. See {see}.");
            }

            await Task.Delay(PollGap, time, ct);
        }
    }

    static async Task<bool> ReadyAsync(HttpClient http, Uri url, TimeProvider time, CancellationToken ct) {
        using var attempt = new CancellationTokenSource(RequestTimeout, time);
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, attempt.Token);

        try {
            using var response = await http.GetAsync(url, linked.Token);
            return response.StatusCode == HttpStatusCode.OK;
        } catch (HttpRequestException) {
            return false;
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            return false;
        }
    }
}
