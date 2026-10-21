using Capacitor.Cli.LocalEdition;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;

/// <summary>An Ollama over WireMock. With nothing configured every path answers 404, which reads as no Ollama.</summary>
sealed class FakeOllama : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    public OllamaClient Client(TimeProvider? time = null) => new(new HttpClient(), new Uri(_server.Url!), time ?? TimeProvider.System);

    public string Url => _server.Url!;

    public FakeOllama Running(params string[] models) {
        _server.Given(Request.Create().WithPath("/api/version").UsingGet())
            .RespondWith(Response.Create().WithBody("""{"version":"0.12.3"}"""));

        var list = string.Join(",", models.Select(m => $$"""{"name":"{{m}}","model":"{{m}}"}"""));
        _server.Given(Request.Create().WithPath("/api/tags").UsingGet())
            .RespondWith(Response.Create().WithBody($$"""{"models":[{{list}}]}"""));

        return this;
    }

    /// <summary>Answers a pull with these NDJSON lines.</summary>
    public FakeOllama Pulls(params string[] lines) {
        _server.Given(Request.Create().WithPath("/api/pull").UsingPost())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/x-ndjson").WithBody(string.Join("\n", lines) + "\n"));

        return this;
    }

    public FakeOllama Embeds(int dimensions) {
        var vector = string.Join(",", Enumerable.Repeat("0.5", dimensions));
        _server.Given(Request.Create().WithPath("/api/embed").UsingPost())
            .RespondWith(Response.Create().WithBody($$"""{"model":"m","embeddings":[[{{vector}}]]}"""));

        return this;
    }

    public FakeOllama CannotEmbed(string error) {
        _server.Given(Request.Create().WithPath("/api/embed").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400).WithBody($$"""{"error":"{{error}}"}"""));

        return this;
    }

    public int Requests(string path) => _server.LogEntries.Count(e => e.RequestMessage.Path == path);

    public string? LastBody(string path) => _server.LogEntries.LastOrDefault(e => e.RequestMessage.Path == path)?.RequestMessage.Body;

    public void Dispose() => _server.Dispose();
}
