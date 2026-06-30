using System.Text.Json;

namespace Capacitor.Cli.Core.Eval;

/// <summary>
/// Fetches the full eval catalog (server-rendered per-question prompts +
/// retrospective prompt + versions) from <c>GET /api/eval/catalog</c> (AI-9
/// Phase 3). Returns <c>null</c> on HTTP failure, deserialization error, or a
/// failed integrity check so callers abort the run BEFORE expensive judge work
/// (SF#4) -- the catalog is non-optional, so there is no safe fallback. Fetched
/// once per run and threaded through <see cref="EvalService.EvalContext"/>.
/// </summary>
public static class EvalCatalogClient {
    public static async Task<EvalCatalogDto?> FetchAsync(
            string            baseUrl,
            HttpClient        httpClient,
            IEvalObserver     observer,
            CancellationToken ct
        ) {
        try {
            using var resp = await httpClient.GetWithRetryAsync($"{baseUrl}/api/eval/catalog", ct: ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized) {
                observer.OnFailed("authentication failed -- run 'kcap login' to re-authenticate");
                return null;
            }
            if (!resp.IsSuccessStatusCode) {
                observer.OnFailed($"failed to load eval catalog: HTTP {(int)resp.StatusCode}");
                return null;
            }

            var json   = await resp.Content.ReadAsStringAsync(ct);
            var parsed = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.EvalCatalogDto);
            if (parsed is null) {
                observer.OnFailed("eval catalog response was not valid JSON");
                return null;
            }

            // SF#4 -- fail-fast integrity checks before any judge invocation.
            // A JSON `"questions": null` overrides the [] initializer, so guard the null case
            // explicitly: `is not { Count: > 0 }` is true for both null and empty -> fail closed
            // rather than NRE on `.Count` (which the catch below would NOT swallow).
            if (parsed.Questions is not { Count: > 0 }) {
                observer.OnFailed("eval catalog is empty or missing the questions array");
                return null;
            }
            if (string.IsNullOrWhiteSpace(parsed.RetrospectivePrompt)) {
                observer.OnFailed("eval catalog retrospective_prompt is empty");
                return null;
            }
            if (string.IsNullOrWhiteSpace(parsed.RetrospectivePromptVersion)) {
                observer.OnFailed("eval catalog retrospective_prompt_version is empty");
                return null;
            }
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var q in parsed.Questions) {
                // A `"questions": [null]` element deserializes to a null entry; reject it
                // (fail closed) rather than NRE on the field accesses below.
                if (q is null) {
                    observer.OnFailed("eval catalog contains a null question entry");
                    return null;
                }
                if (string.IsNullOrWhiteSpace(q.Id)
                        || string.IsNullOrWhiteSpace(q.QuestionText)
                        || string.IsNullOrWhiteSpace(q.Prompt)
                        || string.IsNullOrWhiteSpace(q.PromptVersion)) {
                    observer.OnFailed($"eval catalog question is malformed (missing id/question_text/prompt/prompt_version): '{q.Id}'");
                    return null;
                }
                // N3 -- category and title are also required; a blank field indicates a corrupt
                // or partially-migrated catalog response and must be rejected before judge work.
                if (string.IsNullOrWhiteSpace(q.Category)) {
                    observer.OnFailed($"Catalog question '{q.Id}' has blank category.");
                    return null;
                }
                if (string.IsNullOrWhiteSpace(q.Title)) {
                    observer.OnFailed($"Catalog question '{q.Id}' has blank title.");
                    return null;
                }
                if (!seenIds.Add(q.Id)) {
                    observer.OnFailed($"eval catalog has a duplicate question id: '{q.Id}'");
                    return null;
                }
            }
            return parsed;
        } catch (HttpRequestException ex) {
            observer.OnFailed($"failed to load eval catalog: {ex.Message}");
            return null;
        } catch (JsonException ex) {
            observer.OnFailed($"eval catalog response was not valid JSON: {ex.Message}");
            return null;
        }
    }
}
