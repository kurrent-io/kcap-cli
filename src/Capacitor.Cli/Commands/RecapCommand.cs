using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.PrDetection;

// ReSharper disable MethodHasAsyncOverload

namespace Capacitor.Cli.Commands;

class RecapCommand(
        ConfigRoot config, ISessionsApi sessionsApi, IRepositoriesApi repositoriesApi,
        GitProviderRouter router, WorkingDirectory workdir, TimeProvider time) {
    public async Task<int> HandleRepoRecap(int limit = 10) {
        var repo = await RepositoryDetection.DetectRepositoryAsync(router, config, workdir.Path, time);

        if (repo?.Owner is null || repo.RepoName is null) {
            Console.Error.WriteLine("Not in a git repository with a remote origin.");

            return 1;
        }

        var hash = RepoHashHelper.ComputeRepoHash(repo.Owner, repo.RepoName);

        List<RepoRecapEntry> entries;

        try {
            entries = await repositoriesApi.GetRecapsAsync(hash, limit);
        } catch (CapacitorApiException ex) {
            Console.Error.WriteLine(ex.Message);

            return 1;
        }

        if (entries.Count == 0) {
            await Console.Out.WriteLineAsync($"No session summaries found for {repo.Owner}/{repo.RepoName}.");

            return 0;
        }

        await Console.Out.WriteLineAsync($"# Recent sessions for {repo.Owner}/{repo.RepoName}");
        await Console.Out.WriteLineAsync();

        foreach (var entry in entries) {
            var date  = entry.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            var title = entry.Title ?? "(untitled)";
            await Console.Out.WriteLineAsync($"## {title}");
            await Console.Out.WriteLineAsync($"*Session {entry.SessionId} | {date}*");
            await Console.Out.WriteLineAsync();
            await Console.Out.WriteLineAsync(entry.Summary);
            await Console.Out.WriteLineAsync();
            await Console.Out.WriteLineAsync($"Full transcript: `kcap recap --full {entry.SessionId}`");
            await Console.Out.WriteLineAsync();
            await Console.Out.WriteLineAsync("---");
            await Console.Out.WriteLineAsync();
        }

        return 0;
    }

    public async Task<int> HandleRecap(string sessionId, bool chain, bool full = false) {
        RecapResult result;

        try {
            result = await sessionsApi.GetRecapAsync(sessionId, chain);
        } catch (CapacitorApiException ex) {
            Console.Error.WriteLine(ex.Message);

            return 1;
        }

        if (result is RecapResult.NotFound) {
            Console.Error.WriteLine($"Session not found: {sessionId}");

            return 1;
        }

        var entries = ((RecapResult.Found)result).Entries;

        if (entries.Count == 0) {
            await Console.Out.WriteLineAsync("No recap entries found.");

            return 0;
        }

        return full ? await PrintFull(entries, chain, sessionId) : await PrintSummaryWithOutline(entries, chain, sessionId);
    }

    async Task<int> PrintSummaryWithOutline(List<RecapEntry> entries, bool chain, string sessionId) {
        var summaries = entries.Where(e => e.Type is "whats_done" or "plan").ToList();

        // Fetch outlines for the CONCRETE ids /recap resolved (a slug or chain maps to real session
        // ids, tagged on the entries) — never the raw input. /turns doesn't resolve slugs, so using
        // the slug would 404 and the outline would silently vanish. Fall back to the input only when
        // /recap returned no entries at all. Headers appear whenever more than one session is shown.
        var sessionIds  = OutlineSessionIds(entries, sessionId);
        var showHeaders = chain || sessionIds.Count > 1;

        var printedAnything = false;

        foreach (var sid in sessionIds) {
            if (showHeaders) {
                Console.Out.WriteLine($"# Session {sid}");
                Console.Out.WriteLine();
            }

            // Summary section for this session (plan + whats_done), reusing the existing types.
            foreach (var entry in summaries.Where(e => e.SessionId == sid)) {
                switch (entry.Type) {
                    case "plan":
                        Console.Out.WriteLine("## Plan");
                        Console.Out.WriteLine(entry.Content);
                        Console.Out.WriteLine();
                        printedAnything = true;

                        break;
                    case "whats_done":
                        Console.Out.WriteLine("## Summary");
                        Console.Out.WriteLine(entry.Content);
                        Console.Out.WriteLine();
                        printedAnything = true;

                        break;
                }
            }

            // Turn outline for this session.
            var outline = await FetchTurnOutline(sid);

            if (outline.Length > 0) {
                Console.Out.WriteLine(StatusGlyphs.ForConsole(outline));
                printedAnything = true;
            }
        }

        if (!printedAnything) {
            Console.Out.WriteLine("No summary or turns available yet. Use `kcap recap --full` to see the raw transcript.");

            return 0;
        }

        Console.Out.WriteLine(StatusGlyphs.ForConsole(DrillDownPointer(sessionIds)));

        return 0;
    }

    /// <summary>Session ids present in recap entries, first-seen order, nulls dropped.</summary>
    internal static List<string> DistinctSessionIds(List<RecapEntry> entries) =>
        entries.Select(e => e.SessionId).OfType<string>().Distinct().ToList();

    /// <summary>
    /// Concrete session id(s) to fetch turn outlines for: the ids <c>/recap</c> resolved (a slug or
    /// chain maps to real ids, tagged on the entries), or the raw input when <c>/recap</c> returned
    /// no entries. Never the unresolved slug — <c>/turns</c> doesn't resolve slugs, so that would
    /// silently drop the outline.
    /// </summary>
    internal static List<string> OutlineSessionIds(List<RecapEntry> entries, string inputSessionId) {
        var resolved = DistinctSessionIds(entries);

        return resolved.Count > 0 ? resolved : [inputSessionId];
    }

    /// <summary>
    /// Drill-down hint. For a single resolved session, emit its concrete id so the command targets
    /// THIS session (a bare <c>--get-turn</c> falls back to the current/env session); for multiple
    /// sessions, a <c>&lt;sessionId&gt;</c> placeholder the caller substitutes per the # Session headers.
    /// </summary>
    internal static string DrillDownPointer(IReadOnlyList<string> sessionIds) =>
        sessionIds.Count == 1
            ? $"→ kcap recap --get-turn <N> {sessionIds[0]} for one turn's full detail"
            : "→ kcap recap --get-turn <N> <sessionId> for one turn's full detail";

    /// <summary>GETs /turns for one session and renders the outline block, or "" on any non-success.</summary>
    async Task<string> FetchTurnOutline(string sessionId) {
        try {
            // Escape the id — session ids can be free-form slugs; matches the MCP BuildTurnsUrl path.
            var result = await sessionsApi.GetTurnsAsync(Uri.EscapeDataString(sessionId));

            return result is TurnsResult.Found(var json) ? FormatTurnOutline(json) : "";
        } catch (CapacitorApiException) {
            // Outline is best-effort enrichment on top of the summary — never fail recap on it.
            return "";
        }
    }

    /// <summary>Every captured prompt, reply, tool call and result, per session: each closed turn's
    /// trace, the same one <c>--get-turn</c> prints. The turn list is fetched for the concrete ids
    /// <c>/recap</c> resolved, never a slug, which <c>/turns</c> does not resolve.</summary>
    async Task<int> PrintFull(List<RecapEntry> entries, bool chain, string sessionId) {
        var sessionIds  = OutlineSessionIds(entries, sessionId);
        var showHeaders = chain || sessionIds.Count > 1;

        foreach (var sid in sessionIds) {
            if (showHeaders) {
                await Console.Out.WriteLineAsync($"# Session {sid}");
                await Console.Out.WriteLineAsync();
            }

            var own = entries.Where(e => e.SessionId == sid || (e.SessionId is null && sid == sessionIds[0])).ToList();

            await Console.Out.WriteAsync(await RecapTranscript.RenderSessionAsync(
                own, await FetchTurnList(sid), index => sessionsApi.GetTurnAsync(Uri.EscapeDataString(sid), index)));
        }

        return 0;
    }

    /// <summary>The session's closed turns as the server sent them, or null when there are none to read:
    /// the transcript then falls back to the <c>/recap</c> rows and says so.</summary>
    async Task<string?> FetchTurnList(string sessionId) {
        try {
            return await sessionsApi.GetTurnsAsync(Uri.EscapeDataString(sessionId)) is TurnsResult.Found(var json) ? json : null;
        } catch (CapacitorApiException ex) {
            await Console.Error.WriteLineAsync($"Turns for {sessionId} could not be read: {ex.Message}");

            return null;
        }
    }

    /// <summary>
    /// `kcap recap --per-turn &lt;sessionId&gt;` — prints a compact per-turn index
    /// (turn #, prompt excerpt, tool names, file count, token count, time range),
    /// one block per turn. Parses the server's snake_case JSON with JsonDocument
    /// (the CLI has no TurnClosed DTO and JsonDocument is AOT-safe).
    /// </summary>
    public async Task<int> HandlePerTurnRecap(string sessionId) {
        TurnsResult result;

        try {
            result = await sessionsApi.GetTurnsAsync(sessionId);
        } catch (CapacitorApiException ex) {
            Console.Error.WriteLine(ex.Message);

            return 1;
        }

        if (result is TurnsResult.NotFound) {
            Console.Error.WriteLine($"Session not found: {sessionId}");

            return 1;
        }

        var json = ((TurnsResult.Found)result).Json;

        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) {
            await Console.Out.WriteLineAsync("No turns found. The session may still be active or has no recorded turns.");

            return 0;
        }

        await Console.Out.WriteLineAsync($"# Turns for session {sessionId}");
        await Console.Out.WriteLineAsync();

        foreach (var turn in doc.RootElement.EnumerateArray()) {
            var turnIndex  = turn.TryGetProperty("turn_index", out var ti) ? ti.GetInt32() : -1;
            var userPrompt = turn.TryGetProperty("user_prompt", out var up) ? up.GetString() ?? "" : "";
            var prompt     = userPrompt.Length == 0
                ? $"(turn {turnIndex})"
                : userPrompt.Length > 80 ? userPrompt[..80] + "…" : userPrompt;

            var toolNames = ExtractToolNames(turn);
            var tools     = toolNames.Count > 0 ? string.Join(", ", toolNames) : "—";

            var files  = turn.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array ? f.GetArrayLength() : 0;
            var tokens = turn.TryGetProperty("total_tokens", out var tk) ? tk.GetInt64() : 0;
            var time   = $"{FormatTurnTime(turn, "first_event_at")}–{FormatTurnTime(turn, "last_event_at")}";

            await Console.Out.WriteLineAsync(StatusGlyphs.ForConsole($"Turn {turnIndex,3}: {prompt}"));
            await Console.Out.WriteLineAsync(StatusGlyphs.ForConsole($"         tools={tools}  files={files}  tokens={tokens}  time={time}"));
            await Console.Out.WriteLineAsync();
        }

        Console.Error.WriteLine($"Use `kcap recap --get-turn <N> {sessionId}` for full turn detail.");

        return 0;
    }

    /// <summary>
    /// `kcap recap --get-turn &lt;N&gt; [sessionId]` — prints the formatted event transcript for a
    /// single turn (user prompt, tool calls, assistant text). The turn number is the flag's value;
    /// the session id is the usual positional (or comes from the current session).
    /// </summary>
    public async Task<int> HandleGetTurn(string sessionId, int turnIndex) {
        TurnDetailResult result;

        try {
            result = await sessionsApi.GetTurnAsync(sessionId, turnIndex);
        } catch (CapacitorApiException ex) {
            Console.Error.WriteLine(ex.Message);

            return 1;
        }

        if (result is TurnDetailResult.NotFound) {
            Console.Error.WriteLine($"Turn {turnIndex} not found for session {sessionId}");

            return 1;
        }

        var json = ((TurnDetailResult.Found)result).Json;

        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("trace", out var trace) || trace.ValueKind != JsonValueKind.Array) {
            await Console.Out.WriteLineAsync("No trace available for this turn.");

            return 0;
        }

        await Console.Out.WriteLineAsync(StatusGlyphs.ForConsole($"# Turn {turnIndex} — session {sessionId}"));
        await Console.Out.WriteLineAsync();
        await Console.Out.WriteAsync(RecapTranscript.RenderTrace(trace));

        return 0;
    }

    static string FormatTurnTime(JsonElement turn, string prop) =>
        turn.TryGetProperty(prop, out var el)
        && el.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(el.GetString(), out var dto)
            ? dto.ToLocalTime().ToString("HH:mm")
            : "?";

    /// <summary>Distinct tool names for a turn JSON object, in first-seen order.</summary>
    internal static List<string> ExtractToolNames(JsonElement turn) {
        var names = new List<string>();

        if (turn.TryGetProperty("tools", out var toolsEl) && toolsEl.ValueKind == JsonValueKind.Array) {
            foreach (var t in toolsEl.EnumerateArray()) {
                if (t.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } nm && !names.Contains(nm)) {
                    names.Add(nm);
                }
            }
        }

        return names;
    }

    /// <summary>
    /// Renders the `## Turns` outline block for a session: one line per turn, using the turn's
    /// prose summary when present, otherwise a truncated user-prompt excerpt + tool/file metadata.
    /// Returns "" for a missing/empty/non-array payload so the caller can skip the section.
    /// </summary>
    internal static string FormatTurnOutline(string turnsJson) {
        try {
            using var doc = JsonDocument.Parse(turnsJson);

            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) {
                return "";
            }

            var sb = new StringBuilder();
            sb.AppendLine("## Turns");

            foreach (var turn in doc.RootElement.EnumerateArray()) {
                sb.AppendLine(FormatOutlineLine(turn));
            }

            return sb.ToString();
        } catch (JsonException) {
            // Empty/truncated body, or a non-JSON page (e.g. a proxy 200 with an HTML error).
            // The outline is best-effort enrichment, so treat unparseable input as "nothing to show"
            // — same contract as the empty/non-array case above — rather than crashing recap.
            return "";
        } catch (InvalidOperationException) {
            // Well-formed JSON array, but a field arrives as the wrong type (e.g. turn_index as a
            // JSON string) so GetInt32()/GetString() throw. Degrade to summary-only, never crash.
            return "";
        }
    }

    static string FormatOutlineLine(JsonElement turn) {
        var index = turn.TryGetProperty("turn_index", out var ti) ? ti.GetInt32() : -1;
        var prose = turn.TryGetProperty("prose", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        if (!string.IsNullOrWhiteSpace(prose)) {
            return $"{index,3}  {CollapseWhitespace(prose!)}";
        }

        var raw     = turn.TryGetProperty("user_prompt", out var up) && up.ValueKind == JsonValueKind.String ? up.GetString() ?? "" : "";
        var prompt  = CollapseWhitespace(raw);
        var excerpt = prompt.Length == 0 ? "(no prompt)" : prompt.Length > 80 ? prompt[..80] + "…" : prompt;

        var toolNames = ExtractToolNames(turn);
        var fileCount = turn.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array ? f.GetArrayLength() : 0;

        var parts = new List<string>();
        if (toolNames.Count > 0) parts.Add(string.Join(", ", toolNames));
        if (fileCount > 0) parts.Add($"{fileCount} files");
        var meta = parts.Count > 0 ? $"  [{string.Join(" · ", parts)}]" : "";

        return $"{index,3}  {excerpt}{meta}";
    }

    /// <summary>
    /// Collapses all runs of internal whitespace (spaces, tabs, newlines) to single spaces and trims
    /// the ends, so a multi-line prose/prompt stays on one outline line. AOT-safe (no runtime Regex):
    /// splitting on null splits on all Unicode whitespace, RemoveEmptyEntries drops the gaps.
    /// </summary>
    static string CollapseWhitespace(string s) =>
        string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
