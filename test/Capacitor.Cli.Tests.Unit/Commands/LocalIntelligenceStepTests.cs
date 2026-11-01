using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Setup.Intelligence;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>Setup's background intelligence step: a flag answers it, a person is asked until the server takes an answer
/// or they choose not now, and a run that can do neither leaves it unset and says how to set it later</summary>
public class LocalIntelligenceStepTests {
    static readonly IntelligenceSetupResponse Took =
        new("local_agent", "claude", null, true, 200, ["titles", "outcomes"]);

    sealed class Script(params string[] choices) : IIntelligenceQuestions {
        readonly Queue<string> _choices = new(choices);

        public List<IReadOnlyList<string>> Offered { get; } = [];
        public string? TypedKey { get; init; } = "typed";
        public bool Outcomes { get; init; } = true;
        public int OutcomeQuestions { get; private set; }

        public string Choose(IReadOnlyList<string> harnesses) {
            Offered.Add(harnesses);

            return _choices.Dequeue();
        }

        public IntelligenceVendor Vendor() {
            var id = _choices.Dequeue();

            return IntelligenceVendor.All.Single(v => v.Id == id);
        }
        public string Model(IntelligenceVendor vendor) => _choices.Dequeue();
        public string? Key(IntelligenceVendor vendor) => TypedKey;

        public bool ImportOutcomes() {
            OutcomeQuestions++;

            return Outcomes;
        }
    }

    sealed class Server(params IntelligenceSetupOutcome[] outcomes) {
        readonly Queue<IntelligenceSetupOutcome> _outcomes = new(outcomes);

        public List<IntelligenceSetupRequest> Requests { get; } = [];

        public Task<IntelligenceSetupOutcome> Apply(IntelligenceSetupRequest request) {
            Requests.Add(request);

            return Task.FromResult(_outcomes.Dequeue());
        }
    }

    static LocalSetupArgs Args(string? answer = null, string? model = null, bool? outcomes = null) =>
        new(false, true, true, answer is not null, Intelligence: IntelligenceAnswer.Parse(answer), IntelligenceModel: model, ImportOutcomes: outcomes);

    static (LocalIntelligenceStep Step, List<string> Said) Step(Server server, IIntelligenceQuestions? questions, Func<string, string?>? env = null) {
        var said = new List<string>();

        return (new LocalIntelligenceStep(server.Apply, () => Task.FromResult<string[]>(["claude", "codex"]), env ?? (_ => null), questions, said.Add), said);
    }

    [Test]
    public async Task A_flag_answer_is_posted_without_asking() {
        var server = new Server(new IntelligenceSetupOutcome.Applied(Took));
        var (step, _) = Step(server, null);

        var applied = await step.RunAsync(Args("claude", outcomes: false));

        await Assert.That(applied).IsEqualTo(Took);
        await Assert.That(server.Requests.Single()).IsEqualTo(new IntelligenceSetupRequest("local_agent", "claude", ImportOutcomes: false));
    }

    [Test]
    public async Task A_flag_provider_takes_its_key_from_the_environment() {
        var server = new Server(new IntelligenceSetupOutcome.Applied(Took));
        var (step, _) = Step(server, null, name => name == "ANTHROPIC_API_KEY" ? "sk-env" : null);

        await step.RunAsync(Args("anthropic", model: "claude-haiku-4-5"));

        await Assert.That(server.Requests.Single()).IsEqualTo(
            new IntelligenceSetupRequest("provider", null, "anthropic", "claude-haiku-4-5", "sk-env"));
    }

    [Test]
    public async Task A_refused_flag_answer_is_reported_and_setup_carries_on() {
        var server = new Server(new IntelligenceSetupOutcome.Refused("key_rejected: 401"));
        var (step, said) = Step(server, null, _ => "sk");

        var applied = await step.RunAsync(Args("openai"));

        await Assert.That(applied).IsNull();
        await Assert.That(string.Join('\n', said)).Contains("The provider refused the key: 401");
        await Assert.That(string.Join('\n', said)).Contains("kcap local intelligence");
    }

    [Test]
    public async Task Nobody_to_ask_and_no_flag_leaves_it_unset() {
        var server = new Server();
        var (step, said) = Step(server, null);

        await Assert.That(await step.RunAsync(Args())).IsNull();
        await Assert.That(server.Requests).IsEmpty();
        await Assert.That(string.Join('\n', said)).Contains("kcap local intelligence");
    }

    [Test]
    public async Task Asked_the_local_agent_then_outcomes() {
        var server  = new Server(new IntelligenceSetupOutcome.Applied(Took));
        var script  = new Script("codex") { Outcomes = false };
        var (step, _) = Step(server, script);

        await step.RunAsync(Args());

        await Assert.That(script.Offered.Single()).IsEquivalentTo(["claude", "codex"]);
        await Assert.That(server.Requests.Single()).IsEqualTo(new IntelligenceSetupRequest("local_agent", "codex", ImportOutcomes: false));
    }

    [Test]
    public async Task Not_now_posts_nothing_and_asks_nothing_more() {
        var server = new Server();
        var script = new Script(IntelligenceAnswer.NoneToken);
        var (step, _) = Step(server, script);

        await Assert.That(await step.RunAsync(Args())).IsNull();
        await Assert.That(server.Requests).IsEmpty();
        await Assert.That(script.OutcomeQuestions).IsEqualTo(0);
    }

    [Test]
    public async Task A_refused_provider_is_asked_again_until_one_is_taken() {
        var server = new Server(new IntelligenceSetupOutcome.Refused("key_rejected: 401"), new IntelligenceSetupOutcome.Applied(Took));
        var script = new Script(LocalIntelligenceStep.ProviderChoice, "cohere", "command-r", "claude");
        var (step, said) = Step(server, script);

        var applied = await step.RunAsync(Args());

        await Assert.That(applied).IsEqualTo(Took);
        await Assert.That(server.Requests[0]).IsEqualTo(new IntelligenceSetupRequest("provider", null, "cohere", "command-r", "typed", true));
        await Assert.That(server.Requests[1].Harness).IsEqualTo("claude");
        await Assert.That(string.Join('\n', said)).Contains("The provider refused the key: 401");
    }

    [Test]
    public async Task An_outcome_flag_is_not_asked_again() {
        var server = new Server(new IntelligenceSetupOutcome.Applied(Took));
        var script = new Script("claude");
        var (step, _) = Step(server, script);

        await step.RunAsync(Args(outcomes: true) with { Intelligence = null });

        await Assert.That(script.OutcomeQuestions).IsEqualTo(0);
        await Assert.That(server.Requests.Single().ImportOutcomes == true).IsTrue();
    }

    [Test]
    public async Task An_unreachable_server_warns_and_moves_on() {
        var server = new Server(new IntelligenceSetupOutcome.Failed("connection refused"));
        var (step, said) = Step(server, new Script("claude"));

        await Assert.That(await step.RunAsync(Args())).IsNull();
        await Assert.That(string.Join('\n', said)).Contains("connection refused");
    }
}
