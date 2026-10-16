using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Commands;

/// <summary>
/// <c>kcap memories import</c> — a one-way copy of Claude Code's per-machine auto-memory notes into server memories,
/// through the same save endpoint the memory MCP server uses. A slug that already exists is reported, or updated with
/// <c>--update</c>, never treated as a failure, so a re-run is safe.
/// </summary>
class MemoriesCommand(
        ConfigRoot config, ProfileContext profiles, TokenStore tokens, ICapacitorHttpClient http, HarnessRegistry harnesses,
        GitProviderRouter router, WorkingDirectory workdir, TimeProvider time) {
    const string Usage = """
        Usage:
          kcap memories import [--from <dir>] [--audience user|team|org] [--team <name>]
                               [--project <slug> | --global] [--update] [--dry-run] [--yes]
        """;

    static readonly string[] ValueFlags = ["--from", "--audience", "--team", "--project"];
    static readonly string[] Switches   = ["--global", "--update", "--dry-run", "--yes", "-y"];

    public async Task<int> HandleAsync(string[] args) {
        if (args.Length < 2 || args[1] != "import") return Fail(args.Length < 2 ? null : $"Unknown memories subcommand: {args[1]}");
        if (ParseFlags(args, 2) is not { } flags) return 1;

        var audience = flags.GetValueOrDefault("--audience") ?? "user";
        if (audience is not ("user" or "team" or "org")) return Fail("--audience must be user, team or org.");
        if (flags.ContainsKey("--team") && audience != "team") return Fail("--team only applies to --audience team.");

        var project = flags.GetValueOrDefault("--project");
        var global  = flags.ContainsKey("--global");
        if (project is not null && global) return Fail("Pass --project or --global, not both.");

        var claude = harnesses.Of<ClaudeHarness>().Paths;
        var from   = flags.GetValueOrDefault("--from") is { } given
            ? Path.GetFullPath(given, workdir.Path)
            : Path.Combine(claude.ProjectDir(workdir.Path), "memory");

        if (!Directory.Exists(from)) {
            await Console.Error.WriteLineAsync($"No memory folder at {from} — pass --from <dir>.");
            return 1;
        }

        // A project home still carries the checkout's hash when there is one: a server that does not know project homes
        // reads a null hash as org-wide, and the hash keeps it at the repository instead.
        string? repoHash = null;
        string? placeDir = null;
        if (!global) {
            placeDir = PlaceDir(from, workdir.Path, claude);
            repoHash = placeDir is null ? null : await new CwdRepository(config, placeDir, router, time).GetHashAsync();
            if (repoHash is null && project is null) {
                await Console.Error.WriteLineAsync(placeDir is null
                    ? $"{from} belongs to another Claude project — run from the checkout it was recorded in, or pass --project <slug> or --global."
                    : $"Cannot resolve a repository for {placeDir} — run from its checkout, or pass --project <slug> or --global.");
                return 1;
            }
        }

        string[] paths;
        try {
            paths = [.. Directory.EnumerateFiles(from, "*.md").Order(StringComparer.Ordinal)];
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            await Console.Error.WriteLineAsync($"Cannot list {from}: {ex.Message}");
            return 1;
        }

        var files      = new List<ClaudeMemoryFile>();
        var unreadable = new List<MemoryImportCandidate>();
        foreach (var path in paths) {
            var name = Path.GetFileName(path);
            if (name.Equals(ClaudeMemoryFile.IndexFileName, StringComparison.OrdinalIgnoreCase)) continue;

            try {
                files.Add(ClaudeMemoryFile.Parse(name, await File.ReadAllTextSharedAsync(path)));
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                unreadable.Add(MemoryImportCandidate.Skip(name, null, $"unreadable: {ex.Message}"));
            }
        }

        IReadOnlyList<MemoryImportCandidate> plan = [.. ClaudeMemoryImport.Plan(files), .. unreadable];
        var place = project is not null ? $"project {project}" : global ? "the whole org" : $"the repository at {placeDir}";
        await Console.Out.WriteLineAsync($"{plan.Count(c => c.SkipReason is null)} of {plan.Count} notes in {from} → {audience} audience, {place}");
        foreach (var c in plan) await Console.Out.WriteLineAsync(Row(c));

        if (flags.ContainsKey("--dry-run") || plan.All(c => c.SkipReason is not null)) return 0;

        if (!flags.ContainsKey("--yes") && !flags.ContainsKey("-y")) {
            await Console.Out.WriteAsync("Import? [y/N] ");
            if (!string.Equals((await Console.In.ReadLineAsync())?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) {
                await Console.Out.WriteLineAsync("Cancelled.");
                return 0;
            }
        }

        var baseUrl   = profiles.Resolution.ServerUrl!;
        var machineId = await ResolveMachineIdAsync();
        using var client = await http.ForCommandAsync();

        var failed = 0;
        foreach (var c in plan.Where(c => c.SkipReason is null)) {
            var saveArgs = new JsonObject {
                ["audience"]    = audience,
                ["slug"]        = c.Slug,
                ["description"] = c.Description,
                ["content"]     = c.Content,
                ["kind"]        = c.Kind,
                ["team"]        = flags.GetValueOrDefault("--team"),
                ["project"]     = project,
                ["global"]      = global
            };

            var body = McpMemoryServer.BuildSaveBody(saveArgs, repoHash, machineId);
            body["harness"] = "kcap-import";

            string? outcome;
            try {
                // A project home is left to the save's own check: a fetched row does not say which project it is homed at.
                outcome = audience == "org" && project is null
                       && await ExistingOrgMemoryAsync(client, baseUrl, c.Slug!, global ? null : repoHash, machineId) is { } existing
                    ? await UpdateExistingAsync(client, baseUrl, existing, c, flags.ContainsKey("--update"))
                    : await SaveAsync(client, baseUrl, body, c, flags.ContainsKey("--update"));
            } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) {
                outcome = $"failed ({ex.Message})";
            }

            if (outcome is null) {
                await Console.Error.WriteLineAsync(await AuthRejectionNotice.ForPersistentUnauthorizedAsync(tokens, profiles.Name, baseUrl, time));
                return 1;
            }

            if (outcome.StartsWith("failed", StringComparison.Ordinal)) failed++;
            await Console.Out.WriteLineAsync($"  {c.Slug}: {outcome}");
        }

        return failed == 0 ? 0 : 1;
    }

    /// <summary>The outcome line for one save, or null when the server refused the credential and nothing further
    /// can succeed.</summary>
    internal static async Task<string?> SaveAsync(HttpClient client, string baseUrl, JsonObject body, MemoryImportCandidate c, bool update) {
        using var saved = await client.PostAsync($"{baseUrl}/api/memories", Json(body));
        if (saved.StatusCode == HttpStatusCode.Unauthorized) return null;

        var text = await saved.Content.ReadAsStringAsync();
        if (!saved.IsSuccessStatusCode) return $"failed (HTTP {(int)saved.StatusCode}) {text}";

        var result   = JsonNode.Parse(text)?.AsObject();
        var memoryId = result?["memory"]?["memory_id"]?.GetValue<string>();

        if (result?["slug_conflict"]?.GetValue<bool>() == true) {
            // A save to org the caller may not make directly is checked against their own pool, so the slug can be held by an
            // unrelated personal memory that --update must not overwrite.
            var held  = result["memory"]?["audience"]?.GetValue<string>();
            var asked = body["audience"]?.GetValue<string>();
            if (update && held != asked) return $"exists as {(held is null ? "a memory of unknown audience" : $"a {held} memory")}, skipped (--update overwrites only an existing {asked} memory)";

            return memoryId is null && update
                ? "failed (the server's conflict answer named no memory)"
                : await UpdateExistingAsync(client, baseUrl, memoryId ?? "", c, update);
        }

        var line = new StringBuilder("created");
        if (result?["promotion_status"]?.GetValue<string>() == "pending_approval") line.Append(", org promotion pending approval");
        if (result?["near_duplicate"] is JsonObject dup)
            line.Append($", near-duplicate of {dup["slug"]?.GetValue<string>()} ({dup["similarity"]?.GetValue<double>():0.00})");

        return line.ToString();
    }

    /// <summary>Overwrites an existing memory with the note when <paramref name="update"/> is set, otherwise reports it.</summary>
    internal static async Task<string?> UpdateExistingAsync(HttpClient client, string baseUrl, string memoryId, MemoryImportCandidate c, bool update) {
        if (!update) return "exists, skipped (pass --update to overwrite it)";

        var changes = new JsonObject { ["description"] = c.Description, ["content"] = c.Content, ["kind"] = c.Kind };
        using var put = await client.PutAsync($"{baseUrl}/api/memories/{Uri.EscapeDataString(memoryId)}", Json(changes));
        if (put.StatusCode == HttpStatusCode.Unauthorized) return null;

        return put.IsSuccessStatusCode ? "updated" : $"failed (HTTP {(int)put.StatusCode}) {await put.Content.ReadAsStringAsync()}";
    }

    /// <summary>The id of an org memory already holding <paramref name="slug"/> at this repository, or org-wide when
    /// <paramref name="repoHash"/> is null. A save to org that the caller may not make directly is recorded as their own
    /// memory awaiting approval, so the save's own slug check never sees the org copy an earlier, approved import made, and
    /// a re-run would queue it a second time.</summary>
    internal static async Task<string?> ExistingOrgMemoryAsync(HttpClient client, string baseUrl, string slug, string? repoHash, string? machineId) {
        var query = new List<string>();
        if (repoHash is not null) query.Add($"repo={Uri.EscapeDataString(repoHash)}");
        if (machineId is not null) query.Add($"machine={Uri.EscapeDataString(machineId)}");

        var url = $"{baseUrl}/api/memories/{Uri.EscapeDataString(slug)}{(query.Count == 0 ? "" : "?" + string.Join("&", query))}";
        using var found = await client.GetAsync(url);
        if (!found.IsSuccessStatusCode) return null;

        var row = JsonNode.Parse(await found.Content.ReadAsStringAsync())?.AsObject();
        return row?["audience"]?.GetValue<string>() == "org" && row["repo_hash"]?.GetValue<string>() == repoHash
            ? row["memory_id"]?.GetValue<string>()
            : null;
    }

    /// <summary>The directory whose repository the notes are homed at, or null when that cannot be told. A Claude project
    /// folder belongs to the directory it was named after, and Claude's naming folds <c>/</c>, <c>.</c> and <c>-</c>
    /// together, so its name cannot be decoded back into a path: it is matched by encoding the working directory instead.
    /// Any other folder is homed at the working directory.</summary>
    internal static string? PlaceDir(string memoryDir, string workdir, ClaudePaths claude) {
        var memory     = Path.TrimEndingDirectorySeparator(Path.GetFullPath(memoryDir));
        var projectDir = Path.GetDirectoryName(memory);
        var projects   = Path.TrimEndingDirectorySeparator(Path.GetFullPath(claude.Projects));

        if (projectDir is null || Path.GetFileName(memory) != "memory"
         || !string.Equals(Path.GetDirectoryName(projectDir), projects, StringComparison.Ordinal))
            return workdir;

        var own = Path.TrimEndingDirectorySeparator(Path.GetFullPath(claude.ProjectDir(workdir)));
        return string.Equals(own, projectDir, StringComparison.Ordinal) ? workdir : null;
    }

    static string Row(MemoryImportCandidate c) {
        var action = c.SkipReason is null ? "save" : "skip";
        var detail = c.SkipReason ?? string.Join("; ", c.Notes);

        return $"  {action,-5} {c.Kind ?? "-",-11} {c.Slug ?? "-",-40} {c.FileName}{(detail.Length > 0 ? $" — {detail}" : "")}";
    }

    static StringContent Json(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    async Task<string?> ResolveMachineIdAsync() {
        try { return await MachineIdProvider.GetOrCreateAsync(config); } catch { return null; }
    }

    static Dictionary<string, string>? ParseFlags(string[] args, int from) {
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = from; i < args.Length; i++) {
            var arg = args[i];

            if (Switches.Contains(arg)) {
                flags[arg] = "";
            } else if (ValueFlags.Contains(arg)) {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) {
                    Fail($"{arg} needs a value.");
                    return null;
                }
                flags[arg] = args[++i];
            } else {
                Fail($"{arg} is not a flag of this command.");
                return null;
            }
        }

        return flags;
    }

    static int Fail(string? message) {
        if (message is not null) Console.Error.WriteLine(message);
        Console.Error.WriteLine(Usage);
        return 1;
    }
}
