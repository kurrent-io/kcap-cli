using System.Globalization;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core;

/// <summary>
/// Finds the commits a transcript's shell steps show landing and places each in the repository that
/// holds it, asking git on this machine. The command alone cannot say where a commit went:
/// <c>cd src/cli &amp;&amp; git commit</c> runs outside the folder its transcript line records. A
/// failed or aborted commit is never observed.
/// </summary>
public sealed class CommitObserver(
        Func<string, string, TimeSpan, Task<string?>>          git,
        Func<string, Task<(string? Owner, string? RepoName)>>  placeRepo) {
    static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(3);
    const int RememberedCommands = 256;

    static readonly Regex SummaryRegex = new(
        @"^\[(?<branch>.+?) (?:\(root-commit\) )?(?<sha>[0-9a-f]{7,40})\] (?<subject>.*?)\r?$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    static readonly Regex CommitCommandRegex = new(@"\bgit\b.*\bcommit\b", RegexOptions.Compiled | RegexOptions.Singleline);

    // Folders a command moves into (`cd`, `pushd`) or runs git in (`git -C`), in order.
    static readonly Regex MoveRegex = new(
        @"(?:(?:^|&&|\|\||[;\n(])\s*(?<verb>cd|pushd)|\bgit\s+(?<verb>-C))\s+(?<dir>""[^""]+""|'[^']+'|[^\s;&|)]+)",
        RegexOptions.Compiled);

    static readonly Regex SubmoduleRegex = new(@"^[ +\-U]?[0-9a-f]+ (?<path>.+?)(?: \([^()]*\))?\r?$", RegexOptions.Compiled | RegexOptions.Multiline);

    delegate Task<string?> RunGit(string arguments, string dir);

    sealed record Call(string Command, DateTimeOffset? At);

    readonly Dictionary<string, Call> _calls = new(StringComparer.Ordinal);
    readonly Queue<string>            _order = new();

    /// <summary>Every commit these steps' results show landing. A step git cannot answer for yields
    /// nothing, so it never stalls the batch carrying it.</summary>
    public async Task<IReadOnlyList<ObservedCommit>> ObserveAsync(ShellSteps steps) {
        Recall(steps);
        if (steps.Cwd is not { } cwd || steps.Results.Count == 0) return [];

        try {
            return await ObserveResultsAsync(cwd, steps);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            return [];
        }
    }

    /// <summary>For steps before the resume point: a quiet commit is seen only when its call is known.</summary>
    public void Recall(ShellSteps steps) {
        foreach (var call in steps.Calls) Remember(call.Id, new Call(call.Command, steps.At));
    }

    async Task<List<ObservedCommit>> ObserveResultsAsync(string cwd, ShellSteps steps) {
        List<ObservedCommit> found = [];

        foreach (var result in steps.Results) {
            // A result whose call this observer never saw (a background run read back through
            // BashOutput) still counts.
            var call = _calls.GetValueOrDefault(result.CallId);
            if (call is not null && !CommitCommandRegex.IsMatch(call.Command)) continue;

            foreach (var sighting in Sightings(result, call, steps.At))
                if ((await FindAsync(sighting, cwd) ?? sighting.Unplaced) is { } commit)
                    found.Add(commit);
        }

        return found;
    }

    static IEnumerable<Sighting> Sightings(ShellSteps.Result result, Call? call, DateTimeOffset? at) {
        var summaries = SummaryRegex.Matches(result.Output);

        if (summaries.Count > 0)
            return summaries.Select(s => new Summary(call, s.Groups["sha"].Value, s.Groups["subject"].Value.Trim(), OnBranch(s.Groups["branch"].Value)));

        return call is { At: { } from } && at is { } to && !result.IsError ? [new QuietCommit(call, from, to)] : [];
    }

    async Task<ObservedCommit?> FindAsync(Sighting sighting, string cwd) {
        var tried = new HashSet<string>(StringComparer.Ordinal);

        foreach (var dir in CommandDirs(cwd, sighting.Command))
            if (tried.Add(dir) && await PlaceAsync(sighting, dir) is { } placed) return placed;

        // A submodule's commits are invisible to its parent's git. Listing them runs git once per
        // submodule, so only a miss everywhere else pays for it.
        foreach (var dir in await SubmoduleDirsAsync(cwd))
            if (tried.Add(dir) && await PlaceAsync(sighting, dir) is { } placed) return placed;

        return null;
    }

    async Task<ObservedCommit?> PlaceAsync(Sighting sighting, string dir) {
        if (!Directory.Exists(dir) || await sighting.FindInAsync(Git, dir) is not var (sha, subject)) return null;

        var top           = await Git("rev-parse --show-toplevel", dir) ?? dir;
        var (owner, repo) = await placeRepo(top);

        return new ObservedCommit {
            Sha      = sha,
            Owner    = owner,
            RepoName = repo,
            Branch   = await sighting.BranchInAsync(Git, dir),
            Message  = await Git($"log -1 --format=%B {sha}", dir) ?? subject,
        };
    }

    Task<string?> Git(string arguments, string dir) => git(arguments, dir, GitTimeout);

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
        if (!Directory.Exists(cwd) || await Git("rev-parse --show-toplevel", cwd) is not { } top
         || await Git("submodule status --recursive", top) is not { Length: > 0 } status) return [];

        return SubmoduleRegex.Matches(status).Select(entry => Path.GetFullPath(Path.Combine(top, entry.Groups["path"].Value)));
    }

    void Remember(string id, Call call) {
        if (!_calls.TryAdd(id, call)) return;

        _order.Enqueue(id);
        if (_order.Count > RememberedCommands) _calls.Remove(_order.Dequeue());
    }

    /// <summary>A commit a tool result shows landing, before git has found it. <see cref="Call"/> is
    /// null for a result whose call this observer never saw.</summary>
    abstract record Sighting(Call? Call) {
        public string Command => Call?.Command ?? "";

        /// <summary>The commit's full sha and subject, when <paramref name="dir"/>'s git holds it.</summary>
        public abstract Task<(string Sha, string Subject)?> FindInAsync(RunGit git, string dir);

        public abstract Task<string?> BranchInAsync(RunGit git, string dir);

        /// <summary>What the commit still reports when git finds it nowhere; null when nothing.</summary>
        public abstract ObservedCommit? Unplaced { get; }
    }

    /// <summary>git's <c>[branch sha] subject</c> line.</summary>
    sealed record Summary(Call? Call, string Sha, string Subject, string? Branch) : Sighting(Call) {
        public override async Task<(string Sha, string Subject)?> FindInAsync(RunGit git, string dir) {
            if (await git($"rev-parse --verify --quiet {Sha}^{{commit}}", dir) is not { Length: 40 } full) return null;

            // A short SHA can name another commit in the wrong repo; the subject settles it.
            return await git($"log -1 --format=%s {full}", dir) == Subject ? (full, Subject) : null;
        }

        public override Task<string?> BranchInAsync(RunGit git, string dir) => Task.FromResult(Branch);

        // With no call to vouch for it, only git's confirmation counts.
        public override ObservedCommit? Unplaced => Call is null ? null : new() { Sha = Sha, Branch = Branch, Message = Subject };
    }

    /// <summary>A commit command that printed no summary, such as <c>git commit -q</c>: the HEAD
    /// committed while it ran.</summary>
    sealed record QuietCommit(Call Call, DateTimeOffset From, DateTimeOffset To) : Sighting(Call) {
        // Committer time has one-second resolution.
        static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

        public override async Task<(string Sha, string Subject)?> FindInAsync(RunGit git, string dir) {
            if (await git("log -1 --format=%H%x20%ct%x20%s HEAD", dir) is not { } head
             || head.Split(' ', 3) is not [{ Length: 40 } sha, var seconds, { Length: > 0 } subject]
             || !long.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out var unix)) return null;

            var committed = DateTimeOffset.FromUnixTimeSeconds(unix);

            return committed >= From - Slack && committed <= To + Slack ? (sha, subject) : null;
        }

        public override async Task<string?> BranchInAsync(RunGit git, string dir) => OnBranch(await git("rev-parse --abbrev-ref HEAD", dir));

        public override ObservedCommit? Unplaced => null;
    }
}
