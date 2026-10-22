using System.Net;
using System.Text;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;

/// <summary>The local server as the installer reaches it. Fenced while the root's journal says so, as the real one
/// is; every request is appended to the shared log as <c>GET &lt;path&gt;</c>.</summary>
sealed class FakeLocalServer(List<string> log, LocalRoot root) : HttpMessageHandler {
    public HttpStatusCode Ready { get; set; } = HttpStatusCode.OK;

    public FakeAuthConfig WhileFenced { get; set; } = FakeAuthConfig.Pending;

    public FakeAuthConfig WhileAllowed { get; set; } = FakeAuthConfig.Lifted;

    /// <summary>Runs with the path after the request is logged and before it is answered.</summary>
    public Action<string>? OnRequest { get; set; }

    /// <summary>Answers a path itself when it returns a response, replacing the scripted answers.</summary>
    public Func<string, HttpResponseMessage?>? Override { get; set; }

    public Action<HttpRequestMessage>? OnCliSetupReport { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
        var path = request.RequestUri!.AbsolutePath;

        log.Add($"{request.Method} {path}");
        OnRequest?.Invoke(path);
        if (path == "/api/users/me/cli-setup") OnCliSetupReport?.Invoke(request);

        if (Override?.Invoke(path) is { } custom) return Task.FromResult(custom);

        return Task.FromResult(path switch {
            "/healthz/ready" => new HttpResponseMessage(Ready),
            "/auth/config"   => AuthConfig(Fenced() ? WhileFenced : WhileAllowed),
            _                => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
    }

    bool Fenced() => LocalStateFiles.ReadJournal(root) is StateRead<LifecycleJournalEntry>.Present { Value.ServerStart: not ServerStart.Allow };

    static HttpResponseMessage AuthConfig(FakeAuthConfig answer) {
        if (answer == FakeAuthConfig.Forbidden) return new(HttpStatusCode.Forbidden);

        var response = new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent("""{"provider":"None","server_url":"http://localhost"}""", Encoding.UTF8, "application/json"),
        };

        if (answer == FakeAuthConfig.Pending) response.Headers.Add(LocalServerProbe.PendingHeader, "pending");

        return response;
    }
}
