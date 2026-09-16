using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Commands;

/// <summary>
/// One of the profile's path lists, plus the words its command speaks in. <c>kcap ignore</c> and
/// <c>kcap allow</c> differ only in which array they edit and how they phrase the result.
/// </summary>
/// <param name="Verb">The command name, as it appears in usage and in "Not in &lt;verb&gt; list".</param>
/// <param name="Adjective">Past participle for a listed path — "ignored", "allowed".</param>
/// <param name="Gerund">Sentence-leading form for a fresh entry — "Ignoring", "Allowing".</param>
/// <param name="EmptyNote">What an empty list means, for a list where that is not obvious. "No
/// allowed paths" reads like nothing is captured when it means the opposite, and emptying the allow
/// list widens capture to the whole machine — so both places that can report it say so.</param>
sealed record ProfilePathList(
    string                           Verb,
    string                           Adjective,
    string                           Gerund,
    Func<Profile, string[]>          Get,
    Func<Profile, string[], Profile> With,
    string?                          EmptyNote = null);

/// <summary>
/// Add / remove / list over one <see cref="ProfilePathList"/>. Entries are stored normalized, so
/// a path given as <c>~/dev</c>, <c>~/dev/</c>, or through a symlink lands as one entry and matches
/// the cwds the harnesses report.
/// </summary>
sealed class PathListEditor(ConfigRoot root, ProfileContext profiles, UserHome home, ProfilePathList list) {
    public async Task<int> HandleAsync(string[] args) {
        // args[0] == the verb; --help / -h is handled by the dispatcher in Program.cs.
        if (args.Length < 2) return Usage();

        switch (args[1]) {
            case "--list":
                return await List();
            case "--remove" when args.Length < 3:
                await Console.Error.WriteLineAsync($"Usage: kcap {list.Verb} --remove <path>");

                return 1;
            case "--remove":
                return await Remove(args[2]);
            default:
                return await Add(args[1]);
        }
    }

    async Task<int> Add(string path) {
        if (!TryNormalize(path, home, out var normalized, out var error)) {
            await Console.Error.WriteLineAsync($"Invalid path '{path}': {error}");

            return 1;
        }

        var (profileName, profile) = LoadActive();
        var before = list.Get(profile).Length;

        profile = ApplyAdd(list, profile, normalized, home);

        await ConfigMutator.MutateAsync(root, c => {
            var p = ApplyAdd(list, c.Profiles.GetValueOrDefault(profileName) ?? new Profile(), normalized, home);

            return c with { Profiles = new Dictionary<string, Profile>(c.Profiles) { [profileName] = p } };
        });

        if (list.Get(profile).Length == before) {
            await Console.Out.WriteLineAsync($"Already {list.Adjective}: {normalized} (profile: {profileName})");
        } else {
            await Console.Out.WriteLineAsync($"{list.Gerund}: {normalized} (profile: {profileName})");
        }

        return 0;
    }

    async Task<int> Remove(string path) {
        if (!TryNormalize(path, home, out var normalized, out var error)) {
            await Console.Error.WriteLineAsync($"Invalid path '{path}': {error}");

            return 1;
        }

        var (profileName, profile) = LoadActive();
        var before = list.Get(profile).Length;

        profile = ApplyRemove(list, profile, normalized, home);

        await ConfigMutator.MutateAsync(root, c => {
            var p = ApplyRemove(list, c.Profiles.GetValueOrDefault(profileName) ?? new Profile(), normalized, home);

            return c with { Profiles = new Dictionary<string, Profile>(c.Profiles) { [profileName] = p } };
        });

        if (list.Get(profile).Length == before) {
            await Console.Out.WriteLineAsync($"Not in {list.Verb} list: {normalized} (profile: {profileName})");
        } else {
            await Console.Out.WriteLineAsync($"Removed: {normalized} (profile: {profileName})");

            if (list.Get(profile).Length == 0 && list.EmptyNote is { } note)
                await Console.Out.WriteLineAsync($"The {list.Verb} list is now empty — {note}.");
        }

        return 0;
    }

    async Task<int> List() {
        var (profileName, profile) = LoadActive();
        var paths = list.Get(profile);

        if (paths.Length == 0) {
            await Console.Out.WriteLineAsync(list.EmptyNote is { } note
                ? $"No {list.Adjective} paths (profile: {profileName}) — {note}."
                : $"No {list.Adjective} paths (profile: {profileName}).");

            return 0;
        }

        await Console.Out.WriteLineAsync($"{char.ToUpperInvariant(list.Adjective[0])}{list.Adjective[1..]} paths (profile: {profileName}):");

        foreach (var p in paths)
            await Console.Out.WriteLineAsync($"  {p}");

        return 0;
    }

    static bool TryNormalize(string path, UserHome home, out string normalized, out string error) {
        try {
            var n = PathExclusion.Normalize(path, home);

            if (string.IsNullOrWhiteSpace(n)) {
                normalized = "";
                error      = "path is empty after normalization";

                return false;
            }

            normalized = n;
            error      = "";

            return true;
        } catch (Exception ex) {
            normalized = "";
            error      = ex.Message;

            return false;
        }
    }

    /// <summary>
    /// Pure: <paramref name="path"/> added to the list, normalized and deduped. Per-entry
    /// normalization is guarded so a hand-edited entry that Normalize rejects (null byte, etc.)
    /// doesn't crash the command.
    /// </summary>
    public static Profile ApplyAdd(ProfilePathList list, Profile profile, string path, UserHome home) {
        var normalized = PathExclusion.Normalize(path, home);
        var current    = list.Get(profile);

        if (current.Any(existing => SafeNormalize(existing, home) == normalized))
            return profile;

        return list.With(profile, [.. current, normalized]);
    }

    /// <summary>
    /// Pure: <paramref name="path"/> removed from the list. Per-entry normalization is guarded —
    /// non-normalizable entries are kept (skipped from the removal predicate) so a bad entry in the
    /// stored list doesn't crash the command.
    /// </summary>
    public static Profile ApplyRemove(ProfilePathList list, Profile profile, string path, UserHome home) {
        var normalized = PathExclusion.Normalize(path, home);
        var current    = list.Get(profile);

        var remaining = current
            .Where(existing => SafeNormalize(existing, home) != normalized)
            .ToArray();

        return remaining.Length == current.Length ? profile : list.With(profile, remaining);
    }

    static string? SafeNormalize(string entry, UserHome home) {
        try { return PathExclusion.Normalize(entry, home); } catch { return null; }
    }

    // The startup snapshot, not a re-read: the write goes through ConfigMutator, which re-reads
    // under its own lock anyway.
    (string ProfileName, Profile Profile) LoadActive() => (profiles.Name, profiles.Effective ?? new Profile());

    int Usage() {
        Console.Error.WriteLine($"Usage: kcap {list.Verb} <path>");
        Console.Error.WriteLine($"       kcap {list.Verb} --list");
        Console.Error.WriteLine($"       kcap {list.Verb} --remove <path>");

        return 1;
    }
}
