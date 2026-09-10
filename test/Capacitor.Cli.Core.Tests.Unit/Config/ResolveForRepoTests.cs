using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Tests.Unit.Config;

/// <summary>
/// The short-circuit that skips repo discovery when an explicit URL is given. Bare
/// <c>[NotInParallel]</c>: the repo is discovered from the working directory, which is
/// process-global.
/// </summary>
public class ResolveForRepoTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir]        public required TempDir        Repo   { get; init; }

    const string ActiveUrl = "https://active.example";
    const string PinnedUrl = "https://pinned.example";

    /// A repo pinned to a profile that is NOT the active one, so a resolution that skipped repo
    /// discovery answers a different server than one that did.
    async Task WriteRepoPinnedToAnotherProfile() {
        await ConfigMutator.MutateAsync(Config.Root, _ => new ProfileConfig {
            ActiveProfile = "active",
            Profiles = new() {
                ["active"] = new Profile { ServerUrl = ActiveUrl },
                ["pinned"] = new Profile { ServerUrl = PinnedUrl }
            }
        });
        Repo.CreateFile(".kcap.json", """{"profile":"pinned"}""");
    }

    async Task<string?> ResolveIn(string[] args, ProfileOverrides env) {
        var originalCwd = Environment.CurrentDirectory;
        try {
            Environment.CurrentDirectory = Repo.Path;

            return (await AppConfig.ResolveForRepo(args, Config.Root, env, gitTimeoutMs: 1000))
                   .Resolution.ServerUrl;
        } finally {
            Environment.CurrentDirectory = originalCwd;
        }
    }

    [Test, NotInParallel]
    public async Task Nothing_overridden_resolves_the_repos_own_profile() {
        await WriteRepoPinnedToAnotherProfile();

        await Assert.That(await ResolveIn([], ProfileOverrides.None)).IsEqualTo(PinnedUrl);
    }

    /// <summary>A named override outranks what the repo pins, so the profile named in
    /// <c>.kcap.json</c> loses to it. Says nothing about whether discovery ran — both paths feed the
    /// resolver the same override, and only the git probe tells them apart.</summary>
    [Test, NotInParallel]
    public async Task A_named_override_outranks_the_repos_own_profile() {
        await WriteRepoPinnedToAnotherProfile();

        await Assert.That(await ResolveIn([], new ProfileOverrides("https://env.example", null)))
                    .IsEqualTo("https://env.example");
    }

    /// <summary>An empty <c>--server-url</c> names no server: the resolver discards it and falls
    /// through, so withholding the repo inputs from that fall-through would silently answer the
    /// active profile instead of the one the repo pins.</summary>
    [Test, NotInParallel]
    public async Task An_empty_server_url_flag_still_resolves_the_repos_profile() {
        await WriteRepoPinnedToAnotherProfile();

        await Assert.That(await ResolveIn(["--server-url", ""], ProfileOverrides.None)).IsEqualTo(PinnedUrl);
    }
}
