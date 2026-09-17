using System.Runtime.InteropServices;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Commands;

/// <summary>
/// CLI surface for managing <see cref="ProfileConfig.CwdRemap"/> — the path
/// rewrites that <c>kcap import</c> applies before repository detection, so
/// historic transcripts referencing since-renamed or since-deleted local repo
/// directories can still match an <c>--org</c>/<c>--repo</c> scope. A
/// <c>from</c> is a literal prefix or carries one <c>*</c> segment; either way
/// it is stored and matched verbatim, so <c>--list</c> and <c>--remove</c>
/// name the pattern as typed.
///
/// Entries are stored at the top of <c>~/.config/kcap/config.json</c>
/// (global, not per-profile) — the same rename affects every profile's
/// import. <c>~</c> is kept literal in storage and expanded at apply time
/// by <see cref="CwdRemapper"/>.
/// </summary>
public sealed class RemapCommand(ConfigRoot root) {
    /// <summary>
    /// Mirror <see cref="CwdRemapper"/>'s policy so a stored entry and the
    /// argument typed at the CLI compare with the same case sensitivity that
    /// the import-time matcher uses. Otherwise a user on Windows could type
    /// <c>~/Dev/Foo</c> once and <c>~/dev/foo</c> later and end up with two
    /// stored rules that both fire at import time.
    /// </summary>
    static readonly StringComparison FromComparison =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public async Task<int> HandleAsync(string[] args) {
        // args[0] == "remap"; --help / -h is handled by the dispatcher in Program.cs.
        if (args.Length < 2) return Usage();

        switch (args[1]) {
            case "--list":
                return await List();
            case "--remove" when args.Length < 3:
                await Console.Error.WriteLineAsync("Usage: kcap remap --remove <from>");

                return 1;
            case "--remove":
                return args.Length > 3
                    ? await TooManyArguments("--remove takes exactly one path")
                    : await Remove(args[2]);
            default:
                if (args.Length < 3) return Usage();

                return args.Length > 3
                    ? await TooManyArguments("takes exactly two paths")
                    : await Add(args[1], args[2]);
        }
    }

    async Task<int> Add(string from, string to) {
        if (!TryNormalizeFrom(from, out var nFrom, out var fromError)) {
            await Console.Error.WriteLineAsync($"Invalid from path '{from}': {fromError}");

            return 1;
        }

        if (!TryNormalize(to, out var nTo, out var toError)) {
            await Console.Error.WriteLineAsync($"Invalid to path '{to}': {toError}");

            return 1;
        }

        if (nTo.Contains('*')) {
            await Console.Error.WriteLineAsync(
                $"Invalid to path '{to}': '*' matches in the from path only; the to path is literal");

            return 1;
        }

        var replaced = false;

        await ConfigMutator.MutateAsync(root, c => {
            (var next, replaced) = ApplyAdd(c.CwdRemap, nFrom, nTo);

            return c with { CwdRemap = next };
        });

        await Console.Out.WriteLineAsync(replaced
            ? $"Updated remap: {nFrom} → {nTo}"
            : $"Added remap: {nFrom} → {nTo}");

        return 0;
    }

    // Removal is keyed by the stored string, and deliberately skips the
    // wildcard grammar: an entry hand-written into the config that no import
    // can use still has to be removable through the CLI.
    async Task<int> Remove(string from) {
        if (!TryNormalize(from, out var nFrom, out var error)) {
            await Console.Error.WriteLineAsync($"Invalid from path '{from}': {error}");

            return 1;
        }

        var config = await AppConfig.LoadProfileConfig(root);
        var next = ApplyRemove(config.CwdRemap, nFrom);

        if (next.Length == (config.CwdRemap?.Length ?? 0)) {
            await Console.Out.WriteLineAsync($"Not in remap list: {nFrom}");

            return 0;
        }

        await ConfigMutator.MutateAsync(root, c => c with { CwdRemap = ApplyRemove(c.CwdRemap, nFrom) });
        await Console.Out.WriteLineAsync($"Removed remap: {nFrom}");

        return 0;
    }

    async Task<int> List() {
        var config = await AppConfig.LoadProfileConfig(root);
        var rules  = config.CwdRemap ?? [];

        if (rules.Length == 0) {
            await Console.Out.WriteLineAsync("No remap entries.");

            return 0;
        }

        await Console.Out.WriteLineAsync("Cwd remaps:");

        foreach (var r in rules) {
            await Console.Out.WriteLineAsync($"  {r.From} → {r.To}");
        }

        return 0;
    }

    /// <summary>
    /// Pure: add or replace a remap entry keyed by normalized <c>from</c>.
    /// Returns the new array and a flag indicating whether an existing entry
    /// was overwritten. Exposed for testing.
    /// </summary>
    public static (CwdRemap[] Next, bool Replaced) ApplyAdd(CwdRemap[]? current, string from, string to) =>
        ApplyAdd(current, from, to, FromComparison);

    // Internal seam for tests that need to pin the comparison policy
    // regardless of host OS.
    internal static (CwdRemap[] Next, bool Replaced) ApplyAdd(CwdRemap[]? current, string from, string to, StringComparison comparison) {
        var arr      = current ?? [];
        var next     = new CwdRemap[arr.Length];
        var replaced = false;

        for (var i = 0; i < arr.Length; i++) {
            if (SameFrom(arr[i].From, from, comparison)) {
                next[i]  = new() { From = from, To = to };
                replaced = true;
            } else {
                next[i] = arr[i];
            }
        }

        return replaced
            ? (next, true)
            : ([..arr, new CwdRemap { From = from, To = to }], false);
    }

    /// <summary>
    /// Pure: drop the entry whose normalized <c>from</c> equals
    /// <paramref name="from"/>. Returns the unchanged array if no match.
    /// Exposed for testing.
    /// </summary>
    public static CwdRemap[] ApplyRemove(CwdRemap[]? current, string from) =>
        ApplyRemove(current, from, FromComparison);

    // Internal seam for tests that need to pin the comparison policy
    // regardless of host OS.
    internal static CwdRemap[] ApplyRemove(CwdRemap[]? current, string from, StringComparison comparison) {
        var arr = current ?? [];

        return arr.Where(r => !SameFrom(r.From, from, comparison)).ToArray();
    }

    static bool SameFrom(string stored, string input, StringComparison comparison) =>
        string.Equals(Normalize(stored), Normalize(input), comparison);

    /// <summary>
    /// <see cref="TryNormalize"/> plus the wildcard grammar, so a pattern that
    /// could never match is rejected at the point it is typed rather than
    /// silently ignored by every import that reads it back.
    /// </summary>
    internal static bool TryNormalizeFrom(string path, out string normalized, out string error) {
        if (!TryNormalize(path, out normalized, out error)) return false;
        if (CwdRemapper.TryParseFrom(normalized, out _, out error)) return true;

        normalized = "";

        return false;
    }

    static bool TryNormalize(string path, out string normalized, out string error) {
        var n = Normalize(path);

        if (string.IsNullOrEmpty(n)) {
            normalized = "";
            error      = "path is empty";

            return false;
        }

        normalized = n;
        error      = "";

        return true;
    }

    /// <summary>
    /// Trim whitespace and trailing path separators (both <c>/</c> and <c>\</c>),
    /// so users adding the same prefix with or without a trailing slash hit the
    /// same stored entry. Otherwise stored as-is — <c>~</c> stays literal so
    /// the value remains portable across users / hosts, and is expanded at
    /// apply time by <see cref="CwdRemapper"/>.
    /// </summary>
    internal static string Normalize(string? path) {
        if (string.IsNullOrWhiteSpace(path)) return "";

        var trimmed = path.Trim();
        // Preserve a single leading "/" or "\" — only strip trailing separators.
        var end = trimmed.Length;
        while (end > 1 && (trimmed[end - 1] == '/' || trimmed[end - 1] == '\\')) end--;

        return trimmed[..end];
    }

    /// <summary>
    /// The extra arguments are usually a pattern the shell expanded, in which
    /// case no <c>*</c> survives into <c>args</c> to detect it by — so the
    /// quoting advice is offered rather than diagnosed. Taking the first two
    /// arguments anyway would silently store, or remove, one worktree's rule.
    /// </summary>
    static async Task<int> TooManyArguments(string expected) {
        await Console.Error.WriteLineAsync($"Too many arguments: kcap remap {expected}.");
        await Console.Error.WriteLineAsync("If you meant a wildcard pattern, quote it so the shell doesn't expand it:");
        await Console.Error.WriteLineAsync("  kcap remap '~/dev/repo/worktrees/*' ~/dev/repo");

        return 1;
    }

    static int Usage() {
        Console.Error.WriteLine("Usage: kcap remap <from> <to>");
        Console.Error.WriteLine("       kcap remap --list");
        Console.Error.WriteLine("       kcap remap --remove <from>");
        Console.Error.WriteLine();
        Console.Error.WriteLine("<from> may carry one '*' standing for a single path segment:");
        Console.Error.WriteLine("       kcap remap '~/dev/repo/worktrees/*' ~/dev/repo");

        return 1;
    }
}
