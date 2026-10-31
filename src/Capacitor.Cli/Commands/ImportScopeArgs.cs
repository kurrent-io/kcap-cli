namespace Capacitor.Cli.Commands;

/// <summary>
/// Pure flag parser and resolver for `kcap import` scope selection.
/// Performs no I/O — current-repo lookup is the caller's job; the resolved
/// result is passed in via <see cref="ResolveInput.CurrentRepo"/>.
/// </summary>
public static class ImportScopeArgs {
    public sealed record ParsedFlags(
            bool                  All,
            bool                  Org,
            IReadOnlyList<string> RepoArgs,
            bool                  Yes,
            bool                  Private,
            string?               OrgArg = null, // explicit owner after `--org`, or null for bare `--org`
            IReadOnlyList<string>? FolderArgs     = null,
            IReadOnlyList<string>? SkipRepoArgs   = null,
            IReadOnlyList<string>? SkipFolderArgs = null
        );

    public sealed record ResolveInput(
            ParsedFlags                  Flags,
            string                       ActiveProfile,
            bool                         IsInteractive,
            (string Owner, string Name)? CurrentRepo,
            string?                      StoredOrg = null // org remembered on the active profile
        );

    public sealed record ResolveResult(
            ImportScope? Scope, // null => picker needed, org-pick needed, or error
            bool         Yes,
            bool         Private,
            string?      Error,
            bool         NeedOrgPick = false, // bare `--org`, no value/stored org: pick from discovered repos
            ImportSkip?  Skip        = null   // --skip-repo / --skip-folder, whatever the scope
        );

    public static ParsedFlags ParseFlags(string[] args) {
        // `--org` may be bare or take an explicit owner. Only treat the next token
        // as the owner when it isn't another flag (so `--org --yes` stays bare).
        string? org    = null;
        var     orgIdx = Array.IndexOf(args, "--org");
        if (orgIdx >= 0 && orgIdx + 1 < args.Length && !args[orgIdx + 1].StartsWith('-')) org = args[orgIdx + 1];

        return new(
            All: args.Contains("--all"),
            Org: args.Contains("--org"),
            RepoArgs: Values(args, "--repo"),
            Yes: args.Contains("--yes") || args.Contains("-y"),
            Private: args.Contains("--private"),
            OrgArg: org,
            FolderArgs: Values(args, "--folder"),
            SkipRepoArgs: Values(args, "--skip-repo"),
            SkipFolderArgs: Values(args, "--skip-folder")
        );
    }

    /// <summary>Every value of a repeatable flag. A trailing flag, or one followed by another flag,
    /// records the empty string so the caller can say the value is missing: dropping it would let
    /// <c>--repo a/b --repo</c> import half of what was asked for and report nothing.</summary>
    internal static List<string> Values(string[] args, string flag) {
        var values = new List<string>();

        for (var i = 0; i < args.Length; i++) {
            if (args[i] != flag) continue;

            var value = i + 1 < args.Length ? args[i + 1] : "";

            values.Add(value.StartsWith('-') ? "" : value);
        }

        return values;
    }

    /// <summary>Parses <c>--skip-repo</c> and <c>--skip-folder</c>, or says which value is wrong. Each
    /// value must parse before any is used, as with <c>--repo</c>.</summary>
    public static (ImportSkip? Skip, string? Error) ParseSkip(
            IReadOnlyList<string> repoArgs, IReadOnlyList<string> folderArgs, string repoFlag = "--skip-repo",
            string folderFlag = "--skip-folder") {
        var repos = new List<(string Owner, string Name)>();

        foreach (var value in repoArgs) {
            if (OwnerName(value) is not { } repo) {
                return (null, value.Length == 0
                    ? $"{repoFlag} needs a value: pass `{repoFlag} <owner/name>`."
                    : $"{repoFlag} expects owner/name (got '{value}').");
            }

            repos.Add(repo);
        }

        if (folderArgs.Any(f => f.Length == 0)) return (null, $"{folderFlag} needs a value: pass `{folderFlag} <path>`.");

        return (repos.Count == 0 && folderArgs.Count == 0 ? ImportSkip.None : new ImportSkip(repos, folderArgs), null);
    }

    static (string Owner, string Name)? OwnerName(string value) {
        var parts = value.Split('/');

        return parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0 ? (parts[0], parts[1]) : null;
    }

    public static ResolveResult Resolve(ResolveInput input) {
        var (skip, skipError) = ParseSkip(input.Flags.SkipRepoArgs ?? [], input.Flags.SkipFolderArgs ?? []);

        if (skipError is not null) return new(null, input.Flags.Yes, input.Flags.Private, skipError);

        return ResolveScope(input) with { Skip = skip };
    }

    static ResolveResult ResolveScope(ResolveInput input) {
        var f       = input.Flags;
        var folders = f.FolderArgs ?? [];
        var count   = (f.All ? 1 : 0) + (f.Org ? 1 : 0) + (f.RepoArgs.Count == 0 && folders.Count == 0 ? 0 : 1);

        switch (count) {
            case > 1:
                return new(
                    null,
                    f.Yes,
                    f.Private,
                    "--all, --org, and --repo/--folder are mutually exclusive."
                );
            case 0 when input.IsInteractive:
                return new(null, f.Yes, f.Private, Error: null);
            case 0:
                return new(
                    null,
                    f.Yes,
                    f.Private,
                    "--all, --org, or --repo <owner/name> is required for non-interactive use."
                );
        }

        // A scope flag is set: enforce --yes for non-interactive runs.
        if (!input.IsInteractive && !f.Yes) {
            return new(
                null,
                f.Yes,
                f.Private,
                "--yes is required for non-interactive use."
            );
        }

        if (f.All) {
            return new(new ImportScope.All(), f.Yes, f.Private, null);
        }

        if (f.Org) {
            // Explicit `--org <owner>` wins. The owner is the GitHub org/owner to
            // match against each session's git-remote owner — NOT the profile name
            // (which under WorkOS is a tenant slug, not a GitHub org). Trim so a
            // padded value (e.g. "EventStore ") can't produce a scope that never
            // matches a detected owner.
            var org = (!string.IsNullOrWhiteSpace(f.OrgArg) ? f.OrgArg : input.StoredOrg)?.Trim();

            if (!string.IsNullOrEmpty(org)) {
                return new(new ImportScope.Org(org), f.Yes, f.Private, null);
            }

            // Bare `--org` with nothing remembered: pick an org from discovered repos
            // when interactive; otherwise we have no owner to scope on.
            if (input.IsInteractive) {
                return new(null, f.Yes, f.Private, Error: null, NeedOrgPick: true);
            }

            return new(
                null,
                f.Yes,
                f.Private,
                "--org needs an owner: pass `--org <owner>`, or run `kcap import --org` once interactively to choose and remember one."
            );
        }

        // Every value must parse before any is used: a malformed one is a usage error, and importing
        // the rest would act on a command the user did not write.
        var repos = new List<(string Owner, string Name)>();

        foreach (var value in f.RepoArgs) {
            if (value is "." or "current") {
                if (input.CurrentRepo is null) {
                    return new(
                        null,
                        f.Yes,
                        f.Private,
                        "--repo . requires the current directory to be in a git repo with an origin remote."
                    );
                }

                repos.Add(input.CurrentRepo.Value);

                continue;
            }

            if (value.Length == 0) {
                return new(null, f.Yes, f.Private, "--repo needs a value: pass `--repo <owner/name>`.");
            }

            var parts = value.Split('/');

            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) {
                return new(
                    null,
                    f.Yes,
                    f.Private,
                    $"--repo expects owner/name (got '{value}')."
                );
            }

            repos.Add((parts[0], parts[1]));
        }

        if (folders.Any(v => v.Length == 0)) {
            return new(null, f.Yes, f.Private, "--folder needs a value: pass `--folder <path>`.");
        }

        // `--repo . --repo owner/name` naming the same repo twice is one selection; the scope dedupes.
        return new(new ImportScope.Repo(repos, folders), f.Yes, f.Private, null);
    }
}
