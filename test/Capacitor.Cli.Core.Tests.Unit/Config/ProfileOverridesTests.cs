using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Tests.Unit.Config;

/// <summary>
/// What the environment contributes to server selection, and what it takes for a variable to count
/// as set. Bare <c>[NotInParallel]</c>: both variables are process-global, and a concurrent peer
/// spawning a child would hand them on.
/// </summary>
public class ProfileOverridesTests {
    [Test, NotInParallel]
    [Arguments("https://server.example", "https://server.example")]
    [Arguments("", null)]
    [Arguments(null, null)]
    public async Task An_empty_url_names_no_server(string? raw, string? url) {
        using var _ = EnvScope.Exclusive(ProfileOverrides.UrlVar, raw);

        await Assert.That(ProfileOverrides.FromEnvironment().Url).IsEqualTo(url);
    }

    [Test, NotInParallel]
    [Arguments("work", "work")]
    [Arguments("", null)]
    [Arguments(null, null)]
    public async Task An_empty_profile_names_no_profile(string? raw, string? profile) {
        using var _ = EnvScope.Exclusive(ProfileOverrides.ProfileVar, raw);

        await Assert.That(ProfileOverrides.FromEnvironment().Profile).IsEqualTo(profile);
    }

    /// <summary>
    /// A value of blanks was set by someone, and <see cref="ProfileResolver"/> lets it win — so
    /// reading it as absent here would select a different server than the resolver's own rule does,
    /// and leave the remediation naming a variable it had already discarded.
    /// </summary>
    [Test, NotInParallel]
    public async Task A_url_of_blanks_is_still_a_url() {
        using var _ = EnvScope.Exclusive(ProfileOverrides.UrlVar, "  ");

        await Assert.That(ProfileOverrides.FromEnvironment().Url).IsEqualTo("  ");
    }

    /// <summary>The rule belongs to the type, not to <see cref="ProfileOverrides.FromEnvironment"/>:
    /// a caller that builds one directly — every test that pins resolution does — must get the same
    /// answer, or the null test each consumer makes stops meaning what the resolver means by it.</summary>
    [Test]
    public async Task An_empty_value_is_discarded_however_the_overrides_are_built() {
        await Assert.That(new ProfileOverrides("", "")).IsEqualTo(ProfileOverrides.None);
        await Assert.That(ProfileOverrides.None with { Url = "" }).IsEqualTo(ProfileOverrides.None);
    }

    /// <summary>The two are independently absent: a profile pinned into a service unit names no URL,
    /// and a URL override deliberately selects no profile at all.</summary>
    [Test, NotInParallel]
    public async Task Each_variable_is_read_on_its_own() {
        using var url     = EnvScope.Exclusive(ProfileOverrides.UrlVar, null);
        using var profile = EnvScope.Exclusive(ProfileOverrides.ProfileVar, "work");

        await Assert.That(ProfileOverrides.FromEnvironment()).IsEqualTo(new ProfileOverrides(null, "work"));
    }
}
