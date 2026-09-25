using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.Eval.Contracts;

namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>Certifies a question's refs through the server within one shared budget. A ref the server defers for its
/// per-request work budget is resent; one the budget never reaches is dropped and counted.</summary>
public sealed class EvidenceCitationClient(HttpClient http, string baseUrl, string sessionId, TimeProvider time) {
    public const int MaxRefsPerRequest = 64;
    public static readonly TimeSpan CertificationBudget = TimeSpan.FromSeconds(90);

    /// <summary>An answer under any scope version but <paramref name="scopeVersion"/> is scope loss: its digests belong to
    /// another scope.</summary>
    public async Task<EvidenceCertificationOutcome> CertifyAsync(string token, string scopeVersion, IReadOnlyList<string> refs, CancellationToken ct) {
        var order    = refs.Distinct(StringComparer.Ordinal).ToList();
        var digests  = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending  = new List<string>(order);
        var inFlight = new List<string>();
        var dropped  = 0;
        var url      = $"{baseUrl.TrimEnd('/')}/api/sessions/{Uri.EscapeDataString(sessionId)}/evidence-citations";

        using var budget = new CancellationTokenSource(CertificationBudget, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        try {
            while (pending.Count > 0) {
                inFlight = pending.Take(MaxRefsPerRequest).ToList();
                pending.RemoveRange(0, inFlight.Count);

                var body = JsonSerializer.Serialize(new EvidenceCitationsRequestDto { Token = token, Refs = inFlight }, CapacitorJsonContext.Default.EvidenceCitationsRequestDto);
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var resp    = await http.PostAsync(url, content, linked.Token);
                if ((int)resp.StatusCode is 404 or 409) return new(ScopeLost: true, [], order.Count);
                if (!resp.IsSuccessStatusCode) { dropped += inFlight.Count; inFlight.Clear(); continue; }

                var answer = JsonSerializer.Deserialize(await resp.Content.ReadAsStringAsync(linked.Token), CapacitorJsonContext.Default.EvidenceCitationsResponseDto);
                if (answer is not null && answer.ScopeVersion != scopeVersion) return new(ScopeLost: true, [], order.Count);
                var resend = new List<string>();
                var progressed = false;
                foreach (var entry in answer?.Citations ?? []) {
                    if (entry is { State: "certified", Digest: { } digest }) { digests[entry.Ref] = digest; progressed = true; }
                    else if (entry is { State: "refused", Code: "work_budget" }) resend.Add(entry.Ref);
                    else { dropped++; progressed = true; }
                }
                inFlight.Clear();
                if (!progressed) { dropped += resend.Count; continue; }
                pending.InsertRange(0, resend);
            }
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            dropped += inFlight.Count + pending.Count;
        } catch (HttpRequestException) {
            dropped += inFlight.Count + pending.Count;
        } catch (JsonException) {
            dropped += inFlight.Count + pending.Count;
        }

        return new(false, [.. order.Where(digests.ContainsKey).Select(r => new EvalEvidenceCitation { Ref = r, Digest = digests[r] })], dropped);
    }
}
