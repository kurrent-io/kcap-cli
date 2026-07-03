using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Antigravity;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Discover + classify + import historical Google Antigravity conversations (AI-1160).
/// Each conversation is a brain dir under
/// <c>~/.gemini/antigravity/brain/&lt;id&gt;/.system_generated/logs/transcript_full.jsonl</c> —
/// the same lines the live watcher tails — so historical and live import converge on the
/// server's <c>AntigravityTranscriptNormalizer</c> (routed phase, like Gemini).
///
/// <para>The conversation id is the brain dir name and is used VERBATIM as the session id
/// (matching live capture, which uses the dashed conversationId), so a session captured
/// live and later re-imported dedupes to one stream. Subagents are separate conversations
/// linked via <c>messages/*.json</c> (see <see cref="AntigravitySubagents"/>); roots are
/// conversations that are never a child, and their children are imported as subagents under
/// them. Antigravity records no machine-readable workspace path in the transcript, so import
/// leaves <c>cwd</c> null (no repo enrichment / exclusion on historical import — a v1
/// limitation shared with Gemini; live capture gets cwd from the hook payload).</para>
///
/// <para>Token/model cost lives off-transcript in each conversation's <c>gen_metadata</c>
/// db; the live path streams it as synthetic USAGE lines. Injecting it on historical import
/// is a follow-up (it needs completeness tracking so a failed USAGE send after a complete
/// transcript still retries) — so imported sessions currently carry content but not cost.</para>
/// </summary>
internal sealed class AntigravityImportSource : IImportSource {
    static readonly HashSet<string> RelevantTypes = new(StringComparer.Ordinal) {
        "USER_INPUT", "PLANNER_RESPONSE", "RUN_COMMAND", "VIEW_FILE", "LIST_DIRECTORY", "CODE_ACTION"
    };

    readonly string? _home;
    readonly string? _geminiCliHome;

    public AntigravityImportSource(string? home = null, string? geminiCliHome = null) {
        _home          = home;
        _geminiCliHome = geminiCliHome;
    }

    string BrainRoot => Path.Combine(AntigravityPaths.Root(_home, _geminiCliHome), "brain");

    public string Vendor => "antigravity";
    public bool   IsAvailable => Directory.Exists(BrainRoot);
    public bool   SupportsTitleGeneration => false; // server computes a fallback title at session-end

    public Task<IReadOnlyList<DiscoveredSession>> DiscoverAsync(DiscoveryFilters filters, CancellationToken ct) {
        var result = new List<DiscoveredSession>();
        if (!Directory.Exists(BrainRoot)) return Task.FromResult<IReadOnlyList<DiscoveredSession>>(result);

        // Resolve every conversation to its top-level ancestor: roots (no parent) import as
        // sessions; ALL transitive descendants (children, grandchildren, …) import as their
        // root's subagents. Cycle-/non-tree-safe (BuildRootDescendants) so a deep chain isn't
        // lost and a cycle imports standalone rather than vanishing. Linkage is complete on disk.
        var parentMap = AntigravitySubagents.BuildParentMap(_home, _geminiCliHome);
        var convIds   = Directory.EnumerateDirectories(BrainRoot).Select(Path.GetFileName).OfType<string>().ToList();
        var byRoot    = AntigravitySubagents.BuildRootDescendants(convIds, parentMap);

        var sinceUtc = filters.Since is { } since
            ? new DateTimeOffset(since.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), TimeSpan.Zero)
            : (DateTimeOffset?)null;

        foreach (var (convId, descendants) in byRoot) {
            ct.ThrowIfCancellationRequested();

            if (filters.FilterSession is { } fs && !string.Equals(convId, fs, StringComparison.Ordinal)) continue;

            var transcript = AntigravityPaths.TranscriptFullPath(convId, _home, _geminiCliHome);
            if (!File.Exists(transcript)) continue;

            var firstTimestamp = ReadFirstTimestamp(transcript);
            if (firstTimestamp is null) {
                try { firstTimestamp = File.GetCreationTimeUtc(transcript); } catch { /* best effort */ }
            }
            if (sinceUtc is { } cutoff && firstTimestamp is { } ts && ts < cutoff) continue;

            result.Add(new DiscoveredSession(
                SessionId:      convId,
                Vendor:         Vendor,
                Cwd:            null,
                FirstTimestamp: firstTimestamp,
                SourceMeta:     new Dictionary<string, object?> {
                    ["TranscriptPath"] = transcript,
                    ["Children"]       = descendants, // all transitive descendants, nested under this root
                }));
        }

        return Task.FromResult<IReadOnlyList<DiscoveredSession>>(result);
    }

    public async Task<IReadOnlyList<ImportCommand.SessionClassification>> ClassifyAsync(
            IReadOnlyList<DiscoveredSession> sessions, ClassifyContext ctx, CancellationToken ct) {
        var results = new List<ImportCommand.SessionClassification>(sessions.Count);

        foreach (var s in sessions) {
            var transcriptPath = (string)s.SourceMeta!["TranscriptPath"]!;
            var meta = new SessionMetadata { SessionId = s.SessionId, Cwd = s.Cwd, FirstTimestamp = s.FirstTimestamp };

            int? lastNonBlank, lastRelevant; int nonBlank;
            try {
                (lastNonBlank, lastRelevant, nonBlank) = await ReadTranscriptStatsAsync(transcriptPath, ct);
            } catch {
                results.Add(Make(s, meta, ImportCommand.ClassificationStatus.ProbeError, 0, "transcript read failed"));
                continue;
            }

            if (lastNonBlank is null) {
                results.Add(Make(s, meta, ImportCommand.ClassificationStatus.ProbeError, 0, "empty transcript"));
                continue;
            }
            if (nonBlank < ctx.MinLines) {
                results.Add(Make(s, meta, ImportCommand.ClassificationStatus.TooShort, nonBlank));
                continue;
            }

            int? serverLastLine;
            try {
                serverLastLine = await FetchServerLastLineAsync(ctx.HttpClient, ctx.BaseUrl, s.SessionId, agentId: null, ct);
            } catch (OperationCanceledException) {
                throw;
            } catch {
                results.Add(Make(s, meta, ImportCommand.ClassificationStatus.ProbeError, nonBlank, "watermark probe failed"));
                continue;
            }

            meta.LastTimestamp = TryGetLastWriteUtc(transcriptPath);

            var status = ImportCommand.ClassificationStatus.New;
            var resume = 0;
            // Compare against the last IMPORT-RELEVANT line: Antigravity transcripts end with
            // normalizer-skipped steps (CHECKPOINT / GENERIC / SYSTEM_*), so a raw-tail compare
            // would re-classify a complete session Partial forever (mirrors Gemini).
            var lastImportable = lastRelevant ?? lastNonBlank.Value;
            if (serverLastLine is { } srv) {
                if (srv >= lastImportable) status = ImportCommand.ClassificationStatus.AlreadyLoaded;
                else { status = ImportCommand.ClassificationStatus.Partial; resume = srv + 1; }
            }

            results.Add(new ImportCommand.SessionClassification {
                SessionId      = s.SessionId,
                FilePath       = "", // routed phase
                EncodedCwd     = "",
                Meta           = meta,
                Status         = status,
                Vendor         = Vendor,
                ResumeFromLine = resume,
                TotalLines     = nonBlank,
                SourceMeta     = s.SourceMeta,
            });
        }

        return results;
    }

    public async Task<ImportOutcome> ImportSessionAsync(
            ImportCommand.SessionClassification c, ImportContext ctx, CancellationToken ct) {
        var transcriptPath = (string)c.SourceMeta!["TranscriptPath"]!;
        if (!File.Exists(transcriptPath)) return ImportOutcome.Failed;

        // An AlreadyLoaded root's parent transcript is fully imported, but a subagent may have
        // been skipped on a prior run (watermark probe / subagent-start / transcript POST
        // failure). Children classify independently by their own HWM, so still run a child-repair
        // pass — complete children are no-ops, incomplete ones resume — then report Skipped.
        // Without this, a once-skipped child would be lost forever (AI-1160 review).
        if (c.Status == ImportCommand.ClassificationStatus.AlreadyLoaded) {
            await ImportChildrenAsync(ctx.HttpClient, ctx.BaseUrl, c.SessionId, c.SourceMeta!, ct);
            return ImportOutcome.Skipped;
        }

        // Lifecycle-before-transcript (mirrors Gemini): a transcript that advances the
        // watermark past a failed lifecycle POST would leave the session lifecycle-less.
        // Re-runs are idempotent server-side (deterministic lifecycle ids).
        if (!await PostHookAsync(ctx.HttpClient, ctx.BaseUrl, "session-start/antigravity",
                BuildSessionStartPayload(c.SessionId, c.Meta.Cwd, c.Meta.FirstTimestamp, ctx.ForcePrivate), ct))
            return ImportOutcome.Failed;

        var startLine = c.Status == ImportCommand.ClassificationStatus.Partial ? c.ResumeFromLine : 0;

        int sent;
        try {
            sent = await SessionImporter.SendTranscriptBatches(
                httpClient: ctx.HttpClient, baseUrl: ctx.BaseUrl, sessionId: c.SessionId,
                filePath: transcriptPath, agentId: null, startLine: startLine, vendor: Vendor);
        } catch (OperationCanceledException) {
            throw;
        } catch {
            return ImportOutcome.Failed;
        }

        // Children as subagents — BEFORE session-end so SubagentCompleted precedes SessionEnded.
        // Subagent failures don't fail the (already-imported) parent; a re-import retries.
        await ImportChildrenAsync(ctx.HttpClient, ctx.BaseUrl, c.SessionId, c.SourceMeta!, ct);

        if (!await PostHookAsync(ctx.HttpClient, ctx.BaseUrl, "session-end/antigravity",
                BuildSessionEndPayload(c.SessionId, c.Meta.Cwd, c.Meta.LastTimestamp), ct))
            return ImportOutcome.Failed;

        if (sent == 0) return startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Skipped;
        return startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Loaded;
    }

    async Task ImportChildrenAsync(
            HttpClient client, string baseUrl, string rootId,
            IReadOnlyDictionary<string, object?> sourceMeta, CancellationToken ct) {
        if (!sourceMeta.TryGetValue("Children", out var kidsObj) || kidsObj is not List<string> { Count: > 0 } children)
            return;

        foreach (var childId in children) {
            ct.ThrowIfCancellationRequested();

            var childTranscript = AntigravityPaths.TranscriptFullPath(childId, _home, _geminiCliHome);
            if (!File.Exists(childTranscript)) continue;

            // Where the child's own (rootId, childId) stream is already ingested up to. agentId =
            // raw child conversation id (matches how live child events route).
            int? chwm;
            try {
                chwm = await FetchServerLastLineAsync(client, baseUrl, rootId, childId, ct);
            } catch (OperationCanceledException) {
                throw;
            } catch {
                continue; // watermark probe failed for this child — skip it, retry on re-import
            }

            // Compare the server HWM against the child's last IMPORT-RELEVANT line (same gate the
            // parent uses for AlreadyLoaded): trailing normalizer-skipped steps mustn't force an
            // endless re-send, and a fully-ingested child is a no-op we skip before touching hooks.
            int? childLastImportable;
            try {
                var (lastNonBlank, lastRelevant, _) = await ReadTranscriptStatsAsync(childTranscript, ct);
                childLastImportable = lastRelevant ?? lastNonBlank;
            } catch {
                continue; // unreadable child transcript — retry on re-import
            }
            if (childLastImportable is null) continue;                       // empty child

            if (chwm is { } done && done >= childLastImportable) {
                // Content is fully ingested, but subagent-stop is best-effort below and may have
                // failed AFTER the content POST on a prior run — leaving the subagent with no
                // completion event permanently (HWM only tracks content lines, not lifecycle).
                // Re-post the stop: it's idempotent server-side (deterministic SubagentCompleted
                // id), so a prior failure is repaired and an already-recorded stop dedupes.
                // subagent-start is not re-posted — content is fail-closed behind it, so its
                // presence is implied by the ingested content (AI-1160 review).
                await PostHookAsync(client, baseUrl, "subagent-stop",
                    BuildSubagentPayload("subagent_stop", rootId, childId, childTranscript), ct);

                continue;
            }

            // Resume by FILE POSITION (startLine), numbering surviving lines by their true position
            // (offset 0) — matching the parent's resume and live capture, so line numbers stay
            // stable across re-imports. (Content-hashed event ids dedupe already-sent lines, but a
            // shifted line number would corrupt $lineNumber + the watermark for genuinely-new lines.)
            var childStartLine = chwm is { } v ? checked(v + 1) : 0;

            // Fail-closed: no content unless the subagent registered first.
            if (!await PostHookAsync(client, baseUrl, "subagent-start",
                    BuildSubagentPayload("subagent_start", rootId, childId, childTranscript), ct))
                continue;

            try {
                await SessionImporter.SendTranscriptBatches(
                    httpClient: client, baseUrl: baseUrl, sessionId: rootId,
                    filePath: childTranscript, agentId: childId, startLine: childStartLine, vendor: Vendor);
            } catch (OperationCanceledException) {
                throw;
            } catch {
                continue; // leave subagent-stop unsent; a re-import retries (idempotent)
            }

            await PostHookAsync(client, baseUrl, "subagent-stop",
                BuildSubagentPayload("subagent_stop", rootId, childId, childTranscript), ct);
        }
    }

    // ── payload builders ────────────────────────────────────────────────────────

    static JsonObject BuildSessionStartPayload(string sid, string? cwd, DateTimeOffset? startedAt, bool forcePrivate) {
        var p = new JsonObject { ["hook_event_name"] = "sessionStart", ["session_id"] = sid };
        if (cwd is not null) p["cwd"] = cwd;
        if (startedAt is { } ts) p["started_at"] = ts.ToString("O");
        if (forcePrivate) p["default_visibility"] = "private";
        return p;
    }

    static JsonObject BuildSessionEndPayload(string sid, string? cwd, DateTimeOffset? endedAt) {
        var p = new JsonObject { ["hook_event_name"] = "sessionEnd", ["session_id"] = sid, ["reason"] = "antigravity-import" };
        if (cwd is not null) p["cwd"] = cwd;
        if (endedAt is { } ts) p["ended_at"] = ts.ToString("O");
        return p;
    }

    static JsonObject BuildSubagentPayload(string eventName, string parentSid, string agentId, string transcriptPath) =>
        new() {
            ["hook_event_name"] = eventName,
            ["session_id"]      = parentSid,
            ["agent_id"]        = agentId,
            ["agent_type"]      = "subagent",
            ["transcript_path"] = transcriptPath,
            ["cwd"]             = "",
        };

    static async Task<bool> PostHookAsync(HttpClient client, string baseUrl, string route, JsonObject payload, CancellationToken ct) {
        try {
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await client.PostWithRetryAsync($"{baseUrl}/hooks/{route}", content, ct: ct);
            return resp.IsSuccessStatusCode;
        } catch (OperationCanceledException) {
            throw;
        } catch {
            return false;
        }
    }

    // ── transcript reading ──────────────────────────────────────────────────────

    static DateTimeOffset? ReadFirstTimestamp(string path) {
        try {
            foreach (var line in File.ReadLines(path)) {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                 && doc.RootElement.TryGetProperty("created_at", out var ca)
                 && ca.GetString() is { } s
                 && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts))
                    return ts;
                return null; // first non-blank line had no created_at — don't scan the whole file
            }
        } catch { /* unreadable → null */ }
        return null;
    }

    static async Task<(int? LastNonBlank, int? LastRelevant, int NonBlank)> ReadTranscriptStatsAsync(
            string transcriptPath, CancellationToken ct) {
        await using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var       reader = new StreamReader(stream);

        int? lastIdx = null, lastRelevantIdx = null;
        var count = 0; var lineIdx = 0;

        while (await reader.ReadLineAsync(ct) is { } line) {
            if (!string.IsNullOrWhiteSpace(line)) {
                lastIdx = lineIdx;
                count++;
                if (IsImportRelevantLine(line)) lastRelevantIdx = lineIdx;
            }
            lineIdx++;
        }
        return (lastIdx, lastRelevantIdx, count);
    }

    /// <summary>
    /// True when a line maps to a canonical event under AntigravityTranscriptNormalizer —
    /// a conversational or tool-result step. Noise steps (SYSTEM_*, CHECKPOINT,
    /// CONVERSATION_HISTORY, GENERIC, INVOKE_SUBAGENT) are skipped server-side and can never
    /// advance the watermark; keep in sync with the normalizer.
    /// </summary>
    internal static bool IsImportRelevantLine(string line) {
        try {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("type", out var t)
                && t.GetString() is { } type
                && RelevantTypes.Contains(type);
        } catch {
            return false;
        }
    }

    static DateTimeOffset? TryGetLastWriteUtc(string path) {
        try { return File.GetLastWriteTimeUtc(path); } catch { return null; }
    }

    static async Task<int?> FetchServerLastLineAsync(
            HttpClient http, string baseUrl, string sessionId, string? agentId, CancellationToken ct) {
        var url = $"{baseUrl}/api/sessions/{sessionId}/last-line" + (agentId is not null ? $"?agentId={agentId}" : "");
        using var resp = await http.GetWithRetryAsync(url, ct: ct);
        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) return null;
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"watermark probe returned {(int)resp.StatusCode}");
        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("last_line_number", out var ln) && ln.ValueKind == JsonValueKind.Number
            ? ln.GetInt32() : null;
    }

    static ImportCommand.SessionClassification Make(
            DiscoveredSession s, SessionMetadata meta, ImportCommand.ClassificationStatus status,
            int totalLines, string? probeError = null) => new() {
        SessionId        = s.SessionId,
        FilePath         = "",
        EncodedCwd       = "",
        Meta             = meta,
        Status           = status,
        Vendor           = "antigravity",
        ProbeErrorReason = probeError,
        TotalLines       = totalLines,
        SourceMeta       = s.SourceMeta,
    };
}
