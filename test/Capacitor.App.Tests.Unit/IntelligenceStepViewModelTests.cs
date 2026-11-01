using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup.Intelligence;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

/// <summary>The local edition's background intelligence page: the local agents it offers are the ticked harnesses this
/// machine can run headless, Next posts the answer and holds on a refusal, and not now stores nothing</summary>
public class IntelligenceStepViewModelTests {
    sealed class Harness {
        public readonly FakeKcapCli Cli = new();
        public bool Local = true;
        public HarnessId[] Ticked = [HarnessId.Claude, HarnessId.Codex, HarnessId.Cursor];
        public string[] Headless = ["claude", "codex"];
        public readonly IntelligenceStepViewModel Vm;

        public Harness() =>
            Vm = new IntelligenceStepViewModel(Cli, action => action(), () => Local, () => Ticked, _ => Task.FromResult(Headless));

        public async Task EnterAsync() {
            await Vm.OnEnterAsync(CancellationToken.None);
            await Vm.Loading;
        }

        public IntelligenceOption Option(string token) => Vm.Options.Single(o => o.Token == token);
    }

    [Test]
    public async Task Offers_each_ticked_harness_the_machine_runs_headless_then_a_provider_and_not_now() {
        var h = new Harness { Ticked = [HarnessId.Codex, HarnessId.Cursor] };
        await h.EnterAsync();

        await Assert.That(h.Vm.Options.Select(o => o.Token))
            .IsEquivalentTo(["codex", IntelligenceOption.ProviderToken, IntelligenceAnswer.NoneToken], CollectionOrdering.Matching);
        await Assert.That(h.Option("codex").IsSelected).IsTrue();
    }

    [Test]
    public async Task With_no_local_agent_to_offer_not_now_is_preselected() {
        var h = new Harness { Headless = [] };
        await h.EnterAsync();

        await Assert.That(h.Option(IntelligenceAnswer.NoneToken).IsSelected).IsTrue();
    }

    [Test]
    public async Task Off_the_local_path_the_page_passes_through_and_posts_nothing() {
        var h = new Harness { Local = false };

        await Assert.That(h.Vm.PassThrough).IsTrue();
        await Assert.That(await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None)).IsTrue();
        await Assert.That(h.Cli.IntelligenceCalls).IsEmpty();
    }

    [Test]
    public async Task Next_posts_the_local_agent_and_keeps_what_the_server_applied() {
        var h = new Harness();
        await h.EnterAsync();

        await Assert.That(await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None)).IsTrue();
        await Assert.That(h.Cli.IntelligenceCalls.Single().Answer.Harness).IsEqualTo("claude");
        await Assert.That(h.Cli.IntelligenceCalls.Single().ImportOutcomes).IsNull();
        await Assert.That(h.Vm.Applied!.ResolvesTitles).IsTrue();
    }

    [Test]
    public async Task A_provider_needs_a_key_and_leaves_an_untouched_default_model_to_the_server() {
        var h = new Harness();
        await h.EnterAsync();
        h.Option(IntelligenceOption.ProviderToken).IsSelected = true;
        h.Vm.SelectedVendor = IntelligenceVendor.OpenAI;

        await Assert.That(h.Vm.Model).IsEqualTo(IntelligenceVendor.OpenAI.DefaultModel);
        await Assert.That(h.Vm.CanContinue).IsFalse();

        h.Vm.ApiKey = "sk-typed";
        await Assert.That(h.Vm.CanContinue).IsTrue();
        await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);

        var call = h.Cli.IntelligenceCalls.Single();
        await Assert.That(call.Answer.Vendor).IsEqualTo(IntelligenceVendor.OpenAI);
        await Assert.That(call.Model).IsNull();
        await Assert.That(call.Key).IsEqualTo("sk-typed");
    }

    [Test]
    public async Task An_edited_model_is_sent() {
        var h = new Harness();
        await h.EnterAsync();
        h.Option(IntelligenceOption.ProviderToken).IsSelected = true;
        h.Vm.Model  = " claude-haiku-4-5 ";
        h.Vm.ApiKey = "sk";

        await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);

        await Assert.That(h.Cli.IntelligenceCalls.Single().Model).IsEqualTo("claude-haiku-4-5");
    }

    [Test]
    public async Task A_refusal_holds_the_page_with_the_servers_reason() {
        var h = new Harness();
        h.Cli.IntelligenceBehavior = _ => Task.FromResult<IntelligenceSetupOutcome>(new IntelligenceSetupOutcome.Refused("key_rejected: 401 invalid"));
        await h.EnterAsync();
        h.Option(IntelligenceOption.ProviderToken).IsSelected = true;
        h.Vm.ApiKey = "sk";

        await Assert.That(await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None)).IsFalse();
        await Assert.That(h.Vm.Error).IsEqualTo("The provider refused the key: 401 invalid");
        await Assert.That(h.Vm.Applied).IsNull();
    }

    [Test]
    public async Task An_unreachable_server_holds_the_page_too() {
        var h = new Harness();
        h.Cli.IntelligenceBehavior = _ => Task.FromResult<IntelligenceSetupOutcome>(new IntelligenceSetupOutcome.Failed("connection refused"));
        await h.EnterAsync();

        await Assert.That(await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None)).IsFalse();
        await Assert.That(h.Vm.Error).Contains("connection refused");
    }

    [Test]
    public async Task Not_now_and_skip_store_nothing() {
        var h = new Harness();
        await h.EnterAsync();
        h.Option(IntelligenceAnswer.NoneToken).IsSelected = true;

        await Assert.That(await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None)).IsTrue();
        await Assert.That(await h.Vm.CanLeaveAsync(WizardNavigation.Skip, CancellationToken.None)).IsTrue();
        await Assert.That(h.Cli.IntelligenceCalls).IsEmpty();
        await Assert.That(h.Vm.Applied).IsNull();
    }

    [Test]
    public async Task The_import_choice_re_posts_the_same_answer_with_it() {
        var h = new Harness();
        await h.EnterAsync();
        h.Option(IntelligenceOption.ProviderToken).IsSelected = true;
        h.Vm.SelectedVendor = IntelligenceVendor.Cohere;
        h.Vm.ApiKey = "co";
        await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);

        await h.Vm.ApplyImportOutcomesAsync(false, CancellationToken.None);

        await Assert.That(h.Cli.IntelligenceCalls[1]).IsEqualTo((IntelligenceAnswer.Provider(IntelligenceVendor.Cohere),
            (string?)null, (string?)"co", (bool?)false));
    }
}
