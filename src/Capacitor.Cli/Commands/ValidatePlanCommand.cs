using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Plans;

namespace Capacitor.Cli.Commands;

class ValidatePlanCommand(ISessionsApi sessionsApi) {
    public Task<int> Handle(string sessionId) => HandleCore(sessionsApi, sessionId);

    /// <summary>
    /// Test-friendly core: caller owns the <see cref="ISessionsApi"/>. Two-call flow:
    /// the discovered plan artifact set, then the existing chain-widened recap for
    /// current-session work rows and AI-generated "what's done" summaries. A 404 on the
    /// artifacts route (old server without the route, or a non-visible session) falls back
    /// to <see cref="RenderLegacyAsync"/> — the original recap-only behavior, unchanged.
    /// Exit codes: 0 for a normal render or "no plan found" (absence is a valid answer); 2
    /// when the leading artifact's content — the declared plan document when one exists, else
    /// the server's primary — is unavailable (validation genuinely isn't possible); 1 for a
    /// refused or unreachable server.
    /// </summary>
    internal static async Task<int> HandleCore(ISessionsApi sessionsApi, string sessionId) {
        PlanArtifactsResult artifactsResult;

        try {
            artifactsResult = await sessionsApi.GetPlanArtifactsAsync(sessionId);
        } catch (CapacitorApiException ex) {
            await Console.Error.WriteLineAsync(ex.Message);

            return 1;
        }

        if (artifactsResult is PlanArtifactsResult.NotFound) {
            // Older server without the route, or the session/candidate isn't visible —
            // preserve the original recap-only behavior byte-for-byte.
            return await RenderLegacyAsync(sessionsApi, sessionId);
        }

        var response  = ((PlanArtifactsResult.Found)artifactsResult).Response;
        var primary   = response?.Primary;
        var artifacts = response?.Artifacts ?? [];

        if (primary is null && artifacts.Count == 0) {
            await Console.Out.WriteLineAsync("No plan found for this session.");

            return 0;
        }

        // Work done + AI summaries still come from the existing recap endpoint — the
        // plan-artifacts route only carries the discovered plan/spec/design/checklist set.
        RecapResult recapResult;

        try {
            recapResult = await sessionsApi.GetRecapAsync(sessionId, chain: true);
        } catch (CapacitorApiException ex) {
            await Console.Error.WriteLineAsync(ex.Message);

            return 1;
        }

        if (recapResult is RecapResult.NotFound) {
            await Console.Error.WriteLineAsync($"Session not found: {sessionId}");

            return 1;
        }

        var entries = ((RecapResult.Found)recapResult).Entries;

        // Work done: only from the current session being validated (matches the legacy filter).
        var work      = entries.Where(e => e.Type is "write" or "edit" && e.SessionId == sessionId).ToList();
        var summaries = entries.Where(e => e.Type == "whats_done").ToList();

        // The declared plan document leads: it is the one the agent said it executes, whatever
        // discovery ranked first.
        var lead   = artifacts.FirstOrDefault(a => a.Source == "declared" && a.Kind == "plan") ?? primary;
        var ledger = response?.Ledger;

        var leadUnavailable = await RenderPlanArtifacts(lead, artifacts);
        await RenderTasks(ledger);
        await RenderWhatsDoneAndInstructions(summaries, work, withTasks: ledger is { Tasks.Count: > 0 });

        // An unavailable lead means validation genuinely couldn't happen — distinct from both
        // success (0, including the "no plan found" case above, where absence of a plan is itself
        // a valid answer) and a generic error (1).
        return leadUnavailable ? 2 : 0;
    }

    /// <summary>
    /// Bracketed marker for a degraded artifact (<c>IsComplete == false</c>) — a newer revision
    /// exists but hasn't resolved yet, so this is the last known complete text. Single-sourced
    /// here (rather than referenced from the server) because the CLI has no dependency on
    /// <c>Capacitor.Server</c>; text and spacing must stay byte-for-byte identical to the
    /// server's <c>PlanRowRendering.DegradedText</c>.
    /// </summary>
    const string DegradedMarker = "[plan state: unresolved newer revision — last known complete text]";

    /// <summary>
    /// Renders the "## Plan" section from the discovery response: the lead artifact first —
    /// the declared plan document when one exists, else the server's designated primary (see
    /// <c>PlanArtifactComposer</c>) — followed by any other discovered artifacts in the
    /// order returned (newest-first). A degraded artifact (<c>is_complete == false</c>) is
    /// prefixed with <see cref="DegradedMarker"/>; a truncated one additionally gets a
    /// byte-count marker (degraded composes WITH truncated: degraded line first, then the
    /// truncation line, mirroring the server's <c>PlanRowRendering</c> ordering); an
    /// unavailable one renders a placeholder — and, when the lead itself is unavailable,
    /// an explicit note that full validation isn't possible without its content.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the lead artifact's content could not be retrieved
    /// (<c>content_state == "unavailable"</c>) — the caller uses this to exit 2 instead of 0,
    /// distinguishable from success (0) and from a generic error (1).
    /// </returns>
    static async Task<bool> RenderPlanArtifacts(PlanArtifactDto? lead, IReadOnlyList<PlanArtifactDto> artifacts) {
        var ordered = lead is null
            ? artifacts
            : new List<PlanArtifactDto> { lead }
                .Concat(artifacts.Where(a => a.ArtifactId != lead.ArtifactId))
                .ToList();

        var leadUnavailable = false;

        await Console.Out.WriteLineAsync("## Plan");
        await Console.Out.WriteLineAsync();

        foreach (var artifact in ordered) {
            var isLead = lead is not null && artifact.ArtifactId == lead.ArtifactId;

            if (!artifact.IsComplete) {
                await Console.Out.WriteLineAsync(DegradedMarker);
            }

            // "truncated" with null Content is treated like "unavailable" (review
            // finding 2): the server contract pairs content_state=="truncated" with non-null
            // Content, but a malformed/edge response with Content == null must not render the
            // nonsensical "first 0 of ... bytes" — fall through to the unavailable placeholder.
            var effectiveState = artifact.ContentState == "truncated" && artifact.Content is null
                ? "unavailable"
                : artifact.ContentState;

            switch (effectiveState) {
                case "truncated": {
                    var n = Encoding.UTF8.GetByteCount(artifact.Content!);
                    // OriginalBytes is nullable — a well-formed truncated artifact always
                    // carries it, but fall back to "?" rather than emit "of  bytes" if it's
                    // ever missing.
                    var total = artifact.OriginalBytes?.ToString() ?? "?";
                    await Console.Out.WriteLineAsync($"[plan truncated: first {n} of {total} bytes]");
                    await Console.Out.WriteLineAsync(artifact.Content!);

                    break;
                }
                case "unavailable": {
                    await Console.Out.WriteLineAsync("[plan content unavailable due to size bounds]");

                    if (isLead) {
                        leadUnavailable = true;
                        await Console.Out.WriteLineAsync(
                            "Validation is not possible: the plan content could not be retrieved (exceeds size bounds).");
                    }

                    break;
                }
                default: {
                    if (artifact.Content is not null) {
                        await Console.Out.WriteLineAsync(artifact.Content);
                    }

                    break;
                }
            }
        }

        await Console.Out.WriteLineAsync();

        return leadUnavailable;
    }

    /// <summary>The declared task list, when the server sent one with at least one task. Omitted
    /// otherwise, so a server without the ledger renders exactly as before.</summary>
    static async Task RenderTasks(PlanLedgerDto? ledger) {
        if (ledger is null || ledger.Tasks.Count == 0) return;

        await Console.Out.WriteLineAsync("## Tasks");
        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync(ledger.TotalKnown
            ? $"{ledger.Completed} of {ledger.Total} completed"
            : $"{ledger.Completed} completed, total unknown");

        if (!ledger.IsComplete)
            await Console.Out.WriteLineAsync(
                $"[tasks incomplete: {ledger.WithheldContributions} contribution(s) from sessions you cannot see were withheld]");

        await Console.Out.WriteLineAsync();

        foreach (var task in ledger.Tasks) {
            var partial = task.StatusPartial ? " (status partial)" : "";
            await Console.Out.WriteLineAsync($"{task.Ordinal}. [{task.Status}] {task.Title} ({task.Source}){partial}");

            if (!string.IsNullOrWhiteSpace(task.Note))
                await Console.Out.WriteLineAsync($"   note: {task.Note}");
        }

        await Console.Out.WriteLineAsync();
    }

    /// <summary>Shared "## What's Done" + "## Instructions" rendering, used by both the
    /// plan-artifacts path and the legacy recap-only path so the two stay in sync.</summary>
    static async Task RenderWhatsDoneAndInstructions(List<RecapEntry> summaries, List<RecapEntry> work, bool withTasks = false) {
        await Console.Out.WriteLineAsync("## What's Done");
        await Console.Out.WriteLineAsync();

        if (summaries.Count > 0) {
            await Console.Out.WriteLineAsync("### Summary");
            await Console.Out.WriteLineAsync();

            foreach (var summary in summaries) {
                await Console.Out.WriteLineAsync(summary.Content);
            }

            await Console.Out.WriteLineAsync();
        }

        await Console.Out.WriteLineAsync("### Details");
        await Console.Out.WriteLineAsync();

        if (work.Count == 0) {
            await Console.Out.WriteLineAsync("No file writes or edits recorded.");
        } else {
            foreach (var entry in work) {
                var label = entry.Type == "write" ? "Write" : "Edit";
                var path  = entry.FilePath ?? "unknown";
                await Console.Out.WriteLineAsync($"- {label}: {path}");
            }
        }

        await Console.Out.WriteLineAsync();

        await Console.Out.WriteLineAsync("## Instructions");
        await Console.Out.WriteLineAsync();

        await Console.Out.WriteLineAsync(
            "Compare the plan above against the summary and file list under \"What's Done\". Identify any planned items that were NOT completed. If everything is done, confirm that. If there are gaps, list them and complete the remaining work now."
        );

        if (withTasks)
            await Console.Out.WriteLineAsync(
                "The Tasks section is the declared checklist: a task still pending or in_progress is not done, whatever the file list suggests."
            );
    }

    /// <summary>
    /// Original recap-only behavior, preserved byte-for-byte for old servers that
    /// don't yet expose <c>GET /api/sessions/{id}/plan-artifacts</c> (or a session/candidate the
    /// route can't resolve). Plans come from <c>recap</c> entries of type "plan" across the
    /// session chain; work/summaries are filtered exactly as before.
    /// </summary>
    static async Task<int> RenderLegacyAsync(ISessionsApi sessionsApi, string sessionId) {
        RecapResult result;

        try {
            result = await sessionsApi.GetRecapAsync(sessionId, chain: true);
        } catch (CapacitorApiException ex) {
            await Console.Error.WriteLineAsync(ex.Message);

            return 1;
        }

        if (result is RecapResult.NotFound) {
            await Console.Error.WriteLineAsync($"Session not found: {sessionId}");

            return 1;
        }

        var entries = ((RecapResult.Found)result).Entries;

        if (entries.Count == 0) {
            await Console.Out.WriteLineAsync("No plan found for this session.");

            return 0;
        }

        // Plans can come from any session in the chain (continuation planContent or ExitPlanMode write)
        var plans = entries.Where(e => e.Type == "plan").ToList();
        // Work done: only from the current session being validated
        var work = entries.Where(e => e.Type is "write" or "edit" && e.SessionId == sessionId).ToList();
        // AI-generated summaries
        var summaries = entries.Where(e => e.Type == "whats_done").ToList();

        if (plans.Count == 0) {
            await Console.Out.WriteLineAsync("No plan found for this session.");

            return 0;
        }

        // Output plan(s)
        await Console.Out.WriteLineAsync("## Plan");
        await Console.Out.WriteLineAsync();

        foreach (var plan in plans) {
            await Console.Out.WriteLineAsync(plan.Content);
        }

        await Console.Out.WriteLineAsync();

        await RenderWhatsDoneAndInstructions(summaries, work);

        return 0;
    }
}
