using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

internal sealed class LocalSetupArgsTests {
    [Test]
    [Arguments("--server-url", "https://acme.kcap.ai")]
    [Arguments("--server-url=https://acme.kcap.ai", null)]
    [Arguments("--discover", null)]
    [Arguments("--org", "Acme")]
    [Arguments("--slug", "acme")]
    [Arguments("--tier", "team")]
    [Arguments("--invite-code", "abc")]
    [Arguments("--device", null)]
    [Arguments("--github", null)]
    [Arguments("--muse-assistant", null)]
    public async Task A_conflicting_flag_is_refused_by_name(string flag, string? value) {
        string[] args = value is null ? ["setup", "--local", flag] : ["setup", "--local", flag, value];

        var (parsed, error) = LocalSetupArgs.Parse(args, kcapUrlEnv: null);

        await Assert.That(parsed).IsNull();
        await Assert.That(error).Contains(flag.Split('=')[0]);
    }

    [Test]
    public async Task A_positional_tenant_is_refused() {
        var (parsed, error) = LocalSetupArgs.Parse(["setup", "acme", "--local"], kcapUrlEnv: null);

        await Assert.That(parsed).IsNull();
        await Assert.That(error).Contains("acme");
    }

    [Test]
    public async Task A_set_KCAP_URL_is_refused() {
        var (parsed, error) = LocalSetupArgs.Parse(["setup", "--local"], kcapUrlEnv: "https://acme.kcap.ai");

        await Assert.That(parsed).IsNull();
        await Assert.That(error).Contains("KCAP_URL");
    }

    [Test]
    public async Task An_empty_KCAP_URL_is_not_a_conflict() {
        var (parsed, error) = LocalSetupArgs.Parse(["setup", "--local"], kcapUrlEnv: "");

        await Assert.That(error).IsNull();
        await Assert.That(parsed).IsEqualTo(new LocalSetupArgs(Json: false, SingleUser: false, Yes: false, NoPrompt: false));
    }

    [Test]
    public async Task Json_refuses_a_flag_that_only_the_terminal_steps_read() {
        var (parsed, error) = LocalSetupArgs.Parse(["setup", "--local", "--json", "--default-visibility", "private"], kcapUrlEnv: null);

        await Assert.That(parsed).IsNull();
        await Assert.That(error).Contains("--default-visibility");
    }

    [Test]
    public async Task Json_implies_no_prompt_and_accepts_its_own_flags() {
        var (parsed, error) = LocalSetupArgs.Parse(
            ["setup", "--local", "--json", "--single-user", "--yes", "--no-update-check"], kcapUrlEnv: null);

        await Assert.That(error).IsNull();
        await Assert.That(parsed).IsEqualTo(new LocalSetupArgs(Json: true, SingleUser: true, Yes: true, NoPrompt: true));
    }

    [Test]
    public async Task The_terminal_arm_keeps_the_later_steps_flags() {
        var (parsed, error) = LocalSetupArgs.Parse(
            ["setup", "--local", "--no-prompt", "--default-visibility", "private", "--skip-import"], kcapUrlEnv: null);

        await Assert.That(error).IsNull();
        await Assert.That(parsed).IsEqualTo(new LocalSetupArgs(Json: false, SingleUser: false, Yes: false, NoPrompt: true));
    }
}
