using Capacitor.App.Services.Mutation;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

/// A caller that cannot bind a canonical server must yield an
/// honest Refused("no_server_configured") WITHOUT constructing a MutationRequest — this is the one
/// shared guard every mutation-request boundary (DaemonLifecycleController, DaemonClientService's
/// injected start delegate) routes through.
public class MutationRequestFactoryTests {
    [Test]
    [Arguments(MutationVerb.Install, "old-name")]
    [Arguments(MutationVerb.Replace, " NEW-name ")]
    public async Task Invalid_retire_request_is_refused(MutationVerb verb, string oldName) {
        var refusal = MutationRequestFactory.TryBuild(verb, "work", "https://work.example", "new-name", out var request, oldName);
        await Assert.That(request).IsNull();
        await Assert.That(refusal).IsTypeOf<MutationOutcome.Refused>();
    }

    [Test]
    public async Task Valid_profile_and_server_builds_a_request() {
        var refusal = MutationRequestFactory.TryBuild(
            MutationVerb.StartVerified, "default", "https://kcap.example.com", "daemon-a", out var request);

        await Assert.That(refusal).IsNull();
        await Assert.That(request).IsNotNull();
        await Assert.That(request!.Verb).IsEqualTo(MutationVerb.StartVerified);
        await Assert.That(request.Profile).IsEqualTo("default");
        await Assert.That(request.CanonicalServer).IsEqualTo("https://kcap.example.com:443");
        await Assert.That(request.DaemonName).IsEqualTo("daemon-a");
    }

    [Test]
    public async Task Null_server_url_refuses_without_a_request() {
        var refusal = MutationRequestFactory.TryBuild(MutationVerb.DetachedStart, "default", null, "daemon-a", out var request);

        await Assert.That(request).IsNull();
        await Assert.That(refusal).IsTypeOf<MutationOutcome.Refused>();
        var refused = (MutationOutcome.Refused)refusal!;
        await Assert.That(refused.Reason).IsEqualTo("no_server_configured");
        await Assert.That(refused.Surface).IsEqualTo(RecoverySurface.Attention);
    }

    [Test]
    public async Task Non_canonicalizable_server_url_refuses_without_a_request() {
        // Carries a query string — ServerIdentity.Canonicalize rejects it outright.
        var refusal = MutationRequestFactory.TryBuild(
            MutationVerb.Install, "default", "https://kcap.example.com?tenant=a", "daemon-a", out var request);

        await Assert.That(request).IsNull();
        await Assert.That(refusal).IsTypeOf<MutationOutcome.Refused>();
        await Assert.That(((MutationOutcome.Refused)refusal!).Reason).IsEqualTo("no_server_configured");
    }

    [Test]
    public async Task Null_profile_name_refuses_even_with_a_valid_server() {
        var refusal = MutationRequestFactory.TryBuild(MutationVerb.Replace, null, "https://kcap.example.com", "daemon-a", out var request);

        await Assert.That(request).IsNull();
        await Assert.That(refusal).IsTypeOf<MutationOutcome.Refused>();
    }

    [Test]
    public async Task Whitespace_profile_name_refuses() {
        var refusal = MutationRequestFactory.TryBuild(MutationVerb.Replace, "   ", "https://kcap.example.com", "daemon-a", out var request);

        await Assert.That(request).IsNull();
        await Assert.That(refusal).IsTypeOf<MutationOutcome.Refused>();
    }

    [Test]
    public async Task Already_canonical_server_url_is_idempotent() {
        // The controller passes an already-canonicalized server (same value KcapCli itself
        // receives) — canonicalizing it again must be a no-op, not a second rejection.
        var refusal = MutationRequestFactory.TryBuild(
            MutationVerb.StartVerified, "default", "https://kcap.example.com:443", "daemon-a", out var request);

        await Assert.That(refusal).IsNull();
        await Assert.That(request!.CanonicalServer).IsEqualTo("https://kcap.example.com:443");
    }

    [Test]
    public async Task ForAccount_builds_without_a_server_and_mints_a_request_id_per_call() {
        var args = new AccountArgs(null, null, "abcd1234", "Work");

        var first  = MutationRequestFactory.ForAccount(MutationVerb.AccountRename, args, "daemon-a", null);
        var second = MutationRequestFactory.ForAccount(MutationVerb.AccountRename, args, "daemon-a", null);

        await Assert.That(first.Verb).IsEqualTo(MutationVerb.AccountRename);
        await Assert.That(first.Profile).IsEqualTo("");
        await Assert.That(first.CanonicalServer).IsEqualTo("");
        await Assert.That(first.DaemonName).IsEqualTo("daemon-a");
        await Assert.That(first.Account).IsEqualTo(args);
        await Assert.That(Guid.TryParseExact(first.RequestId, "N", out _)).IsTrue();
        await Assert.That(second.RequestId).IsNotEqualTo(first.RequestId);
        await Assert.That(second).IsNotEqualTo(first);
        await Assert.That(MutationRequestFactory.ForAccount(MutationVerb.AccountRename, args, "daemon-a", "work").Profile).IsEqualTo("work");
    }

    [Test]
    public async Task ForAccount_refuses_a_non_account_verb() {
        foreach (var verb in Enum.GetValues<MutationVerb>().Where(v => !v.IsAccountVerb()))
            await Assert.That(() => MutationRequestFactory.ForAccount(verb, new AccountArgs(null, null, "abcd1234", null), "daemon-a", "default"))
                .Throws<ArgumentOutOfRangeException>();
    }
}
