namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// Hands every client the same handler. The handler outlives the clients the code under test
/// disposes, so a test can still read what it recorded.
internal sealed class HandlerFactory(HttpMessageHandler handler) : IHttpClientFactory {
    public HttpClient CreateClient(string name) =>
        new(handler, disposeHandler: false) { BaseAddress = new Uri("http://attachments.test") };
}
