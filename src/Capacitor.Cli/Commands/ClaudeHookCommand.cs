using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Single-binary dispatcher for Claude Code hooks. Claude invokes one
/// CLI entry per session event; this method consolidates the seven
/// historical per-event subcommands (<c>session-start</c>, <c>stop</c>, …)
/// behind one entry point and routes off the <c>hook_event_name</c>
/// in the JSON payload — mirroring <see cref="CodexHookCommand"/> and
/// <see cref="CursorHookCommand"/>.
/// </summary>
public static class ClaudeHookCommand {
    // Hard ceiling on the best-effort pre-POST drain (watcher kill + inline transcript
    // drain) for session-end / subagent-stop. Claude kills the SessionEnd hook at its
    // configured timeout (15s); a slow or retrying remote call in the drain could consume
    // all of it (the HTTP retry helper alone allows up to 30s) and the session-end POST
    // would never be sent, leaving the session stuck "Active". 8s leaves ample headroom to
    // send the POST; the server's StopAndDrain + the "kcap import" hint recover the rest.
    static readonly TimeSpan PreHookDrainCap = TimeSpan.FromSeconds(8);

    public static Task<int> Handle(string baseUrl, TextReader stdin, Task? updateCheckTask = null, long processStart = 0) {
        var spool = new HookSpool(PathHelpers.ConfigPath("spool"));
        spool.ReapOlderThan(TimeSpan.FromDays(30));
        var ps = processStart == 0 ? Stopwatch.GetTimestamp() : processStart;
        return HandleWithDeps(spool, ps, baseUrl, stdin, updateCheckTask);
    }

    static async Task<int> HandleWithDeps(HookSpool spool, long processStart, string baseUrl, TextReader stdin, Task? updateCheckTask) {
        HttpClient? client = null;
        try {
            client = await HttpClientExtensions.CreateAuthenticatedClientAsync();
            return await HandleCore(client, spool, processStart, baseUrl, stdin, updateCheckTask);
        } catch {
            return 0; // fail-open
        } finally { client?.Dispose(); }
    }

    internal static async Task<int> WithHardCap(Task<int> inner, TimeSpan budget) {
        var winner = await Task.WhenAny(inner, Task.Delay(budget));
        return winner == inner ? await inner : 0;
    }

    /// <summary>
    /// Single bounded attempt. ok=true on 2xx. permanent=true on a 4xx that is not
    /// 408/429 (do not spool). Any exception/timeout → (false, false) → spool as transient.
    /// </summary>
    internal static async Task<(bool ok, bool permanent)> PostLifecycleBoundedAsync(
            HttpClient client, string url, string body, TimeSpan remaining, CancellationToken ct) {
        if (remaining <= TimeSpan.Zero) return (false, false);
        try {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp    = await client.PostOnceAsync(url, content, remaining, ct);
            if (resp.IsSuccessStatusCode) return (true, false);
            var code      = (int)resp.StatusCode;
            var transient = code is >= 500 or 408 or 429;
            return (false, !transient);
        } catch {
            return (false, false); // unreachable / hung / timeout — transient
        }
    }

    internal static async Task<int> HandleCore(HttpClient client, HookSpool spool, long processStart, string baseUrl, TextReader stdin, Task? updateCheckTask = null) {
        var body = await stdin.ReadToEndAsync();

        var eventName = ExtractEventName(body);

        if (eventName is null) {
            Console.Error.WriteLine("kcap hook --claude: missing hook_event_name in payload");
            return 1;
        }

        // Normalize Claude's PascalCase event names to the kebab-case server
        // route convention (`SessionStart` → `session-start`).
        var command = ToKebab(eventName);

        // Inject home_dir and agent_host_id into all hook payloads, and normalize IDs.
        try {
            var node = JsonNode.Parse(body);

            if (node is not null) {
                NormalizeGuidField(node, "session_id");
                NormalizeGuidField(node, "agent_id");

                node["home_dir"] = PathHelpers.HomeDirectory;

                var agentHostId = Environment.GetEnvironmentVariable("KCAP_AGENT_ID");

                if (agentHostId is not null) {
                    node["agent_host_id"] = agentHostId;
                }

                body = node.ToJsonString();
            }
        } catch {
            // Best effort — don't fail the hook if JSON parsing fails.
        }

        // Check if session is disabled — skip all server communication.
        try {
            var disabledSessionId = JsonNode.Parse(body)?["session_id"]?.GetValue<string>();

            if (disabledSessionId is not null && DisabledSessions.IsDisabled(disabledSessionId)) {
                if (command == "session-end") {
                    DisabledSessions.RemoveMarker(disabledSessionId);
                }

                return 0;
            }
        } catch {
            // Best effort — don't fail if JSON parsing fails.
        }

        // PermissionRequest has its own handler path (daemon bridge / fire-and-forget).
        if (command == "permission-request") {
            return await PermissionRequestCommand.Handle(baseUrl, body);
        }

        // On session-start, clear the last-emitted repo cache so this session always gets a
        // RepositoryDetected event (the dedup cache is per-cwd, but each session needs its own link).
        if (command == "session-start") {
            try {
                var cwdNode = JsonNode.Parse(body)?["cwd"]?.GetValue<string>();

                if (cwdNode is not null) {
                    RepositoryDetection.ClearLastEmitted(cwdNode);
                }
            } catch {
                // Best effort
            }
        }

        // Enrich hook payloads with repository info.
        // For session-end and subagent-stop, defer enrichment to run in parallel with watcher kill.
        // For session-start, pass a time budget so enrichment self-skips under deadline pressure
        // (repo info still arrives via the watcher's own detection).
        Task<string>? deferredRepoTask = null;

        if (command is "session-end" or "subagent-stop") {
            deferredRepoTask = RepositoryDetection.EnrichWithRepositoryInfo(body);
        } else if (command == "session-start") {
            body = await RepositoryDetection.EnrichWithRepositoryInfo(body, HookBudget.Remaining(processStart, command));
        } else {
            body = await RepositoryDetection.EnrichWithRepositoryInfo(body);
        }

        // Resolve the V2 profile once for repo/path exclusion and
        // default_visibility injection. Reading these off the legacy top-level
        // CapacitorConfig silently misses v2 settings (the fields live under
        // the active profile), so per-profile `excluded_repos` / `private`
        // visibility were being ignored.
        var activeProfile = await AppConfig.GetActiveProfileAsync();

        // Check repo exclusion — silently exit for excluded repos.
        if (activeProfile?.ExcludedRepos is { Length: > 0 } repos && await RepoExclusion.IsExcludedAsync(body, repos)) {
            return 0;
        }

        // Check path exclusion against the V2 profile that applies to this process.
        if (activeProfile?.ExcludedPaths is { Length: > 0 } paths) {
            try {
                var cwd = JsonNode.Parse(body)?["cwd"]?.GetValue<string>();

                if (PathExclusion.IsExcluded(cwd, paths)) return 0;
            } catch {
                // Best effort
            }
        }

        // Inject default_visibility from the active V2 profile for session-start
        // hooks. The legacy top-level CapacitorConfig.DefaultVisibility shape is
        // not populated by v2 configs (the field lives under the profile), so
        // reading it there silently fell back to "org_public" and ignored
        // per-profile `private` settings.
        if (command == "session-start" && activeProfile?.DefaultVisibility is { } vis) {
            try {
                var node = JsonNode.Parse(body);

                if (node is not null) {
                    node["default_visibility"] = vis;
                    body                       = node.ToJsonString();
                }
            } catch {
                // Best effort
            }
        }

        // For session-start: read plan file if slug is known and inject plan_content into payload.
        var planContentInjected = false;

        if (command == "session-start") {
            try {
                var node = JsonNode.Parse(body);
                var slug = node?["slug"]?.GetValue<string>();

                if (slug is not null) {
                    var planContent = ReadPlanFile(slug);

                    if (planContent is not null) {
                        node!["plan_content"] = planContent;
                        body                  = node.ToJsonString();
                        planContentInjected   = true;
                    }
                }
            } catch {
                // Best effort
            }
        }

        // For session-end and subagent-stop: kill watcher BEFORE posting hook
        // so transcript is fully drained before server computes stats.
        switch (command) {
            case "session-end": {
                try {
                    var node           = JsonNode.Parse(body);
                    var sessionId      = node?["session_id"]?.GetValue<string>();
                    var transcriptPath = node?["transcript_path"]?.GetValue<string>();

                    if (sessionId is not null) {
                        // Clamp the pre-drain cap so it cannot consume the entire remaining budget
                        // that the bounded POST needs. Use whichever is smaller.
                        var remaining     = HookBudget.Remaining(processStart, "session-end");
                        var effectiveCap  = TimeSpan.FromMilliseconds(
                            Math.Min(PreHookDrainCap.TotalMilliseconds, remaining.TotalMilliseconds));

                        var drained = await TimeBudget.RunCappedAsync(
                            async () => {
                                await WatcherManager.KillWatcher(sessionId);

                                if (transcriptPath is not null) {
                                    await WatcherManager.InlineDrainAsync(baseUrl, sessionId, transcriptPath, agentId: null);
                                }
                            },
                            effectiveCap
                        );

                        if (!drained) {
                            await Console.Error.WriteLineAsync(
                                $"[kcap] session-end pre-drain cap ({effectiveCap.TotalSeconds:0.#}s) elapsed; proceeding to POST. "
                              + $"Transcript tail may be incomplete — recoverable via: kcap import --session {sessionId}"
                            );
                        }
                    }
                } catch (Exception ex) {
                    Console.Error.WriteLine($"[kcap] session-end pre-hook failed: {ex.Message}");
                }

                body = await deferredRepoTask!;

                break;
            }
            case "subagent-stop": {
                try {
                    var node           = JsonNode.Parse(body);
                    var sessionId      = node?["session_id"]?.GetValue<string>();
                    var agentId        = node?["agent_id"]?.GetValue<string>();
                    var transcriptPath = node?["transcript_path"]?.GetValue<string>();

                    if (sessionId is not null && agentId is not null) {
                        var drained = await TimeBudget.RunCappedAsync(
                            async () => {
                                await WatcherManager.KillWatcher($"{sessionId}-{agentId}");

                                if (transcriptPath is not null) {
                                    var sessionDir          = Path.ChangeExtension(transcriptPath, null);
                                    var agentTranscriptPath = Path.Combine(sessionDir, "subagents", $"agent-{agentId}.jsonl");
                                    await WatcherManager.InlineDrainAsync(baseUrl, sessionId, agentTranscriptPath, agentId);
                                }
                            },
                            PreHookDrainCap
                        );

                        if (!drained) {
                            await Console.Error.WriteLineAsync(
                                $"[kcap] subagent-stop pre-drain cap ({PreHookDrainCap.TotalSeconds:0}s) elapsed; proceeding to POST"
                            );
                        }
                    }
                } catch (Exception ex) {
                    Console.Error.WriteLine($"[kcap] subagent-stop pre-hook failed: {ex.Message}");
                }

                body = await deferredRepoTask!;

                break;
            }
        }

        // Dedicated bounded path for session-start: spawn the watcher FIRST (transcript capture
        // must never be lost even if the POST fails), then bounded POST, spool on transient
        // failure, and emit the context envelope + plan-content POST on success.
        if (command == "session-start") {
            var startNode      = JsonNode.Parse(body);
            var sessionId      = startNode?["session_id"]?.GetValue<string>();
            var transcriptPath = startNode?["transcript_path"]?.GetValue<string>();
            var sessionCwd     = startNode?["cwd"]?.GetValue<string>();
            var source         = startNode?["source"]?.GetValue<string>();
            var isResumeOrCompact = source is not null &&
                (source.Equals("resume", StringComparison.OrdinalIgnoreCase) ||
                 source.Equals("compact", StringComparison.OrdinalIgnoreCase));

            // 1. Capture never lost: spawn the watcher before any slow git/gh/POST.
            //    Idempotent — safe to call even if the POST subsequently fails.
            if (sessionId is not null && transcriptPath is not null) {
                await WatcherManager.EnsureWatcherRunning(
                    baseUrl, sessionId, transcriptPath,
                    agentId: null, cwd: sessionCwd, skipTitle: isResumeOrCompact);
            }

            // 2. Bounded POST; spool on transient failure (exit-0: session-start is async in plugin).
            var remaining = HookBudget.Remaining(processStart, "session-start");
            var (ok, permanent) = await PostLifecycleBoundedAsync(
                client, $"{baseUrl}/hooks/session-start", body, remaining, CancellationToken.None);

            if (!ok) {
                if (!permanent && sessionId is not null) {
                    spool.Append(sessionId, "session-start", body);
                }
                return 0;
            }

            // 3. Success: re-issue a single bounded POST to read the response body for the
            //    context-envelope emission and plan-content POST. PostLifecycleBoundedAsync
            //    disposes its response, so we need this second call for the response payload.
            //    The injected-client test seam observes this POST.
            remaining = HookBudget.Remaining(processStart, "session-start");
            JsonNode? responseNode = null;
            if (remaining > TimeSpan.Zero) {
                try {
                    using var responseContent = new StringContent(body, Encoding.UTF8, "application/json");
                    using var resp = await client.PostOnceAsync(
                        $"{baseUrl}/hooks/session-start", responseContent, remaining, CancellationToken.None);

                    if (resp.IsSuccessStatusCode) {
                        var responseBody = await resp.Content.ReadAsStringAsync();
                        responseNode = JsonNode.Parse(responseBody);
                    }
                } catch {
                    // Best effort — envelope is optional; don't fail the hook.
                }
            }

            // Plan-content POST from response-resolved slug (only if not already injected).
            if (responseNode is not null && !planContentInjected && sessionId is not null) {
                try {
                    var resolvedSlug = responseNode["slug"]?.GetValue<string>();

                    if (resolvedSlug is not null) {
                        var planContent = ReadPlanFile(resolvedSlug);

                        if (planContent is not null) {
                            await PostPlanContentAsync(client, baseUrl, sessionId, planContent);
                        }
                    }
                } catch {
                    // Best effort
                }
            }

            // Context-envelope emission (lessons/version-nudge).
            if (responseNode is not null) {
                try {
                    var disabled        = AppConfig.ResolvedProfile?.Profile?.DisableSessionGuidelines is true;
                    var lessonsFragment = SessionGuidelinesEmitter.BuildFragment(responseNode, disabled);
                    var nudgeFragment   = VersionNudgeEmitter.BuildFragment(responseNode, CapacitorVersion.CurrentDisplay());

                    var envelope = SessionStartAdditionalContext.BuildEnvelope(lessonsFragment, nudgeFragment);

                    if (envelope is not null) {
                        Console.WriteLine(envelope);
                    }
                } catch {
                    // Best effort — never break session capture for hook output emission.
                }
            }

            if (updateCheckTask is not null) {
                await updateCheckTask;
            }

            return 0;
        }

        // Dedicated bounded POST for session-end: single attempt clamped to the remaining
        // hook budget, spools on transient failure, and checks generate_whats_done on success.
        // Other commands continue through the shared PostWithRetryAsync path below.
        if (command == "session-end") {
            // Parse once: stamp ended_at and extract sessionId in a single pass.
            string? sessionId = null;
            try {
                var node = JsonNode.Parse(body);
                sessionId = node?["session_id"]?.GetValue<string>();
                if (node is not null) {
                    node["ended_at"] = DateTimeOffset.UtcNow.ToString("O");
                    body             = node.ToJsonString();
                }
            } catch { }

            var remaining  = HookBudget.Remaining(processStart, "session-end");
            HttpResponseMessage? resp = null;
            try {
                if (remaining > TimeSpan.Zero) {
                    using var content = new StringContent(body, Encoding.UTF8, "application/json");
                    resp = await client.PostOnceAsync($"{baseUrl}/hooks/session-end", content, remaining, CancellationToken.None);
                }
            } catch { resp = null; }

            if (resp is null || !resp.IsSuccessStatusCode) {
                var permanent = resp is not null && (int)resp.StatusCode is < 500 and not 408 and not 429;
                resp?.Dispose();
                if (!permanent) {
                    if (sessionId is not null) {
                        spool.Append(sessionId, "session-end", body);
                        await Console.Error.WriteLineAsync($"[kcap] session-end spooled; will retry on the next kcap hook ({sessionId})");
                    } else {
                        await Console.Error.WriteLineAsync("[kcap] session-end transient failure but session_id missing — cannot spool; event dropped");
                    }
                }
                return 0;
            }

            try {
                var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
                if (node?["generate_whats_done"]?.GetValue<bool>() == true && sessionId is not null)
                    WatcherManager.SpawnWhatsDoneGenerator(baseUrl, sessionId);
            } catch { }
            resp.Dispose();
            return 0;
        }

        using var sharedContent = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage response;

        try {
            response = await client.PostWithRetryAsync($"{baseUrl}/hooks/{command}", sharedContent);
        } catch (HttpRequestException ex) {
            HttpClientExtensions.WriteUnreachableError(baseUrl, ex);

            return 1;
        }

        if (!response.IsSuccessStatusCode) {
            Console.Error.WriteLine($"HTTP {(int)response.StatusCode}");

            return 1;
        }

        switch (command) {
            case "subagent-start": {
                var node           = JsonNode.Parse(body);
                var sessionId      = node?["session_id"]?.GetValue<string>();
                var agentId        = node?["agent_id"]?.GetValue<string>();
                var transcriptPath = node?["transcript_path"]?.GetValue<string>();

                if (sessionId is not null && agentId is not null && transcriptPath is not null) {
                    var sessionDir          = Path.ChangeExtension(transcriptPath, null);
                    var agentTranscriptPath = Path.Combine(sessionDir, "subagents", $"agent-{agentId}.jsonl");
                    await WatcherManager.EnsureWatcherRunning(baseUrl, $"{sessionId}-{agentId}", agentTranscriptPath, agentId, sessionId);
                }

                break;
            }
            case "notification" or "stop": {
                var node           = JsonNode.Parse(body);
                var sessionId      = node?["session_id"]?.GetValue<string>();
                var transcriptPath = node?["transcript_path"]?.GetValue<string>();
                var sessionCwd     = node?["cwd"]?.GetValue<string>();

                if (sessionId is not null && transcriptPath is not null) {
                    await WatcherManager.EnsureWatcherRunning(baseUrl, sessionId, transcriptPath, agentId: null, cwd: sessionCwd);
                }

                break;
            }
        }

        if (updateCheckTask is not null) {
            await updateCheckTask;
        }

        return 0;
    }

    /// <summary>
    /// Extracts <c>hook_event_name</c> from a hook payload. Returns null if
    /// the payload is malformed or the field is missing. Best-effort —
    /// errors are swallowed so a malformed payload doesn't crash the CLI.
    /// </summary>
    static string? ExtractEventName(string body) {
        try {
            return JsonNode.Parse(body)?["hook_event_name"]?.GetValue<string>();
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Normalizes a hook event name to kebab-case: <c>SessionStart</c>,
    /// <c>session_start</c>, and <c>session-start</c> all return
    /// <c>session-start</c>.
    /// </summary>
    static string ToKebab(string s) {
        if (string.IsNullOrEmpty(s)) return s;

        var sb = new StringBuilder(s.Length + 4);

        for (var i = 0; i < s.Length; i++) {
            var c = s[i];

            if (c == '_') {
                sb.Append('-');
            } else if (char.IsUpper(c)) {
                if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
                sb.Append(char.ToLowerInvariant(c));
            } else {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    static void NormalizeGuidField(JsonNode node, string fieldName) {
        var value = node[fieldName]?.GetValue<string>();

        if (value is not null && value.Contains('-')) {
            node[fieldName] = value.Replace("-", "");
        }
    }

    static string? ReadPlanFile(string slug) {
        var planPath = Path.Combine(ClaudePaths.Plans, $"{slug}.md");

        try {
            return File.Exists(planPath) ? File.ReadAllText(planPath) : null;
        } catch (Exception ex) {
            Console.Error.WriteLine($"[kcap] Failed to read plan file at {planPath}: {ex.Message}");

            return null;
        }
    }

    static async Task PostPlanContentAsync(HttpClient httpClient, string url, string sessionId, string planContent) {
        var       obj         = new JsonObject { ["plan_content"] = planContent };
        using var planPayload = new StringContent(obj.ToJsonString(), Encoding.UTF8, "application/json");
        await httpClient.PostWithRetryAsync($"{url}/api/sessions/{sessionId}/plan", planPayload);
    }
}
