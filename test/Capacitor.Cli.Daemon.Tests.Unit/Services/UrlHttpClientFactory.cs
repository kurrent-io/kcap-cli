namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// Points every client it hands out at one base address, so a stub server stands in for the API.
internal sealed class UrlHttpClientFactory(string baseUrl) : IHttpClientFactory {
    public HttpClient CreateClient(string name) => new() { BaseAddress = new Uri(baseUrl) };
}
