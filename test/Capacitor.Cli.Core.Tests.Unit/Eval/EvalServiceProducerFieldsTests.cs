using Capacitor.Cli.Core.Eval;

namespace Capacitor.Cli.Core.Tests.Unit.Eval;

/// <summary>A verdict that carries a reported obligations array or strategy stamps still parses, and none of them is taken from
/// the judge: the producer stamps and reconciles those fields itself.</summary>
public class EvalServiceProducerFieldsTests {
    static readonly EvalQuestionDto Question = new() { Category = "plan_adherence", Id = "completed_items", Text = "t", Prompt = "p" };

    [Test]
    public async Task A_verdict_carrying_reported_obligations_and_stamps_parses_without_them() {
        const string reply = """
            {"category":"plan_adherence","question_id":"completed_items","outcome":"assessed","score":4,"verdict":"pass","finding":"ok","evidence":null,"recommendation":null,"retain_fact":null,
             "strategy":"judge-said","strategy_version":"v9","obligations":[{"title":"t","origin":"plan","status":"verified","anchor":"o3.1","citations":["o3.2"]}]}
            """;

        var parsed = EvalService.ParseVerdict(reply, Question);

        await Assert.That(parsed).IsNotNull();
        await Assert.That(parsed!.Score).IsEqualTo(4);
        await Assert.That(parsed.Obligations).IsNull();
        await Assert.That(parsed.Strategy).IsNull();
        await Assert.That(parsed.StrategyVersion).IsNull();
    }
}
