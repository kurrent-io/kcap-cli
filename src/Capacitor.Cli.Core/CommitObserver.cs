using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core;

/// <summary>
/// Finds the commits a Claude transcript shows landing and places each in the repository that holds
/// it, asking git on this machine. The command alone cannot say where a commit went:
/// <c>cd src/cli &amp;&amp; git commit</c> runs outside the folder its transcript line records. A
/// commit is seen through git's <c>[branch sha] subject</c> summary, or, for a commit command that
/// printed none, as the HEAD committed while it ran; a failed or aborted one never is.
/// </summary>
public sealed class CommitObserver(
        Func<string, string, TimeSpan, Task<string?>>          git,
        Func<string, Task<(string? Owner, string? RepoName)>>  placeRepo) {
    static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(3);
    // Committer time has one-second resolution.
    static readonly TimeSpan CommitTimeSlack = TimeSpan.FromSeconds(2);
    const int RememberedCommands = 256;

    static readonly Regex SummaryRegex = new(
        @"^\[(?<branch>.+?) (?:\(root-commit\) )?(?<sha>[0-9a-f]{7,40})\] (?<subject>.*?)\r?$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    static readonly Regex CommitCommandRegex = new(@"\bgit\b.*\bcommit\b", RegexOptions.Compiled | RegexOptions.Singleline);

    // Folders a command moves into (`cd`, `pushd`) or runs git in (`git -C`), in order.
    static readonly Regex MoveRegex = new(
        @"(?:(?:^|&&|\|\||[;\n(])\s*(?<verb>cd|pushd)|\bgit\s+(?<verb>-C))\s+(?<dir>""[^""]+""|'[^']+'|[^\s;&|)]+)",
        RegexOptions.Compiled);

    sealed record Call(string Command, DateTimeOffset? At);

    readonly Dictionary<string, Call> _calls = new(StringComparer.Ordinal);
    readonly Queue<string>            _order = new();

    /// <summary>Every commit one transcript line's tool results show landing. A line the observer
    /// cannot read yields nothing, so it never stalls the batch carrying it.</summary>
    public async Task<IReadOnlyList<ObservedCommit>> ObserveAsync(string line) {
        if (!line.Contains("tool_use", StringComparison.Ordinal)) return [];

        try {
            return await ObserveLineAsync(line);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            return [];
        }
    }

    async Task<List<ObservedCommit>> ObserveLineAsync(string line) {
        using var doc  = JsonDocument.Parse(line);
        var       root = doc.RootElement;

        if (Prop(Prop(root, "message"), "content") is not { ValueKind: JsonValueKind.Array } content) return [];

        var cwd = Str(root, "cwd");
        DateTimeOffset? at = DateTimeOffset.TryParse(Str(root, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t
            : null;

        List<ObservedCommit> found = [];

        foreach (var block in content.EnumerateArray()) {
            var type = Str(block, "type");

            if (type == "tool_use" && Str(block, "name") is "Bash" && Str(block, "id") is { } id
             && Str(Prop(block, "input"), "command") is { } bash) {
                Remember(id, new Call(bash, at));
                continue;
            }

            if (type != "tool_result" || cwd is null || Str(block, "tool_use_id") is not { } resultOf) continue;

            // A result whose call this observer never saw (a watcher restart, a background run read back
            // through BashOutput) still counts, but only once git confirms the commit.
            var call = _calls.GetValueOrDefault(resultOf);
            if (call is not null && !CommitCommandRegex.IsMatch(call.Command)) continue;

            var command   = call?.Command ?? "";
            var summaries = SummaryRegex.Matches(ResultText(block));

            foreach (Match summary in summaries) {
                var sha     = summary.Groups["sha"].Value;
                var subject = summary.Groups["subject"].Value.Trim();
                var branch  = OnBranch(summary.Groups["branch"].Value);

                if (await FindAsync(cwd, command, dir => ShaInAsync(dir, sha, subject), dir => Task.FromResult(branch), subject) is { } placed)
                    found.Add(placed);
                else if (call is not null)
                    found.Add(new ObservedCommit { Sha = sha, Branch = branch, Message = subject });
            }

            // `git commit -q`, or output sent elsewhere: no summary, so the commit is the HEAD made during the call.
            if (summaries.Count == 0 && call?.At is { } from && at is { } to && !IsError(block)
             && await FindAsync(cwd, command, dir => HeadCommittedBetweenAsync(dir, from, to), BranchOfAsync, "") is { } quiet)
                found.Add(quiet);
        }

        return found;
    }

    async Task<ObservedCommit?> FindAsync(
            string cwd, string command, Func<string, Task<string?>> commitIn, Func<string, Task<string?>> branchIn, string fallbackMessage) {
        var tried = new HashSet<string>(StringComparer.Ordinal);

        foreach (var dir in CommandDirs(cwd, command))
            if (tried.Add(dir) && await PlaceAsync(dir) is { } placed) return placed;

        // A submodule's commits are invisible to its parent's git. Listing them runs git once per
        // submodule, so only a miss everywhere else pays for it.
        foreach (var dir in await SubmoduleDirsAsync(cwd))
            if (tried.Add(dir) && await PlaceAsync(dir) is { } placed) return placed;

        return null;

        async Task<ObservedCommit?> PlaceAsync(string dir) {
            if (!Directory.Exists(dir) || await commitIn(dir) is not { } full) return null;

            var top           = await git("rev-parse --show-toplevel", dir, GitTimeout) ?? dir;
            var (owner, repo) = await placeRepo(top);

            return new ObservedCommit {
                Sha      = full,
                Owner    = owner,
                RepoName = repo,
                Branch   = await branchIn(dir),
                Message  = await git($"log -1 --format=%B {full}", dir, GitTimeout) ?? fallbackMessage,
            };
        }
    }

    async Task<string?> ShaInAsync(string dir, string sha, string subject) {
        if (await git($"rev-parse --verify --quiet {sha}^{{commit}}", dir, GitTimeout) is not { Length: 40 } full) return null;

        // A short SHA can name another commit in the wrong repo; the subject settles it.
        return await git($"log -1 --format=%s {full}", dir, GitTimeout) == subject ? full : null;
    }

    async Task<string?> HeadCommittedBetweenAsync(string dir, DateTimeOffset from, DateTimeOffset to) {
        if (await git("log -1 --format=%H%x20%ct HEAD", dir, GitTimeout) is not { } head
         || head.Split(' ') is not [{ Length: 40 } full, var seconds]
         || !long.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out var unix)) return null;

        var committed = DateTimeOffset.FromUnixTimeSeconds(unix);

        return committed >= from - CommitTimeSlack && committed <= to + CommitTimeSlack ? full : null;
    }

    async Task<string?> BranchOfAsync(string dir) => OnBranch(await git("rev-parse --abbrev-ref HEAD", dir, GitTimeout));

    static string? OnBranch(string? branch) =>
        branch is null or "HEAD" || branch.StartsWith("detached HEAD", StringComparison.Ordinal) ? null : branch;

    // The folders the command named, latest first, then the line's own folder.
    static List<string> CommandDirs(string cwd, string command) {
        var dirs = new List<string>();
        var here = cwd;

        foreach (Match move in MoveRegex.Matches(command)) {
            var raw = move.Groups["dir"].Value.Trim('"', '\'');
            if (raw.StartsWith('$') || raw.StartsWith('~') || raw == "-" || raw.Contains('\0')) continue;

            var path = Path.GetFullPath(Path.Combine(here, raw));
            dirs.Insert(0, path);
            if (move.Groups["verb"].Value is not "-C") here = path;
        }

        dirs.Add(cwd);

        return dirs;
    }

    async Task<IEnumerable<string>> SubmoduleDirsAsync(string cwd) {
        if (!Directory.Exists(cwd) || await git("rev-parse --show-toplevel", cwd, GitTimeout) is not { } top
         || await git("submodule status --recursive", top, GitTimeout) is not { Length: > 0 } status) return [];

        return status.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Trim().Split(' '))
            .Where(parts => parts.Length >= 2)
            .Select(parts => Path.GetFullPath(Path.Combine(top, parts[1])));
    }

    void Remember(string id, Call call) {
        if (!_calls.TryAdd(id, call)) return;

        _order.Enqueue(id);
        if (_order.Count > RememberedCommands) _calls.Remove(_order.Dequeue());
    }

    static bool IsError(JsonElement result) => Prop(result, "is_error") is { ValueKind: JsonValueKind.True };

    static string ResultText(JsonElement result) {
        if (Prop(result, "content") is not { } content) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";

        return string.Join('\n', content.EnumerateArray().Select(part => Str(part, "text")).OfType<string>());
    }

    static JsonElement? Prop(JsonElement? element, string property) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(property, out var value) ? value : null;

    static string? Str(JsonElement? element, string property) =>
        Prop(element, property) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
}
