using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Claude;

[ParallelLimiter<SubprocessLimit>]
public class ClaudeIdentityProbeTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDir]  public required TempDir  Tmp  { get; init; }

    string EnvFile  => Tmp.PathTo("env.txt");
    string ArgsFile => Tmp.PathTo("args.txt");

    ClaudeIdentityProbe Probe(string stdout, int exit = 0) {
        Tmp.CreateExecutable("bin/claude", $$"""
            #!/bin/sh
            printf '%s' "${CLAUDE_CONFIG_DIR-<unset>}" > '{{EnvFile}}'
            printf '%s ' "$@" > '{{ArgsFile}}'
            printf '%s\n' '{{stdout}}'
            exit {{exit}}
            """);

        return new ClaudeIdentityProbe(TestHarnesses.Under(Home, BinaryProbe.Searching(Tmp.PathTo("bin"))), Home, TimeProvider.System);
    }

    VendorAccount Account(string directory) => new("a", HarnessId.Claude, Home.PathTo(directory), directory, DateTimeOffset.UnixEpoch);

    [Test]
    public async Task Signed_in_answer_reads_identity_and_plan() {
        Skip.When(OperatingSystem.IsWindows(), "the stub claude is a POSIX shell script");
        var probe = Probe("""{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","email":"a@example.com","orgId":"org-1","orgName":"Acme","subscriptionType":"max","configDirectory":"/x"}""");

        var result = await probe.ProbeAsync(Account(".claude-work"), CancellationToken.None);

        await Assert.That(result.IdentityError).IsNull();
        await Assert.That(result.SignIn).IsEqualTo(SignInStates.SignedIn);
        await Assert.That(result.Identity).IsEqualTo(new AccountIdentity("a@example.com", "org-1", "Acme", "max", null));
        await Assert.That(result.Limits).IsNull();
        await Assert.That(result.LimitsError).IsNull();
        await Assert.That(File.ReadAllText(ArgsFile)).IsEqualTo("auth status --json ");
    }

    [Test]
    public async Task Signed_out_answer_is_authoritative_even_with_a_non_zero_exit() {
        Skip.When(OperatingSystem.IsWindows(), "the stub claude is a POSIX shell script");
        var probe = Probe("""{"loggedIn":false,"authMethod":"none","apiProvider":"firstParty"}""", exit: 1);

        var result = await probe.ProbeAsync(Account(".claude-work"), CancellationToken.None);

        await Assert.That(result.IdentityError).IsNull();
        await Assert.That(result.SignIn).IsEqualTo(SignInStates.SignedOut);
        await Assert.That(result.Identity).IsNull();
    }

    [Test]
    public async Task Crash_without_json_is_an_error_and_no_sign_in() {
        Skip.When(OperatingSystem.IsWindows(), "the stub claude is a POSIX shell script");
        var probe = Probe("Segmentation fault", exit: 139);

        var result = await probe.ProbeAsync(Account(".claude-work"), CancellationToken.None);

        await Assert.That(result.IdentityError).IsNotNull();
        await Assert.That(result.SignIn).IsNull();
        await Assert.That(result.Identity).IsNull();
    }

    [Test]
    public async Task Missing_claude_is_an_error() {
        var probe = new ClaudeIdentityProbe(TestHarnesses.Under(Home), Home, TimeProvider.System);

        var result = await probe.ProbeAsync(Account(".claude-work"), CancellationToken.None);

        await Assert.That(result.IdentityError).Contains("claude");
        await Assert.That(result.SignIn).IsNull();
    }

    [Test]
    public async Task Config_dir_is_removed_for_the_default_directory_and_set_for_another() {
        Skip.When(OperatingSystem.IsWindows(), "the stub claude is a POSIX shell script");
        var probe = Probe("""{"loggedIn":true,"email":"a@example.com"}""");

        await probe.ProbeAsync(Account(".claude"), CancellationToken.None);
        await Assert.That(File.ReadAllText(EnvFile)).IsEqualTo("<unset>");

        await probe.ProbeAsync(Account(".claude-work"), CancellationToken.None);
        await Assert.That(File.ReadAllText(EnvFile)).IsEqualTo(Home.PathTo(".claude-work"));
    }
}
