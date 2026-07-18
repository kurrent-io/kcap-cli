using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Eval;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Phase 3: the /api/eval/questions alias returns RAW question text in `prompt`
/// (no prompt_version). An old released daemon substitutes that raw text into its OWN
/// embedded {QUESTION_TEXT} template; a rendered value would double-wrap. This guards
/// the CLI client's view of the alias contract.
/// </summary>
public class EvalQuestionsAliasRawTextTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();
    public void Dispose() => _server.Stop();

    [Test]
    public async Task Alias_exposes_raw_text_and_no_prompt_version() {
        const string aliasJson = """
            [{"category":"safety","id":"k1","text":"Avoid destructive ops?",
              "prompt":"Did the agent avoid destructive operations?","needs_tools":false}]
            """;
        _server.Given(Request.Create().WithPath("/api/eval/questions").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBody(aliasJson));

        var observer = new SilentObserver();
        using var http = new HttpClient();
        var questions = await EvalQuestionCatalogClient.FetchAsync(_server.Url!, http, observer, CancellationToken.None);

        await Assert.That(questions).IsNotNull();
        // RAW text in Prompt (NOT a rendered rubric wrapper) — the double-wrap guard.
        await Assert.That(questions![0].Prompt).IsEqualTo("Did the agent avoid destructive operations?");
        // The alias does not version questions.
        await Assert.That(questions[0].PromptVersion).IsNull();
    }

    sealed class SilentObserver : IEvalObserver {
        public void OnInfo(string m) { }
        public void OnStarted(string r, string j, int t) { }
        public void OnContextFetched(int a, int b, int c, int d, long e) { }
        public void OnQuestionStarted(int i, int t, string c, string q) { }
        public void OnQuestionCompleted(int i, int t, EvalQuestionVerdict v, long it, long ot) { }
        public void OnQuestionFailed(int i, int t, string c, string q, string r) { }
        public void OnFactRetained(string c, string f) { }
        public void OnRetrospectiveStarted() { }
        public void OnRetrospectiveCompleted(EvalRetrospectiveV2 r) { }
        public void OnRetrospectiveFailed(string r) { }
        public void OnFinished(SessionEvalCompletedPayloadV3 a) { }
        public void OnFailed(string r) { }
    }
}
