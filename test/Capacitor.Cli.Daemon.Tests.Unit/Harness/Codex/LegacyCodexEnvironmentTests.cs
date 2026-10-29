using Capacitor.Cli.Daemon.Harness.Codex;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

public class LegacyCodexEnvironmentTests {
    static Func<string, string?> Env(params (string Key, string Value)[] vars) =>
        key => vars.FirstOrDefault(v => v.Key == key).Value;

    [Test]
    public async Task Warning_is_null_when_no_removed_variable_is_set() =>
        await Assert.That(LegacyCodexEnvironment.Warning(Env(("KCAP_CODEX_PATH", "/opt/codex")))).IsNull();

    [Test]
    public async Task Warning_names_each_variable_set_and_the_command() {
        var warning = LegacyCodexEnvironment.Warning(Env(
            ("KCAP_CODEX_TRANSPORT", "app-server"), ("KCAP_CODEX_APPSERVER_INTERACTIVE", "1")));

        await Assert.That(warning).Contains("KCAP_CODEX_TRANSPORT");
        await Assert.That(warning).Contains("KCAP_CODEX_APPSERVER_INTERACTIVE");
        await Assert.That(warning).Contains("kcap experimental enable codex-app-server");
    }
}
