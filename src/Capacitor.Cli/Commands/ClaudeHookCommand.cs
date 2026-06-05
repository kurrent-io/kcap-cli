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
    public static async Task<int> Handle(string baseUrl, TextReader stdin, Task? updateCheckTask = null) {
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
        Task<string>? deferredRepoTask = null;

        if (command is "session-end" or "subagent-stop") {
            deferredRepoTask = RepositoryDetection.EnrichWithRepositoryInfo(body);
        } else {
            body = await RepositoryDetection.EnrichWithRepositoryInfo(body);
        }

        // Load config once for exclusion check and default_visibility injection.
        var kcapConfig = await AppConfig.Load();

        // Check repo exclusion — silently exit for excluded repos.
        if (kcapConfig?.ExcludedRepos is { Length: > 0 } repos && await RepoExclusion.IsExcludedAsync(body, repos)) {
            return 0;
        }

        // Check path exclusion against the V2 profile that applies to this process.
        if ((await AppConfig.GetActiveProfileAsync())?.ExcludedPaths is { Length: > 0 } paths) {
            try {
                var cwd = JsonNode.Parse(body)?["cwd"]?.GetValue<string>();

                if (PathExclusion.IsExcluded(cwd, paths)) return 0;
            } catch {
                // Best effort
            }
        }

        // Inject default_visibility from config for session-start hooks.
        if (command == "session-start" && kcapConfig?.DefaultVisibility is { } vis) {
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
                        await WatcherManager.KillWatcher(sessionId);

                        if (transcriptPath is not null) {
                            await WatcherManager.InlineDrainAsync(baseUrl, sessionId, transcriptPath, agentId: null);
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
                        await WatcherManager.KillWatcher($"{sessionId}-{agentId}");

                        if (transcriptPath is not null) {
                            var sessionDir          = Path.ChangeExtension(transcriptPath, null);
                            var agentTranscriptPath = Path.Combine(sessionDir, "subagents", $"agent-{agentId}.jsonl");
                            await WatcherManager.InlineDrainAsync(baseUrl, sessionId, agentTranscriptPath, agentId);
                        }
                    }
                } catch (Exception ex) {
                    Console.Error.WriteLine($"[kcap] subagent-stop pre-hook failed: {ex.Message}");
                }

                body = await deferredRepoTask!;

                break;
            }
        }

        using var client  = await HttpClientExtensions.CreateAuthenticatedClientAsync();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage response;

        try {
            response = await client.PostWithRetryAsync($"{baseUrl}/hooks/{command}", content);
        } catch (HttpRequestException ex) {
            HttpClientExtensions.WriteUnreachableError(baseUrl, ex);

            return 1;
        }

        if (!response.IsSuccessStatusCode) {
            Console.Error.WriteLine($"HTTP {(int)response.StatusCode}");

            return 1;
        }

        // Check session-end response for generate_whats_done flag.
        if (command == "session-end") {
            try {
                var responseBody = await response.Content.ReadAsStringAsync();
                var responseNode = JsonNode.Parse(responseBody);

                if (responseNode?["generate_whats_done"]?.GetValue<bool>() == true) {
                    var node      = JsonNode.Parse(body);
                    var sessionId = node?["session_id"]?.GetValue<string>();

                    if (sessionId is not null) {
                        WatcherManager.SpawnWhatsDoneGenerator(baseUrl, sessionId);
                    }
                }
            } catch {
                // Best effort
            }
        }

        switch (command) {
            case "session-start": {
                var node           = JsonNode.Parse(body);
                var sessionId      = node?["session_id"]?.GetValue<string>();
                var transcriptPath = node?["transcript_path"]?.GetValue<string>();
                var sessionCwd     = node?["cwd"]?.GetValue<string>();

                JsonNode? responseNode = null;
                try {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    responseNode     = JsonNode.Parse(responseBody);
                } catch {
                    // Best effort
                }

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

                if (responseNode is not null) {
                    try {
                        var disabled = AppConfig.ResolvedProfile?.Profile?.DisableSessionGuidelines is true;
                        var emission = SessionGuidelinesEmitter.BuildAdditionalContext(responseNode, disabled);

                        if (emission is not null) {
                            Console.WriteLine(emission);
                        }
                    } catch {
                        // Best effort
                    }
                }

                var source = node?["source"]?.GetValue<string>();

                var isResumeOrCompact = source is not null
                 && (source.Equals("resume", StringComparison.OrdinalIgnoreCase)
                     || source.Equals("compact", StringComparison.OrdinalIgnoreCase));

                if (sessionId is not null && transcriptPath is not null) {
                    await WatcherManager.EnsureWatcherRunning(baseUrl, sessionId, transcriptPath, agentId: null, cwd: sessionCwd, skipTitle: isResumeOrCompact);
                }

                break;
            }
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
