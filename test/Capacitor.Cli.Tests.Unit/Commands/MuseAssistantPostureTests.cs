using System.Reflection;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Auth;
using TUnit.Assertions.Enums;

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
    public async Task ApprovalHosts_lists_a_self_hosted_server_first_then_the_telemetry_host() {
        var hosts = MuseAssistantPosture.ApprovalHosts("https://capacitor.example.internal");

        await Assert.That(hosts).IsEquivalentTo(["capacitor.example.internal", "phog.kurrent.io"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ApprovalHosts_does_not_repeat_a_server_that_is_also_the_telemetry_host() {
        var hosts = MuseAssistantPosture.ApprovalHosts("https://phog.kurrent.io");

        await Assert.That(hosts.Count(h => h == "phog.kurrent.io")).IsEqualTo(1);
    }

    [Test]
    public async Task SignInHosts_names_the_hosts_each_providers_device_sign_in_contacts() {
        await Assert.That(MuseAssistantPosture.SignInHosts(AuthProvider.WorkOS)).IsEquivalentTo(["api.workos.com", "signin.kcap.ai"]);
        await Assert.That(MuseAssistantPosture.SignInHosts(AuthProvider.GitHubApp)).IsEquivalentTo(["github.com"]);
        await Assert.That(MuseAssistantPosture.SignInHosts(AuthProvider.None)).IsEmpty();
    }

    [Test]
    public async Task The_sign_in_notice_is_silent_when_sign_in_contacts_nothing() {
        await Assert.That(MuseAssistantPosture.SignInNoticeLines([]).ToList()).IsEmpty();

        var text = string.Join("\n", MuseAssistantPosture.SignInNoticeLines(["api.workos.com"]));
        await Assert.That(text).Contains("api.workos.com");
    }

    /// <summary>A declined or expired code fails the same way as an unapproved host, so the hint must
    /// not assert the host is the cause.</summary>
    [Test]
    public async Task The_login_failure_hint_covers_a_declined_code_without_blaming_approval() {
        var denied = new AuthResult.Failed("WorkOS sign-in did not complete.", AuthFailureReason.SigninDenied);
        var text   = string.Join("\n", MuseAssistantPosture.LoginFailedHintLines(denied, AuthProvider.WorkOS));

        await Assert.That(text).Contains("api.workos.com");
        await Assert.That(text).Contains("expired or was declined");
        await Assert.That(text).Contains("run the same command again");
        await Assert.That(text).DoesNotContain("not yet approved");
    }

    /// <summary>A failure a re-run cannot fix keeps only its own remedy.</summary>
    [Test]
    public async Task The_login_failure_hint_is_silent_for_a_failure_a_re_run_cannot_fix() {
        AuthResult[] others = [
            new AuthResult.Failed("Unknown auth provider 'x'"),
            new AuthResult.Failed("Token exchange failed."),
            new AuthResult.Cancelled(),
        ];

        foreach (var result in others)
            await Assert.That(MuseAssistantPosture.LoginFailedHintLines(result, AuthProvider.WorkOS).ToList()).IsEmpty();
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
