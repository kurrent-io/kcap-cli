namespace kapacitor.Tests.Unit;

/// <summary>
/// Tests for <see cref="DaemonNameResolver"/> — the shared name-resolution
/// helper used by both the CLI supervisor and the daemon binary. Keeping
/// these paths in lockstep is critical: a mismatch would mean the CLI
/// checks one PID file while the daemon writes to a different one, and
/// the AI-78 PID-file guard would be bypassable again.
/// </summary>
[NotInParallel("KAPACITOR_DAEMON_NAME")]
public class DaemonNameResolverTests {
    static readonly string? OriginalEnv = Environment.GetEnvironmentVariable("KAPACITOR_DAEMON_NAME");

    static void Reset() => Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_NAME", OriginalEnv);

    [Test]
    public async Task Resolve_PrefersNameArg() {
        Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_NAME", null);

        try {
            var name = DaemonNameResolver.Resolve(["--name", "from-arg"], profileName: "from-profile");
            await Assert.That(name).IsEqualTo("from-arg");
        } finally {
            Reset();
        }
    }

    [Test]
    public async Task Resolve_FallsBackToProfile_WhenNoArgOrEnv() {
        Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_NAME", null);

        try {
            var name = DaemonNameResolver.Resolve([], profileName: "from-profile");
            await Assert.That(name).IsEqualTo("from-profile");
        } finally {
            Reset();
        }
    }

    [Test]
    public async Task Resolve_EnvVarOverridesProfile() {
        Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_NAME", "from-env");

        try {
            var name = DaemonNameResolver.Resolve([], profileName: "from-profile");
            await Assert.That(name).IsEqualTo("from-env");
        } finally {
            Reset();
        }
    }

    [Test]
    public async Task Resolve_EnvVarAlsoOverridesArg() {
        // Documented quirk inherited from the pre-AI-630 DaemonRunner.RunAsync
        // ordering: env var trumps everything so shell scripts can fan out
        // multiple daemons without rewriting argv.
        Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_NAME", "from-env");

        try {
            var name = DaemonNameResolver.Resolve(["--name", "from-arg"], profileName: "from-profile");
            await Assert.That(name).IsEqualTo("from-env");
        } finally {
            Reset();
        }
    }

    [Test]
    public async Task Resolve_FallsBackToUsername_WhenNothingProvided() {
        Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_NAME", null);

        try {
            var name = DaemonNameResolver.Resolve([]);
            // Should at minimum produce a non-empty fallback. The exact value
            // depends on the test runner environment, so just assert it's
            // sane.
            await Assert.That(name).IsNotEmpty();
        } finally {
            Reset();
        }
    }
}
