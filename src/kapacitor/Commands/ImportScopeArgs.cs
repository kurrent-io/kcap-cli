namespace kapacitor.Commands;

/// <summary>
/// Pure flag parser and resolver for `kapacitor history` scope selection.
/// Performs no I/O — current-repo lookup is the caller's job; the resolved
/// result is passed in via <see cref="ResolveInput.CurrentRepo"/>.
/// </summary>
public static class ImportScopeArgs {
    public sealed record ParsedFlags(
        bool    All,
        bool    Org,
        string? RepoArg,
        bool    Yes,
        bool    Private);

    public sealed record ResolveInput(
        ParsedFlags                         Flags,
        string                              ActiveProfile,
        bool                                IsInteractive,
        (string Owner, string Name)?        CurrentRepo);

    public sealed record ResolveResult(
        ImportScope? Scope,        // null => either picker needed or error
        bool         Yes,
        bool         Private,
        string?      Error);

    public static ParsedFlags ParseFlags(string[] args) {
        string? repo = null;
        var idx = Array.IndexOf(args, "--repo");
        if (idx >= 0 && idx + 1 < args.Length) repo = args[idx + 1];

        return new(
            All:     args.Contains("--all"),
            Org:     args.Contains("--org"),
            RepoArg: repo,
            Yes:     args.Contains("--yes") || args.Contains("-y"),
            Private: args.Contains("--private"));
    }

    public static ResolveResult Resolve(ResolveInput input) {
        var f = input.Flags;
        var count = (f.All ? 1 : 0) + (f.Org ? 1 : 0) + (f.RepoArg is null ? 0 : 1);

        if (count > 1) {
            return new(null, f.Yes, f.Private,
                "--all, --org, and --repo are mutually exclusive.");
        }

        if (count == 0) {
            if (input.IsInteractive) {
                return new(null, f.Yes, f.Private, Error: null);
            }
            return new(null, f.Yes, f.Private,
                "--all, --org, or --repo <owner/name> is required for non-interactive use.");
        }

        // A scope flag is set: enforce --yes for non-interactive runs.
        if (!input.IsInteractive && !f.Yes) {
            return new(null, f.Yes, f.Private,
                "--yes is required for non-interactive use.");
        }

        if (f.All) {
            return new(new ImportScope.All(), f.Yes, f.Private, null);
        }

        if (f.Org) {
            if (string.IsNullOrEmpty(input.ActiveProfile) || input.ActiveProfile == "default") {
                return new(null, f.Yes, f.Private,
                    "--org requires a tenant-bound profile. Run `kapacitor setup` first, or use --all / --repo <owner/name>.");
            }
            return new(new ImportScope.Org(input.ActiveProfile), f.Yes, f.Private, null);
        }

        // --repo <value>
        var value = f.RepoArg!;
        if (value is "." or "current") {
            if (input.CurrentRepo is null) {
                return new(null, f.Yes, f.Private,
                    "--repo . requires the current directory to be in a git repo with an origin remote.");
            }
            var (owner, name) = input.CurrentRepo.Value;
            return new(new ImportScope.Repo(owner, name), f.Yes, f.Private, null);
        }

        var parts = value.Split('/');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) {
            return new(null, f.Yes, f.Private,
                $"--repo expects owner/name (got '{value}').");
        }

        return new(new ImportScope.Repo(parts[0], parts[1]), f.Yes, f.Private, null);
    }
}
