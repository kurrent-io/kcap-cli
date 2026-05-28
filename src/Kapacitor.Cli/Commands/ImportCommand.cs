using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Kapacitor.Cli.Core;
using Kapacitor.Cli.Core.Config;
using Spectre.Console;

namespace Kapacitor.Cli.Commands;

static class ImportCommand {
    /// <summary>
    /// Maximum parallel worker count for the Importing phase. Both the
    /// channel-based dispatcher in ImportChainsAsync and the TTY slot-row
    /// renderer in HandleImport size themselves to this value, so they
    /// MUST stay in lockstep.
    /// </summary>
    const int ImportWorkerCount = 4;

    readonly struct ImportDisplay {
        public bool Tty { get; init; }

        public void Line(string plain, string? markup = null) {
            if (Tty) AnsiConsole.MarkupLine(markup ?? Markup.Escape(plain));
            else Console.WriteLine(plain);
        }

        public void BeginPhase(string title) {
            if (Tty) {
                AnsiConsole.Write(new Rule($"[yellow]{Markup.Escape(title)}[/]").LeftJustified());
            } else {
                Console.WriteLine();
                Console.WriteLine($"== {title} ==");
            }
        }

        public void WritePlanGrid(ClassificationCounts c) {
            if (Tty) {
                var grid = new Grid().AddColumn().AddColumn();
                grid.AddRow("[bold]New[/]", c.New.ToString());
                grid.AddRow("[bold]Resumable[/]", c.Partial.ToString());
                grid.AddRow("[bold]Already loaded[/]", c.AlreadyLoaded.ToString());
                grid.AddRow("[bold]Too short[/]", c.TooShort.ToString());
                grid.AddRow("[bold]Excluded[/]", c.Excluded.ToString());
                if (c.ProbeError > 0) grid.AddRow("[bold]Probe errors[/]", $"[red]{c.ProbeError}[/]");
                AnsiConsole.Write(grid);
            } else {
                Console.WriteLine($"  New               {c.New}");
                Console.WriteLine($"  Resumable         {c.Partial}");
                Console.WriteLine($"  Already loaded    {c.AlreadyLoaded}");
                Console.WriteLine($"  Too short         {c.TooShort}");
                Console.WriteLine($"  Excluded          {c.Excluded}");
                if (c.ProbeError > 0) Console.WriteLine($"  Probe errors      {c.ProbeError}");
            }
        }

        public void WriteDoneGrid(FinalCounts f) {
            if (Tty) {
                var grid = new Grid().AddColumn().AddColumn();
                grid.AddRow("[bold]Loaded[/]", f.Loaded.ToString());
                grid.AddRow("[bold]Resumed[/]", f.Resumed.ToString());
                grid.AddRow("[bold]Already loaded[/]", f.AlreadyLoaded.ToString());
                if (f.TooShort   > 0) grid.AddRow("[bold]Too short[/]", f.TooShort.ToString());
                if (f.Excluded   > 0) grid.AddRow("[bold]Excluded[/]", f.Excluded.ToString());
                if (f.ProbeError > 0) grid.AddRow("[bold]Probe errors[/]", $"[red]{f.ProbeError}[/]");
                if (f.Errored    > 0) grid.AddRow("[bold]Errored[/]", $"[red]{f.Errored}[/]");

                if (f.RanBackground) {
                    grid.AddRow("[bold]Titles[/]", $"{f.TitlesGenerated} generated, {f.TitlesSkipped} skipped, {f.TitlesFailed} failed");

                    if (f.RequestedSummaries)
                        grid.AddRow("[bold]Summaries[/]", $"{f.SummariesGenerated} generated, {f.SummariesFailed} failed");
                }

                AnsiConsole.Write(new Rule("[green]Done[/]").LeftJustified());
                AnsiConsole.Write(grid);
            } else {
                Console.WriteLine();
                Console.WriteLine("== Done ==");
                Console.WriteLine($"  Loaded              {f.Loaded}");
                Console.WriteLine($"  Resumed             {f.Resumed}");
                Console.WriteLine($"  Already loaded      {f.AlreadyLoaded}");
                if (f.TooShort   > 0) Console.WriteLine($"  Too short           {f.TooShort}");
                if (f.Excluded   > 0) Console.WriteLine($"  Excluded            {f.Excluded}");
                if (f.ProbeError > 0) Console.WriteLine($"  Probe errors        {f.ProbeError}");
                if (f.Errored    > 0) Console.WriteLine($"  Errored             {f.Errored}");

                if (f.RanBackground) {
                    Console.WriteLine($"  Titles              {f.TitlesGenerated} generated, {f.TitlesSkipped} skipped, {f.TitlesFailed} failed");

                    if (f.RequestedSummaries)
                        Console.WriteLine($"  Summaries           {f.SummariesGenerated} generated, {f.SummariesFailed} failed");
                }
            }
        }

        public static ImportDisplay Create() => new() { Tty = !Console.IsOutputRedirected };
    }

    internal enum ClassificationStatus {
        /// <summary>Session does not exist on the server — needs a full import.</summary>
        New,

        /// <summary>Session exists on the server with a partial line count — resume from ResumeFromLine.</summary>
        Partial,

        /// <summary>Session is fully loaded on the server — no work to do.</summary>
        AlreadyLoaded,

        /// <summary>Transcript line count is below the minLines threshold.</summary>
        TooShort,

        /// <summary>Session's repository is in the user's excluded list and the user declined to include it.</summary>
        Excluded,

        /// <summary>The last-line probe failed (HTTP error, network error).</summary>
        ProbeError,

        /// <summary>Kapacitor-spawned sub-session (title generation, what's-done summary) — never imported.</summary>
        InternalSubSession,
    }

    internal sealed record SessionClassification {
        public required string               SessionId  { get; init; }
        public required string               FilePath   { get; init; }
        public required string               EncodedCwd { get; init; }
        public required SessionMetadata      Meta       { get; init; }
        public required ClassificationStatus Status     { get; init; }

        /// <summary>"claude" (default) or "codex" — picks the matching metadata extractor,
        /// title extractor, session-start hook shape, and TranscriptBatch.Vendor tag.</summary>
        public string Vendor { get; init; } = "claude";

        /// <summary>Only populated when Status == Partial.</summary>
        public int ResumeFromLine { get; init; }

        /// <summary>Only populated when Status == ProbeError. Short human-readable reason.</summary>
        public string? ProbeErrorReason { get; init; }

        /// <summary>Populated when the session is a continuation in the continuation map.</summary>
        public string? PreviousSessionId { get; init; }

        /// <summary>
        /// Populated when Status == Excluded OR when the session would otherwise be New/Partial
        /// but its cwd maps to an excluded repo and the user has not yet been consulted.
        /// Format: "{Owner}/{RepoName}".
        /// </summary>
        public string? ExcludedRepoKey { get; init; }

        /// <summary>
        /// Populated when the session's cwd lies within a path configured via
        /// <c>kapacitor ignore</c>. Holds the matching excluded-path entry
        /// (normalized) so prompts can group sessions by the entry that excluded
        /// them.
        /// </summary>
        public string? ExcludedPathKey { get; init; }

        /// <summary>Total transcript line count (cached so we don't re-read the file downstream).</summary>
        public int TotalLines { get; init; }

        /// <summary>
        /// Source-specific opaque metadata attached during DiscoverAsync.
        /// Claude/Codex sources don't need this (their fields live in FilePath/EncodedCwd).
        /// Cursor uses it to carry ComposerId, WorkspacePath, GlobalDbPath, CliOwner, CliRepo.
        /// The orchestrator does not inspect this dictionary; only the originating
        /// IImportSource reads it back in ImportSessionAsync.
        /// </summary>
        public IReadOnlyDictionary<string, object?>? SourceMeta { get; init; }
    }

    internal sealed record ClassificationCounts(
            int New,
            int Partial,
            int AlreadyLoaded,
            int TooShort,
            int Excluded,
            int ProbeError
        );

    internal sealed record FinalCounts(
            int  Loaded,
            int  Resumed,
            int  AlreadyLoaded,
            int  TooShort,
            int  Excluded,
            int  ProbeError,
            int  Errored,
            int  TitlesGenerated,
            int  TitlesSkipped,
            int  TitlesFailed,
            int  SummariesGenerated,
            int  SummariesFailed,
            bool RanBackground,
            bool RequestedSummaries
        );

    public static async Task<int> HandleImport(
            string       baseUrl,
            string?      filterCwd,
            string?      filterSession     = null,
            int          minLines          = 15,
            bool         generateSummaries = false,
            bool         codex             = false,
            DateOnly?    since             = null,
            ImportScope? scope             = null,
            bool         skipConfirmation  = false,
            bool         forcePrivate      = false,
            string       activeProfile     = "default",
            (string Owner, string Name)? currentRepo = null
        ) {
        using var httpClient = await HttpClientExtensions.CreateAuthenticatedClientAsync();
        var       display    = ImportDisplay.Create();
        var       vendor     = codex ? "codex" : "claude";

        // --- Discover ---
        display.BeginPhase("Discovering");
        List<(string SessionId, string FilePath, string EncodedCwd)> transcriptFiles;

        if (codex) {
            if (!Directory.Exists(CodexPaths.Sessions)) {
                display.Line("No Codex sessions directory found.");

                return 0;
            }

            transcriptFiles = CodexPaths.Discover(since: since);
        } else {
            var projectsDir = ClaudePaths.Projects;

            if (!Directory.Exists(projectsDir)) {
                display.Line("No Claude Code projects directory found.");

                return 0;
            }

            transcriptFiles = DiscoverTranscripts(projectsDir);
        }

        if (transcriptFiles.Count == 0) {
            display.Line("No transcript files found.");

            return 0;
        }

        if (filterSession is not null) {
            var normalized = NormalizeGuid(filterSession);
            transcriptFiles = [.. transcriptFiles.Where(t => t.SessionId == normalized)];

            if (transcriptFiles.Count == 0) {
                await Console.Error.WriteLineAsync($"Session not found: {normalized}");

                return 1;
            }
        }

        if (filterCwd is not null) {
            var normalizedFilter = filterCwd.TrimEnd('/');

            transcriptFiles = [
                .. transcriptFiles.Where(t => {
                        var cwd = ExtractCwdFromTranscript(t.FilePath, codex);

                        return cwd?.TrimEnd('/').Equals(normalizedFilter, StringComparison.Ordinal) == true;
                    }
                )
            ];
        }

        var projectCount = transcriptFiles.Select(t => t.EncodedCwd).Distinct().Count();
        display.Line($"Found {transcriptFiles.Count} {vendor} session{(transcriptFiles.Count == 1 ? "" : "s")} in {projectCount} project{(projectCount == 1 ? "" : "s")}");

        var kapacitorConfig = await AppConfig.Load();

        // --- Scope: pre-detect repos for the filter and (if needed) the picker ---
        // Resolve all transcript → repo mappings up front, in parallel and deduped by
        // cwd. With many sessions sharing a cwd, sequential per-session detection
        // (git config + `gh pr view`) could take minutes; that silent gap made
        // `kapacitor import` look frozen (see AI-692).
        var resolved = await ResolveTranscriptReposAsync(transcriptFiles, codex, display);

        if (scope is null) {
            var distinct = resolved.Values
                .Where(v => v is not null)
                .Select(v => v!.Value)
                .ToList();

            scope = ImportScopePrompt.RunPicker(activeProfile, currentRepo, distinct);
            if (scope is null) {
                // RunPicker has already printed the specific reason (e.g. "Active profile
                // has no org" or "No repositories detected in discovered sessions") via
                // AnsiConsole. Don't tack on a misleading "cancelled" message.
                return 1;
            }
        }

        transcriptFiles = await ImportScopeFilter.Apply(
            transcriptFiles,
            scope,
            (t, _) => new ValueTask<(string?, string?)>(
                resolved.TryGetValue(t.SessionId, out var v) && v is { } x ? (x.Owner, x.Name) : (null, null)));

        if (transcriptFiles.Count == 0) {
            display.Line("No sessions match the selected scope.");
            return 0;
        }

        // --- Confirmation ---
        var sampleRepos = new List<string>();
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in transcriptFiles) {
                if (!resolved.TryGetValue(t.SessionId, out var v) || v is not { } x) continue;
                if (seen.Add($"{x.Owner}/{x.Name}")) sampleRepos.Add($"{x.Owner}/{x.Name}");
            }
        }

        var visibilityDesc = forcePrivate
            ? "private (--private)"
            : $"{kapacitorConfig?.DefaultVisibility ?? "org_public"} (from profile)";

        if (!ImportScopePrompt.PromptConfirm(
                scope, transcriptFiles.Count, sampleRepos, visibilityDesc, skipConfirmation)) {
            await Console.Error.WriteLineAsync("Import cancelled.");
            return 0;
        }

        // --- Classify (parallel probes) ---
        var                         excludedRepos = kapacitorConfig?.ExcludedRepos;
        var                         excludedPaths = (await AppConfig.GetActiveProfileAsync())?.ExcludedPaths;
        List<SessionClassification> classifications;

        if (display.Tty) {
            var tmp = new List<SessionClassification>();

            await AnsiConsole.Progress()
                .AutoClear(true)
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
                .StartAsync(async ctx => {
                        var bar = ctx.AddTask("[yellow]Probing[/]", maxValue: transcriptFiles.Count);

                        var results = await TranscriptFileClassification.ClassifyAsync(
                            httpClient,
                            baseUrl,
                            transcriptFiles,
                            minLines,
                            excludedRepos,
                            CancellationToken.None,
                            vendor,
                            onProbed: () => bar.Increment(1),
                            excludedPaths: excludedPaths
                        );
                        tmp.AddRange(results);
                    }
                );
            classifications = tmp;
        } else {
            display.Line($"Probing {transcriptFiles.Count} sessions...");
            classifications = await TranscriptFileClassification.ClassifyAsync(httpClient, baseUrl, transcriptFiles, minLines, excludedRepos, CancellationToken.None, vendor, excludedPaths: excludedPaths);
        }

        // --- --since filter (Claude path only — Codex pruned at discovery) ---
        if (since is { } sinceCutoff && !codex) {
            var cutoff = sinceCutoff.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

            classifications = [
                .. classifications.Where(c => {
                        var ts = c.Meta.FirstTimestamp?.UtcDateTime;

                        if (ts is null) {
                            try { ts = File.GetLastWriteTimeUtc(c.FilePath); } catch { return true; }
                        }

                        return ts >= cutoff;
                    }
                )
            ];
        }

        // --- Resolve excluded-repo / excluded-path prompts (TTY only; non-TTY auto-skips) ---
        // Repo and path exclusions are independent gates: a session is included only when
        // EVERY applicable exclusion key has been opted-in. Without that, opting into a
        // repo would silently bypass a path the user had explicitly ignored.
        var excludedByRepo = classifications
            .Where(c => c.ExcludedRepoKey is not null)
            .GroupBy(c => c.ExcludedRepoKey!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var excludedByPath = classifications
            .Where(c => c.ExcludedPathKey is not null)
            .GroupBy(c => c.ExcludedPathKey!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        if (excludedByRepo.Count > 0 || excludedByPath.Count > 0) {
            var includedRepoKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var includedPathKeys = new HashSet<string>(StringComparer.Ordinal);
            // Only prompt when both stdin and stdout are interactive. Writing prompts to stderr
            // keeps them visible even when stdout is redirected, but we still can't ReadLine
            // meaningfully without a TTY on stdin.
            var canPrompt = display.Tty && !Console.IsInputRedirected;

            if (canPrompt) {
                foreach (var (key, sessions) in excludedByRepo) {
                    await Console.Error.WriteAsync($"Repository {key} is excluded. Include {sessions.Count} session{(sessions.Count == 1 ? "" : "s")} from it? (y/N) ");
                    var answer = Console.ReadLine()?.Trim();
                    if (string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)) includedRepoKeys.Add(key);
                }

                foreach (var (key, sessions) in excludedByPath) {
                    await Console.Error.WriteAsync($"Path {key} is excluded. Include {sessions.Count} session{(sessions.Count == 1 ? "" : "s")} under it? (y/N) ");
                    var answer = Console.ReadLine()?.Trim();
                    if (string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)) includedPathKeys.Add(key);
                }
            } else {
                var distinctSessions = excludedByRepo.Values.SelectMany(v => v)
                    .Concat(excludedByPath.Values.SelectMany(v => v))
                    .Select(c => c.SessionId)
                    .Distinct()
                    .Count();
                var totalGroups = excludedByRepo.Count + excludedByPath.Count;
                await Console.Error.WriteLineAsync($"Auto-skipping {distinctSessions} session(s) from {totalGroups} excluded source(s) (non-interactive).");
            }

            for (var i = 0; i < classifications.Count; i++) {
                var c = classifications[i];

                if (ShouldExclude(c, includedRepoKeys, includedPathKeys)) {
                    classifications[i] = c with { Status = ClassificationStatus.Excluded };
                }
            }
        }

        // --- Plan grid ---
        var planCounts = new ClassificationCounts(
            New: classifications.Count(c => c.Status           == ClassificationStatus.New),
            Partial: classifications.Count(c => c.Status       == ClassificationStatus.Partial),
            AlreadyLoaded: classifications.Count(c => c.Status == ClassificationStatus.AlreadyLoaded),
            TooShort: classifications.Count(c => c.Status      == ClassificationStatus.TooShort),
            Excluded: classifications.Count(c => c.Status      == ClassificationStatus.Excluded),
            ProbeError: classifications.Count(c => c.Status    == ClassificationStatus.ProbeError)
        );

        display.BeginPhase("Plan");
        display.WritePlanGrid(planCounts);

        // --- Build chains + set continuation predecessors ---
        var continuationMap = BuildContinuationMapFromClassifications(classifications);

        classifications = [
            .. classifications.Select(c =>
                continuationMap.TryGetValue(c.SessionId, out var prev) ? c with { PreviousSessionId = prev } : c
            )
        ];
        var chains = BuildImportChains(classifications);

        // --- Import ---
        // ConcurrentBag rather than List: OnTitleTaskReady / OnBackgroundWorkReady
        // callbacks fire from parallel chain-worker threads, so a plain List's
        // Add would race. No ordering guarantees needed — we only enumerate at
        // the end via Task.WhenAll.
        var       backgroundTasks    = new System.Collections.Concurrent.ConcurrentBag<Task>();
        var       titleTaskCount     = 0;
        var       summaryTaskCount   = 0;
        using var concurrencyLimit   = new SemaphoreSlim(3);
        var       titlesGenerated    = 0;
        var       titlesSkipped      = 0;
        var       titlesFailed       = 0;
        var       summariesGenerated = 0;
        var       summariesFailed    = 0;
        var       titleFailures      = new System.Collections.Concurrent.ConcurrentBag<(string SessionId, string Reason)>();
        var       summaryFailures    = new System.Collections.Concurrent.ConcurrentBag<(string SessionId, string Reason)>();
        var       importedSessionIds = new System.Collections.Concurrent.ConcurrentBag<string>();

        var events = new ChainWorkerEvents {
            OnSessionStarted  = (_, _) => { },    // non-TTY: session start is silent; TTY overrides below
            OnSubagentStarted = (_, _, _) => { }, // non-TTY: subagent start is silent; TTY overrides below
            OnSubagentFinished = (_, _, aid, lines) => display.Line(
                $"  ↳ imported subagent {aid} ({lines} lines)",
                $"  [dim]↳[/] imported subagent [cyan]{Markup.Escape(aid)}[/] ({lines} lines)"
            ),
            OnSessionErrored = (_, sid, reason) => display.Line(
                $"Skipping {sid} [{reason}]",
                $"[red]✗[/] Skipping [cyan]{Markup.Escape(sid)}[/] [{Markup.Escape(reason)}]"
            ),
            OnSessionEnded = (_, c, outcome, lines) => {
                importedSessionIds.Add(c.SessionId);
                var verb = outcome == SessionImportOutcome.Resumed
                    ? $"resuming from line {c.ResumeFromLine}"
                    : "new";

                display.Line(
                    $"Loading {c.SessionId}... {lines} lines [{verb}]",
                    $"[green]✓[/] Loading [cyan]{Markup.Escape(c.SessionId)}[/]... {lines} lines [{verb}]"
                );
            },
            OnTitleTaskReady = t => {
                var (sid, fp, _, vnd) = t;
                Interlocked.Increment(ref titleTaskCount);

                backgroundTasks.Add(
                    Task.Run(async () => {
                            await concurrencyLimit.WaitAsync();

                            try {
                                var result = await GenerateTitleForImportAsync(httpClient, baseUrl, sid, fp, vnd);

                                switch (result) {
                                    case TitleResult.Generated: Interlocked.Increment(ref titlesGenerated); break;
                                    case TitleResult.Skipped:   Interlocked.Increment(ref titlesSkipped); break;
                                    case TitleResult.Failed:
                                        Interlocked.Increment(ref titlesFailed);
                                        titleFailures.Add((sid, "generation error"));

                                        break;
                                }
                            } finally { concurrencyLimit.Release(); }
                        }
                    )
                );
            },
            OnBackgroundWorkReady = t => {
                if (!t.GenerateWhatsDone || !generateSummaries) return;

                Interlocked.Increment(ref summaryTaskCount);
                var sid = t.SessionId;
                var vnd = t.Vendor;

                backgroundTasks.Add(
                    Task.Run(async () => {
                            await concurrencyLimit.WaitAsync();

                            try {
                                var rc = await WhatsDoneCommand.GenerateForSessionAsync(baseUrl, sid, _ => { }, vnd);

                                if (rc == 0) Interlocked.Increment(ref summariesGenerated);
                                else {
                                    Interlocked.Increment(ref summariesFailed);
                                    summaryFailures.Add((sid, $"exit {rc}"));
                                }
                            } catch (Exception ex) {
                                Interlocked.Increment(ref summariesFailed);
                                summaryFailures.Add((sid, ex.Message));
                            } finally { concurrencyLimit.Release(); }
                        }
                    )
                );
            },
        };

        ImportChainsResult importResult;

        if (chains.Count > 0) {
            display.BeginPhase($"Importing {chains.Sum(c => c.Count)} sessions");

            if (display.Tty) {
                var r = default(ImportChainsResult);

                await AnsiConsole.Progress()
                    .AutoClear(false)
                    .HideCompleted(false)
                    .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
                    .StartAsync(async ctx => {
                            var bar = ctx.AddTask("[green]Importing[/]", maxValue: chains.Sum(c => c.Count));

                            // Four description-only progress tasks rendered as live "slot rows"
                            // beneath the main bar. IsIndeterminate=true draws a stripe
                            // animation while a worker is processing; setting it to false
                            // and Description="idle" parks the slot.
                            var slots = new ProgressTask[ImportWorkerCount];

                            for (var i = 0; i < ImportWorkerCount; i++) {
                                slots[i]                 = ctx.AddTask($"  Slot {i + 1} — idle", maxValue: 1);
                                slots[i].IsIndeterminate = false;
                            }

                            // currentSession[slot] holds the SessionId currently rendered on
                            // the slot row, used to revert the description after a subagent
                            // finishes (revert from "↳ subagent X" to "Loading <parent>").
                            var currentVerb = new string[ImportWorkerCount];
                            var currentSid  = new string[ImportWorkerCount];

                            var wrappedEvents = events with {
                                OnSessionStarted = (slot, c) => {
                                    var verb = c.Status == ClassificationStatus.Partial
                                        ? $"resuming from line {c.ResumeFromLine}"
                                        : "new";
                                    currentSid[slot]  = c.SessionId;
                                    currentVerb[slot] = verb;
                                    SetSlot(slot, $"  [bold]Slot {slot + 1}[/] — Loading [cyan]{Markup.Escape(c.SessionId)}[/] ({verb})");
                                },
                                OnSubagentStarted = (slot, sid, aid) => {
                                    SetSlot(slot, $"  [bold]Slot {slot + 1}[/] — [dim]↳[/] subagent [cyan]{Markup.Escape(aid)}[/] (parent {Markup.Escape(sid)})");
                                },
                                OnSubagentFinished = (slot, _, _, _) => {
                                    // Revert to the parent session's "Loading" description.
                                    // No scrollback line in TTY mode — subagent activity was
                                    // already visible on the slot row while it ran.
                                    if (!string.IsNullOrEmpty(currentSid[slot])) {
                                        SetSlot(
                                            slot,
                                            $"  [bold]Slot {slot + 1}[/] — Loading [cyan]{Markup.Escape(currentSid[slot])}[/] ({currentVerb[slot]})"
                                        );
                                    }
                                },
                                OnSessionEnded = (slot, c, _, _) => {
                                    importedSessionIds.Add(c.SessionId);
                                    // Description stays on the just-finished session until
                                    // the next OnSessionStarted swaps it. We only flip the
                                    // stripe off here so a slot that drains (queue empty)
                                    // looks calm.
                                    bar.Increment(1);
                                    slots[slot].IsIndeterminate = false;
                                    // Suppress the legacy per-session log line in TTY mode
                                    // by NOT calling the base handler. Slot rows showed the
                                    // session while it ran; errors render via scrollback below.
                                },
                                OnSessionErrored = (slot, sid, reason) => {
                                    bar.Increment(1);
                                    IdleSlot(slot);
                                    // Errors print to scrollback above the live region —
                                    // Spectre.Console.Progress flushes prior writes.
                                    AnsiConsole.MarkupLine($"[red]✗[/] Skipping [cyan]{Markup.Escape(sid)}[/] [{Markup.Escape(reason)}]");
                                },
                            };

                            r = await ImportChainsAsync(httpClient, baseUrl, chains, wrappedEvents, CancellationToken.None);

                            // After the await, all workers have drained; mark every slot idle.
                            for (var i = 0; i < ImportWorkerCount; i++) IdleSlot(i);

                            return;

                            void IdleSlot(int slot) {
                                slots[slot].Description     = $"  Slot {slot + 1} — idle";
                                slots[slot].IsIndeterminate = false;
                                currentSid[slot]            = "";
                                currentVerb[slot]           = "";
                            }

                            void SetSlot(int slot, string markup) {
                                slots[slot].Description     = markup;
                                slots[slot].IsIndeterminate = true;
                            }
                        }
                    );
                importResult = r!;
            } else {
                importResult = await ImportChainsAsync(httpClient, baseUrl, chains, events, CancellationToken.None);
            }
        } else {
            importResult = new(0, 0, 0);
        }

        // --- --private: mark all imported sessions owner-only ---
        if (forcePrivate && !importedSessionIds.IsEmpty) {
            display.BeginPhase("Marking imported sessions private");
            await SetVisibilityNoneForAll(httpClient, baseUrl, [.. importedSessionIds]);
        }

        // --- Background phase (titles / summaries) ---
        var ranBackground = backgroundTasks.IsEmpty;

        if (ranBackground) {
            display.BeginPhase("Titles & summaries");

            if (display.Tty) {
                await AnsiConsole.Progress()
                    .AutoClear(false)
                    .HideCompleted(false)
                    .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
                    .StartAsync(async ctx => {
                            var titleTask   = titleTaskCount   > 0 ? ctx.AddTask("[cyan]Titles[/]", maxValue: titleTaskCount) : null;
                            var summaryTask = summaryTaskCount > 0 ? ctx.AddTask("[cyan]Summaries[/]", maxValue: summaryTaskCount) : null;
                            var seenT       = 0;
                            var seenS       = 0;

                            while (backgroundTasks.Any(t => !t.IsCompleted)) {
                                titleTask?.Value   = titlesGenerated    + titlesFailed + titlesSkipped;
                                summaryTask?.Value = summariesGenerated + summariesFailed;
                                var tList = titleFailures.ToList();
                                var sList = summaryFailures.ToList();

                                for (var i = seenT; i < tList.Count; i++) {
                                    var (sid, reason) = tList[i];
                                    AnsiConsole.MarkupLine($"  [red]✗[/] title failed for [cyan]{Markup.Escape(sid)}[/]: {Markup.Escape(reason)}");
                                }

                                seenT = tList.Count;

                                for (var i = seenS; i < sList.Count; i++) {
                                    var (sid, reason) = sList[i];
                                    AnsiConsole.MarkupLine($"  [red]✗[/] summary failed for [cyan]{Markup.Escape(sid)}[/]: {Markup.Escape(reason)}");
                                }

                                seenS = sList.Count;
                                await Task.Delay(250);
                            }

                            try { await Task.WhenAll(backgroundTasks); } catch {
                                /* per-task try/catch */
                            }

                            titleTask?.Value   = titlesGenerated    + titlesFailed + titlesSkipped;
                            summaryTask?.Value = summariesGenerated + summariesFailed;
                        }
                    );
            } else {
                display.Line($"Waiting for {backgroundTasks.Count} background task(s) (titles/summaries)...");

                try { await Task.WhenAll(backgroundTasks); } catch {
                    /* per-task */
                }

                foreach (var (sid, reason) in titleFailures)
                    display.Line($"  ✗ title failed for {sid}: {reason}");

                foreach (var (sid, reason) in summaryFailures)
                    display.Line($"  ✗ summary failed for {sid}: {reason}");
            }
        }

        // --- Done ---
        var final = new FinalCounts(
            Loaded: importResult.Loaded,
            Resumed: importResult.Resumed,
            AlreadyLoaded: planCounts.AlreadyLoaded,
            TooShort: planCounts.TooShort,
            Excluded: planCounts.Excluded,
            ProbeError: planCounts.ProbeError,
            Errored: importResult.Errored,
            TitlesGenerated: titlesGenerated,
            TitlesSkipped: titlesSkipped,
            TitlesFailed: titlesFailed,
            SummariesGenerated: summariesGenerated,
            SummariesFailed: summariesFailed,
            RanBackground: ranBackground,
            RequestedSummaries: summaryTaskCount > 0
        );
        display.WriteDoneGrid(final);

        return 0;
    }

    /// <summary>
    /// Timestamp used to order a continuation chain. Prefers the transcript's first
    /// observed message timestamp; falls back to the file's last-write mtime when
    /// the transcript has no parseable timestamp in its first 50 lines (best-effort
    /// `ExtractSessionMetadata` leaves `FirstTimestamp` null in that case).
    /// <br/>
    /// Without this fallback, timestamp-less transcripts would all sort at
    /// `DateTimeOffset.MinValue` and could displace real predecessors — corrupting
    /// `previous_session_id` links in the session-start hook. The mtime lookup is
    /// wrapped so a file deleted between discovery and chain building can't crash
    /// the whole import run; ordering is best-effort.
    /// </summary>
    static DateTimeOffset ChainTimestamp(SessionClassification c) {
        if (c.Meta.FirstTimestamp is { } ts) return ts;

        try {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(c.FilePath), TimeSpan.Zero);
        } catch {
            return DateTimeOffset.MinValue;
        }
    }

    /// <summary>
    /// Build a sessionId → previousSessionId map from classifications, grouping by slug.
    /// Replaces BuildContinuationMap which read transcripts again.
    /// </summary>
    static Dictionary<string, string> BuildContinuationMapFromClassifications(List<SessionClassification> classifications) {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        var bySlug = classifications
            .Where(c => c.Meta.Slug is not null)
            .GroupBy(c => c.Meta.Slug!, StringComparer.Ordinal);

        foreach (var group in bySlug) {
            var chain = group
                .OrderBy(ChainTimestamp)
                .ThenBy(c => c.SessionId, StringComparer.Ordinal)
                .ToList();

            for (var i = 1; i < chain.Count; i++)
                map[chain[i].SessionId] = chain[i - 1].SessionId;
        }

        return map;
    }

    internal static SessionMetadata ExtractSessionMetadata(string filePath) {
        var meta = new SessionMetadata();

        try {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            var linesChecked = 0;

            while (reader.ReadLine() is { } line && linesChecked < 50) {
                linesChecked++;

                if (string.IsNullOrWhiteSpace(line)) {
                    continue;
                }

                try {
                    var doc  = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    // Skip file-history-snapshot entries
                    if (root.Str("type") == "file-history-snapshot") {
                        continue;
                    }

                    // Extract cwd from metadata
                    meta.Cwd ??= root.Str("cwd");

                    // Extract model from assistant message
                    meta.Model ??= root.Obj("message")?.Str("model");

                    // Extract slug from metadata
                    meta.Slug ??= root.Str("slug");

                    // Extract sessionId
                    meta.SessionId ??= root.Str("sessionId");

                    // Extract first timestamp for continuation ordering
                    if (meta.FirstTimestamp is null
                     && root.Str("timestamp") is { } tsStr
                     && DateTimeOffset.TryParse(tsStr, out var ts)) {
                        meta.FirstTimestamp = ts;
                    }

                    // Stop early once we have all metadata
                    if (meta.Cwd is not null && meta.Model is not null && meta.Slug is not null) {
                        break;
                    }
                } catch (JsonException) { }
            }
        } catch {
            // Best effort metadata extraction
        }

        return meta;
    }

    internal static DateTimeOffset? ExtractLastTimestamp(string filePath) {
        try {
            // Read backward from end of file to find the last timestamp without loading everything into memory.
            // Strategy: read the last ~64KB chunk which covers well over 50 JSONL lines.
            using var fs        = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            const int chunkSize = 64 * 1024;
            var       offset    = Math.Max(0, fs.Length - chunkSize);
            fs.Seek(offset, SeekOrigin.Begin);

            using var reader = new StreamReader(fs);

            // If we seeked mid-file, skip the first partial line
            if (offset > 0) reader.ReadLine();

            // Collect the last 50 non-empty lines
            var tail = new List<string>(50);

            while (reader.ReadLine() is { } line) {
                if (!string.IsNullOrWhiteSpace(line)) {
                    tail.Add(line);

                    if (tail.Count > 50) tail.RemoveAt(0);
                }
            }

            // Scan from the end
            for (var i = tail.Count - 1; i >= 0; i--) {
                try {
                    using var doc  = JsonDocument.Parse(tail[i]);
                    var       root = doc.RootElement;

                    if (root.Str("timestamp") is { } tsStr && DateTimeOffset.TryParse(tsStr, out var ts)) {
                        return ts;
                    }
                } catch (JsonException) { }
            }
        } catch {
            // Best effort
        }

        return null;
    }

    static string? ExtractCwdFromTranscript(string filePath, bool codex = false) {
        try {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            var linesChecked = 0;

            while (reader.ReadLine() is { } line && linesChecked < 20) {
                linesChecked++;

                if (string.IsNullOrWhiteSpace(line)) {
                    continue;
                }

                try {
                    var doc = JsonDocument.Parse(line);

                    // Codex stores cwd inside a session_meta envelope; Claude stores it
                    // at the JSONL root.
                    var cwd = codex
                        ? doc.RootElement.Obj("payload")?.Str("cwd")
                        : doc.RootElement.Str("cwd");

                    if (cwd is { }) {
                        return cwd;
                    }
                } catch (JsonException) { }
            }
        } catch {
            // Best effort
        }

        return null;
    }

    /// <summary>
    /// Codex-shape variant of <see cref="ExtractSessionMetadata"/>. Reads the first
    /// <c>session_meta</c> line and pulls cwd, the inner timestamp (when codex
    /// started, not when the envelope was written), and the model. Codex rollouts
    /// have no slug, so <see cref="SessionMetadata.Slug"/> stays null.
    ///
    /// Model resolution: <c>turn_context.payload.model</c> (the real model name,
    /// e.g. <c>gpt-5.5</c>) is preferred when present; falls back to
    /// <c>session_meta.payload.model_provider</c> (e.g. <c>openai</c>) when the
    /// rollout never reaches its first turn. The first turn_context typically
    /// arrives within the first few lines, but we scan generously in case
    /// future Codex versions interleave more preludes.
    /// </summary>
    internal static SessionMetadata ExtractCodexSessionMetadata(string filePath) {
        const int MaxLines = 50;

        var meta             = new SessionMetadata();
        var sessionMetaFound = false;
        var turnModelFound   = false;

        try {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            var linesChecked = 0;

            while (reader.ReadLine() is { } line && linesChecked < MaxLines) {
                linesChecked++;

                if (string.IsNullOrWhiteSpace(line)) continue;

                try {
                    using var doc  = JsonDocument.Parse(line);
                    var       root = doc.RootElement;

                    var type = root.Str("type");

                    if (!sessionMetaFound && type == "session_meta") {
                        if (root.Obj("payload") is not { } payload) continue;

                        meta.Cwd       = payload.Str("cwd");
                        meta.Model     = payload.Str("model_provider");
                        meta.SessionId = payload.Str("id");

                        if (payload.Str("timestamp") is { } tsStr
                         && DateTimeOffset.TryParse(tsStr, out var ts)) {
                            meta.FirstTimestamp = ts;
                        }

                        sessionMetaFound = true;
                    } else if (sessionMetaFound && !turnModelFound && type == "turn_context") {
                        // Only honor turn_context AFTER session_meta — a turn_context
                        // that appears before the header (truncated/corrupt rollout) is
                        // unreliable and would otherwise stamp a model name onto an
                        // otherwise-empty meta.
                        if (root.Obj("payload")?.Str("model") is { Length: > 0 } turnModel) {
                            meta.Model     = turnModel;
                            turnModelFound = true;
                        }
                    }

                    if (sessionMetaFound && turnModelFound) break;
                } catch (JsonException) { }
            }
        } catch {
            // Best effort
        }

        return meta;
    }

    internal sealed record CodexGitInfo(string? RemoteUrl, string? Branch, string? CommitHash);

    /// <summary>
    /// Extract the optional <c>git</c> block from the first <c>session_meta</c> line
    /// of a Codex rollout (commit_hash, branch, repository_url). Returns null when
    /// there's no git block — codex omits it for sessions started outside a repo.
    /// </summary>
    internal static CodexGitInfo? ExtractCodexGitInfo(string filePath) {
        try {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            var linesChecked = 0;

            while (reader.ReadLine() is { } line && linesChecked < 5) {
                linesChecked++;

                if (string.IsNullOrWhiteSpace(line)) continue;

                try {
                    using var doc  = JsonDocument.Parse(line);
                    var       root = doc.RootElement;

                    if (root.Str("type") != "session_meta") continue;

                    var git = root.Obj("payload")?.Obj("git");

                    if (git is null) return null;

                    return new CodexGitInfo(
                        RemoteUrl: git.Value.Str("repository_url"),
                        Branch: git.Value.Str("branch"),
                        CommitHash: git.Value.Str("commit_hash")
                    );
                } catch (JsonException) { }
            }
        } catch {
            // Best effort
        }

        return null;
    }

    /// <summary>
    /// Build a SessionId → repo lookup for every discovered transcript. Repo
    /// detection (git + `gh pr view`) is the slow part of the discovery phase
    /// and ran sequentially per-session pre-AI-692, making `kapacitor import`
    /// look frozen on large histories. This version deduplicates by cwd
    /// (transcripts in the same project share a repo) and runs detection in
    /// parallel, surfacing progress via a Spectre status spinner on a TTY and
    /// a plain status line otherwise.
    /// </summary>
    static async Task<Dictionary<string, (string Owner, string Name)?>> ResolveTranscriptReposAsync(
            IReadOnlyList<(string SessionId, string FilePath, string EncodedCwd)> transcripts,
            bool                                                                   codex,
            ImportDisplay                                                          display) {
        // Extract cwd per transcript first (cheap: ≤20-line file read).
        var perTranscript = new (string SessionId, string? Cwd)[transcripts.Count];
        for (var i = 0; i < transcripts.Count; i++) {
            perTranscript[i] = (transcripts[i].SessionId, ExtractCwdFromTranscript(transcripts[i].FilePath, codex));
        }

        var uniqueCwds = perTranscript
            .Select(p => p.Cwd)
            .Where(c => c is not null)
            .Distinct(StringComparer.Ordinal)
            .Cast<string>()
            .ToList();

        var repoByCwd = new ConcurrentDictionary<string, (string Owner, string Name)?>(StringComparer.Ordinal);

        if (uniqueCwds.Count > 0) {
            var options = new ParallelOptions { MaxDegreeOfParallelism = 8 };
            var done    = 0;
            var total   = uniqueCwds.Count;

            async ValueTask DetectOne(string cwd) {
                var repo = await RepositoryDetection.DetectRepositoryAsync(cwd);
                repoByCwd[cwd] = repo is { Owner: { } o, RepoName: { } n } ? (o, n) : null;
            }

            if (display.Tty) {
                var statusLock = new Lock();
                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync($"Scanning repositories (0/{total})…", async ctx => {
                        await Parallel.ForEachAsync(uniqueCwds, options, async (cwd, _) => {
                            await DetectOne(cwd);
                            var d = Interlocked.Increment(ref done);
                            // Spectre's StatusContext isn't documented as thread-safe; serialize
                            // the per-completion update so parallel workers can't race on the
                            // status renderer.
                            lock (statusLock) {
                                ctx.Status($"Scanning repositories ({d}/{total})…");
                            }
                        });
                    });
            } else {
                display.Line($"Scanning {total} repositor{(total == 1 ? "y" : "ies")}...");
                await Parallel.ForEachAsync(uniqueCwds, options, async (cwd, _) => await DetectOne(cwd));
            }
        }

        var resolved = new Dictionary<string, (string Owner, string Name)?>(StringComparer.Ordinal);
        foreach (var (sid, cwd) in perTranscript) {
            resolved[sid] = cwd is not null && repoByCwd.TryGetValue(cwd, out var r) ? r : null;
        }
        return resolved;
    }

    /// <summary>
    /// Enumerate ~/.claude/projects/*/*.jsonl transcripts, deduplicating directories
    /// by their resolved path (so symlinked project dirs don't scan the same files
    /// twice). Returns one entry per transcript with the normalized session id.
    /// </summary>
    internal static List<(string SessionId, string FilePath, string EncodedCwd)> DiscoverTranscripts(string projectsDir) {
        var results = new List<(string, string, string)>();
        var seen    = new HashSet<string>(StringComparer.Ordinal);

        if (!Directory.Exists(projectsDir)) return results;

        foreach (var cwdDir in Directory.GetDirectories(projectsDir)) {
            var realPath = new DirectoryInfo(cwdDir).ResolveLinkTarget(returnFinalTarget: true)?.FullName
             ?? Path.GetFullPath(cwdDir);

            if (!seen.Add(realPath)) continue;

            var encodedCwd = Path.GetFileName(cwdDir);

            results.AddRange(
                from jsonlFile in Directory.GetFiles(cwdDir, "*.jsonl")
                let sessionId = NormalizeGuid(Path.GetFileNameWithoutExtension(jsonlFile))
                select (sessionId, jsonlFile, encodedCwd)
            );
        }

        return results;
    }

    /// <summary>
    /// Group the import-bound subset (New + Partial) into ordered chains by slug.
    /// A chain is a list of classifications sharing the same slug, ordered by
    /// FirstTimestamp ascending. Sessions without a slug (or with a unique slug)
    /// become chains of length 1. Chain order (across chains) is stable by slug
    /// string so re-runs import in the same order.
    /// </summary>
    internal static List<List<SessionClassification>> BuildImportChains(List<SessionClassification> classifications) {
        var importable = classifications
            .Where(c => c.Status is ClassificationStatus.New or ClassificationStatus.Partial)
            .ToList();

        var withSlug = importable
            .Where(c => c.Meta.Slug is not null)
            .GroupBy(c => c.Meta.Slug!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        var chains = withSlug.Select(group => group.OrderBy(ChainTimestamp)
                .ThenBy(c => c.SessionId, StringComparer.Ordinal)
                .ToList()
            )
            .ToList();
        chains.AddRange(importable.Where(c => c.Meta.Slug is null).OrderBy(c => c.SessionId, StringComparer.Ordinal).Select(solo => (List<SessionClassification>)[solo]));

        return chains;
    }

    /// <summary>
    /// Normalize a GUID string to dashless format (matching the live CLI's NormalizeGuidField).
    /// Non-GUID strings are returned as-is.
    /// </summary>
    static string NormalizeGuid(string value) =>
        Guid.TryParse(value, out var guid) ? guid.ToString("N") : value;

    static async Task<TitleResult> GenerateTitleForImportAsync(HttpClient httpClient, string baseUrl, string sessionId, string filePath, string vendor) {
        try {
            var (userText, assistantText) = vendor == "codex"
                ? TitleGenerator.ExtractCodexTitleContext(filePath)
                : TitleGenerator.ExtractTitleContext(filePath);

            if (userText is null) {
                return TitleResult.Skipped;
            }

            var result = await TitleGenerator.GenerateAsync(userText, assistantText, _ => { }, vendor);

            if (result is null) {
                return TitleResult.Skipped;
            }

            var payload = new SessionTitlePayload {
                SessionId        = sessionId,
                Title            = result.Result,
                Model            = result.Model,
                InputTokens      = result.InputTokens,
                OutputTokens     = result.OutputTokens,
                CacheReadTokens  = result.CacheReadTokens,
                CacheWriteTokens = result.CacheWriteTokens
            };

            var       payloadJson = JsonSerializer.Serialize(payload, Kapacitor.Cli.Core.KapacitorJsonContext.Default.SessionTitlePayload);
            using var content     = new StringContent(payloadJson, Encoding.UTF8, "application/json");
            using var titleResp   = await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/session-title", content);

            return TitleResult.Generated;
        } catch {
            return TitleResult.Failed;
        }
    }

    enum TitleResult { Generated, Skipped, Failed }

    internal sealed record ImportChainsResult(int Loaded, int Resumed, int Errored);

    internal sealed record ChainWorkerEvents {
        /// <summary>Fired before a worker begins importing a session on its slot.</summary>
        public required Action<int, SessionClassification> OnSessionStarted { get; init; }

        /// <summary>Fired when the worker begins streaming a subagent's transcript inline.</summary>
        public required Action<int, string, string> OnSubagentStarted { get; init; } // slot, sessionId, agentId

        /// <summary>Fired after a subagent's transcript has been fully streamed.</summary>
        public required Action<int, string, string, int> OnSubagentFinished { get; init; } // slot, sessionId, agentId, lines

        /// <summary>Fired when a session import fails on a worker slot.</summary>
        public required Action<int, string, string> OnSessionErrored { get; init; } // slot, sessionId, reason

        /// <summary>
        /// Fired after a session import completes (loaded or resumed). The slot is
        /// available for the next session as soon as this returns.
        /// </summary>
        public required Action<int, SessionClassification, SessionImportOutcome, int> OnSessionEnded { get; init; }
        // slot, classification, outcome (Loaded|Resumed), linesSent

        /// <summary>Fired when a successfully-imported session is ready for title generation.</summary>
        public required Action<(string SessionId, string FilePath, string? PreviousSessionId, string Vendor)> OnTitleTaskReady { get; init; }

        /// <summary>
        /// Fired when a session's session-end hook returned, signalling that the
        /// background phase may enqueue title / what's-done work for it.
        /// Renamed from the previous `OnSessionEnded` to disambiguate from the
        /// slot-aware lifecycle event above.
        /// </summary>
        public required Action<(string SessionId, bool GenerateWhatsDone, string Vendor)> OnBackgroundWorkReady { get; init; }
    }

    /// <summary>
    /// Dispatch chains across 4 parallel workers; sessions within a chain run
    /// serially. Thread-safe: counters use Interlocked, callbacks must be
    /// thread-safe (production wiring uses AnsiConsole + ConcurrentBag).
    /// </summary>
    internal static async Task<ImportChainsResult> ImportChainsAsync(
            HttpClient                        httpClient,
            string                            baseUrl,
            List<List<SessionClassification>> chains,
            ChainWorkerEvents                 events,
            CancellationToken                 ct
        ) {
        var loaded  = 0;
        var resumed = 0;
        var errored = 0;

        var queue = Channel.CreateUnbounded<List<SessionClassification>>(
            new() {
                SingleReader = false,
                SingleWriter = true,
            }
        );

        foreach (var chain in chains) await queue.Writer.WriteAsync(chain, ct);
        queue.Writer.Complete();

        var workers = new Task[ImportWorkerCount];

        for (var i = 0; i < ImportWorkerCount; i++) {
            var slot = i; // capture

            workers[i] = Task.Run(
                async () => {
                    while (await queue.Reader.WaitToReadAsync(ct)) {
                        while (queue.Reader.TryRead(out var chain)) {
                            foreach (var session in chain) {
                                ct.ThrowIfCancellationRequested();
                                events.OnSessionStarted(slot, session);

                                SessionImportOutcome r;

                                var linesSent = 0;

                                try {
                                    (r, linesSent) = await ImportSingleSessionAsync(httpClient, baseUrl, session, slot, events, ct);
                                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                                    throw;
                                } catch (Exception ex) {
                                    events.OnSessionErrored(slot, session.SessionId, ex.Message);
                                    r = SessionImportOutcome.Errored;
                                }

                                switch (r) {
                                    case SessionImportOutcome.Loaded:  Interlocked.Increment(ref loaded); break;
                                    case SessionImportOutcome.Resumed: Interlocked.Increment(ref resumed); break;
                                    case SessionImportOutcome.Errored: Interlocked.Increment(ref errored); break;
                                }

                                if (r != SessionImportOutcome.Errored) {
                                    events.OnSessionEnded(slot, session, r, linesSent);
                                }
                            }
                        }
                    }
                },
                ct
            );
        }

        await Task.WhenAll(workers);

        return new(loaded, resumed, errored);
    }

    internal enum SessionImportOutcome { Loaded, Resumed, Errored }

    static async Task<(SessionImportOutcome Outcome, int LinesSent)> ImportSingleSessionAsync(
            HttpClient            httpClient,
            string                baseUrl,
            SessionClassification session,
            int                   slot,
            ChainWorkerEvents     events,
            CancellationToken     ct
        ) {
        IProgress<ImportProgress> perSessionProgress = new CallbackProgress(ev => {
                switch (ev) {
                    case SubagentStarted ss:  events.OnSubagentStarted(slot, session.SessionId, ss.AgentId); break;
                    case SubagentFinished sf: events.OnSubagentFinished(slot, session.SessionId, sf.AgentId, sf.LinesSent); break;
                }
            }
        );

        if (session.Status == ClassificationStatus.Partial) {
            try {
                var linesSent = await SessionImporter.SendTranscriptBatches(
                    httpClient,
                    baseUrl,
                    session.SessionId,
                    session.FilePath,
                    agentId: null,
                    startLine: session.ResumeFromLine,
                    progress: perSessionProgress,
                    vendor: session.Vendor
                );

                return (SessionImportOutcome.Resumed, linesSent);
            } catch (HttpRequestException ex) {
                events.OnSessionErrored(slot, session.SessionId, $"server unreachable: {ex.Message}");

                return (SessionImportOutcome.Errored, 0);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                events.OnSessionErrored(slot, session.SessionId, ex.Message);

                return (SessionImportOutcome.Errored, 0);
            }
        }

        // status == New: session-start → import → session-end → enqueue background tasks
        var meta = session.Meta;
        var cwd  = meta.Cwd ?? SessionImporter.DecodeCwdFromDirName(session.EncodedCwd);

        var startHook = new System.Text.Json.Nodes.JsonObject {
            ["session_id"]      = session.SessionId,
            ["transcript_path"] = session.FilePath,
            ["cwd"]             = cwd ?? "",
            ["source"]          = "Startup",
            ["hook_event_name"] = "session_start",
            ["model"]           = meta.Model,
        };
        if (meta.FirstTimestamp is not null) startHook["started_at"]                = meta.FirstTimestamp.Value.ToString("O");
        if (session.PreviousSessionId is not null) startHook["previous_session_id"] = session.PreviousSessionId;
        if (meta.Slug is not null) startHook["slug"]                                = meta.Slug;
        if (session.Vendor != "claude") startHook["vendor"]                         = session.Vendor;

        // Codex sessions carry a `git` block on session_meta — prefer it over a fresh
        // RepositoryDetection probe (which reads the live git config and might disagree
        // with what was true when the rollout was recorded). Detection still runs as a
        // fallback for fields the rollout omits (user_name / user_email).
        var codexRepo = session.Vendor == "codex" ? ExtractCodexGitInfo(session.FilePath) : null;

        if (cwd is not null) {
            var repo = await RepositoryDetection.DetectRepositoryAsync(cwd);

            if (repo is not null || codexRepo is not null) {
                var repoNode = new System.Text.Json.Nodes.JsonObject();

                if (repo?.UserName is not null) repoNode["user_name"]   = repo.UserName;
                if (repo?.UserEmail is not null) repoNode["user_email"] = repo.UserEmail;

                var remoteUrl = codexRepo?.RemoteUrl ?? repo?.RemoteUrl;
                if (remoteUrl is not null) repoNode["remote_url"] = remoteUrl;

                var (codexOwner, codexRepoName) = Kapacitor.Cli.Core.GitUrlParser.ParseRemoteUrl(codexRepo?.RemoteUrl);
                var owner    = codexOwner    ?? repo?.Owner;
                var repoName = codexRepoName ?? repo?.RepoName;
                var branch   = codexRepo?.Branch ?? repo?.Branch;

                if (owner is not null) repoNode["owner"]        = owner;
                if (repoName is not null) repoNode["repo_name"] = repoName;
                if (branch is not null) repoNode["branch"]      = branch;

                if (repoNode.Count > 0) startHook["repository"] = repoNode;
            }
        }

        try {
            using var startContent = new StringContent(startHook.ToJsonString(), Encoding.UTF8, "application/json");
            using var startResp    = await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/session-start", startContent, ct: ct);

            if (!startResp.IsSuccessStatusCode) {
                events.OnSessionErrored(slot, session.SessionId, $"session-start failed: HTTP {(int)startResp.StatusCode}");

                return (SessionImportOutcome.Errored, 0);
            }
        } catch (HttpRequestException ex) {
            events.OnSessionErrored(slot, session.SessionId, $"server unreachable: {ex.Message}");

            return (SessionImportOutcome.Errored, 0);
        }

        ImportResult importResult;

        try {
            importResult = await SessionImporter.ImportSessionAsync(
                httpClient,
                baseUrl,
                session.FilePath,
                session.SessionId,
                meta,
                session.EncodedCwd,
                perSessionProgress,
                session.Vendor
            );
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            events.OnSessionErrored(slot, session.SessionId, ex.Message);

            return (SessionImportOutcome.Errored, 0);
        }

        var lastTs = ExtractLastTimestamp(session.FilePath);

        var endHook = new System.Text.Json.Nodes.JsonObject {
            ["session_id"]      = session.SessionId,
            ["transcript_path"] = session.FilePath,
            ["cwd"]             = cwd ?? "",
            ["reason"]          = "Other",
            ["hook_event_name"] = "session_end",
        };
        if (lastTs is not null) endHook["ended_at"] = lastTs.Value.ToString("O");

        var generateWhatsDone = false;

        try {
            using var endContent = new StringContent(endHook.ToJsonString(), Encoding.UTF8, "application/json");
            using var endResp    = await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/session-end", endContent, ct: ct);

            if (endResp.IsSuccessStatusCode) {
                try {
                    var body = await endResp.Content.ReadAsStringAsync(ct);
                    var node = System.Text.Json.Nodes.JsonNode.Parse(body);
                    generateWhatsDone = node?["generate_whats_done"]?.GetValue<bool>() == true;
                } catch {
                    /* best effort */
                }
            }
        } catch {
            /* best effort */
        }

        events.OnTitleTaskReady((session.SessionId, session.FilePath, session.PreviousSessionId, session.Vendor));
        events.OnBackgroundWorkReady((session.SessionId, generateWhatsDone, session.Vendor));

        return (SessionImportOutcome.Loaded, importResult.LinesSent);
    }

    sealed class CallbackProgress(Action<ImportProgress> onReport) : IProgress<ImportProgress> {
        public void Report(ImportProgress value) => onReport(value);
    }

    /// <summary>
    /// Probe each transcript against the server's last-line API and classify
    /// what the import phase should do with it. Probes run concurrently via
    /// SemaphoreSlim(8) — idempotent GETs, safe to parallelize.
    /// </summary>
    /// <summary>
    /// Decides whether a classified session should be force-flipped to Excluded
    /// after the prompt loop. Repo and path exclusions are independent gates:
    /// the session is excluded if ANY applicable exclusion key was not opted-in.
    /// Exposed for testing.
    /// </summary>
    internal static bool ShouldExclude(
            SessionClassification c,
            HashSet<string>       includedRepoKeys,
            HashSet<string>       includedPathKeys
        ) {
        if (c.ExcludedRepoKey is { } repoKey && !includedRepoKeys.Contains(repoKey)) return true;
        if (c.ExcludedPathKey is { } pathKey && !includedPathKeys.Contains(pathKey)) return true;

        return false;
    }

    /// <summary>
    /// PUT visibility=none for every imported session id. Failures are logged
    /// inline (one line per session) but never throw — the import already
    /// succeeded; users can re-run `kapacitor hide` for any that failed.
    /// </summary>
    internal static async Task SetVisibilityNoneForAll(
        HttpClient            httpClient,
        string                baseUrl,
        IReadOnlyList<string> sessionIds) {
        foreach (var sessionId in sessionIds) {
            var payload = new JsonObject { ["visibility"] = "none" };
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            try {
                using var resp = await httpClient.PutWithRetryAsync(
                    $"{baseUrl}/api/sessions/{sessionId}/visibility", content);
                if (!resp.IsSuccessStatusCode) {
                    await Console.Error.WriteLineAsync(
                        $"  ! visibility=none failed for {sessionId}: HTTP {(int)resp.StatusCode}");
                }
            } catch (Exception ex) {
                await Console.Error.WriteLineAsync(
                    $"  ! visibility=none failed for {sessionId}: {ex.Message}");
            }
        }
    }
}
