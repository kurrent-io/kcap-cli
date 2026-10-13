using System.Reflection;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class MuseAssistantPostureTests {
    static CodingAgentsStep.Options Permissive() =>
        new(SkipClaude: false, SkipCodex: false, SkipCursor: false, SkipCopilot: false, NoPrompt: false, InstallAgents: true);

    /// <summary>Every <c>Skip*</c> option, present and future, is on: a vendor added to the step
    /// without being added to the posture would otherwise get its files written on a Muse VM.</summary>
    [Test]
    public async Task Apply_sets_every_skip_option() {
        var applied = MuseAssistantPosture.Apply(Permissive());

        var skips = typeof(CodingAgentsStep.Options).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(bool) && p.Name.StartsWith("Skip", StringComparison.Ordinal))
            .ToList();

        await Assert.That(skips.Count).IsGreaterThan(20);

        foreach (var skip in skips)
            await Assert.That((bool)skip.GetValue(applied)!).IsTrue().Because($"{skip.Name} must be skipped on Muse");
    }

    [Test]
    public async Task Apply_turns_installs_off_and_prompts_off() {
        var applied = MuseAssistantPosture.Apply(Permissive());

        await Assert.That(applied.InstallAgents).IsFalse();
        await Assert.That(applied.NoPrompt).IsTrue();
    }

    [Test]
    public async Task HostOf_reads_a_full_url_a_schemeless_host_and_a_url_with_a_path() {
        await Assert.That(MuseAssistantPosture.HostOf("https://acme.kcap.ai")).IsEqualTo("acme.kcap.ai");
        await Assert.That(MuseAssistantPosture.HostOf("acme.kcap.ai")).IsEqualTo("acme.kcap.ai");
        await Assert.That(MuseAssistantPosture.HostOf("http://localhost:5000/")).IsEqualTo("localhost");
        await Assert.That(MuseAssistantPosture.HostOf("https://signin.kcap.ai/oauth2/token")).IsEqualTo("signin.kcap.ai");
    }

    [Test]
    public async Task ApprovalHosts_lists_the_server_first_then_the_fixed_hosts_once() {
        var hosts = MuseAssistantPosture.ApprovalHosts("https://capacitor.example.internal", AuthEndpoints.Defaults);

        await Assert.That(hosts[0]).IsEqualTo("capacitor.example.internal");
        await Assert.That(hosts).Contains("phog.kurrent.io");
        await Assert.That(hosts).Contains("auth.kcap.ai");
        await Assert.That(hosts).Contains("signin.kcap.ai");
        await Assert.That(hosts.Distinct().Count()).IsEqualTo(hosts.Count);
    }

    [Test]
    public async Task ApprovalHosts_does_not_repeat_a_server_that_is_also_a_fixed_host() {
        var hosts = MuseAssistantPosture.ApprovalHosts("https://signin.kcap.ai", AuthEndpoints.Defaults);

        await Assert.That(hosts.Count(h => h == "signin.kcap.ai")).IsEqualTo(1);
    }

    [Test]
    public async Task ArgumentError_names_the_flag_and_the_tenant_when_none_was_given() {
        var error = MuseAssistantPosture.ArgumentError(tenantGiven: false);

        await Assert.That(error).IsNotNull();
        await Assert.That(error!).Contains("--muse-assistant");
        await Assert.That(error!).Contains("<tenant>");
        await Assert.That(MuseAssistantPosture.ArgumentError(tenantGiven: true)).IsNull();
    }

    [Test]
    public async Task The_notice_names_every_host_and_the_card_choice() {
        var lines = MuseAssistantPosture.HostNoticeLines(["acme.kcap.ai", "phog.kurrent.io"]).ToList();
        var text  = string.Join("\n", lines);

        await Assert.That(text).Contains("acme.kcap.ai");
        await Assert.That(text).Contains("phog.kurrent.io");
        await Assert.That(text).Contains("Always allow this site");
    }

    [Test]
    public async Task The_unreachable_hint_reads_the_failure_as_not_yet_approved() {
        var text = string.Join("\n", MuseAssistantPosture.UnreachableHintLines(["acme.kcap.ai"]));

        await Assert.That(text).Contains("not yet approved");
        await Assert.That(text).Contains("acme.kcap.ai");
        await Assert.That(text).Contains("run the same command again");
    }
}
