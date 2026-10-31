using System.Net;
using Capacitor.App.Services.LocalSetup;
using TUnit.Core.Enums;

namespace Capacitor.App.Tests.Unit;

public class LocalOllamaAvailabilityTests {
    sealed class Reply(HttpStatusCode status, string body) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    sealed class Failure(Exception error) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(error);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_stopped_or_unresponsive_ollama_is_unavailable(bool timedOut) {
        using var handler = new Failure(timedOut ? new TaskCanceledException() : new HttpRequestException("Connection refused"));
        using var http = new HttpClient(handler);
        await Assert.That(await LocalOllamaAvailability.CheckAsync(http, new Uri("http://localhost:11434/api/version"), CancellationToken.None))
            .IsFalse();
    }

    [Test]
    [Arguments(HttpStatusCode.OK, "{\"version\":\"0.12.3\"}", true)]
    [Arguments(HttpStatusCode.NotFound, "", false)]
    [Arguments(HttpStatusCode.OK, "not json", false)]
    [Arguments(HttpStatusCode.OK, "{\"version\":null}", false)]
    [Arguments(HttpStatusCode.OK, "{\"version\":\"\"}", false)]
    public async Task Only_an_ollama_version_response_counts_as_available(HttpStatusCode status, string body, bool available) {
        using var reply = new Reply(status, body);
        using var http = new HttpClient(reply);
        await Assert.That(await LocalOllamaAvailability.CheckAsync(http, new Uri("http://localhost:11434/api/version"), CancellationToken.None))
            .IsEqualTo(available);
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task The_app_bundle_or_a_homebrew_cli_counts_as_installed() {
        await Assert.That(LocalOllamaAvailability.Installed(path => path == "/Applications/Ollama.app")).IsTrue();
        await Assert.That(LocalOllamaAvailability.Installed(path => path == "/opt/homebrew/bin/ollama")).IsTrue();
        await Assert.That(LocalOllamaAvailability.Installed(_ => false)).IsFalse();
    }

    [Test, RunOn(OS.Windows)]
    public async Task The_per_user_windows_install_counts_as_installed() {
        await Assert.That(LocalOllamaAvailability.Installed(path => path.EndsWith(@"\Programs\Ollama\ollama.exe", StringComparison.OrdinalIgnoreCase))).IsTrue();
        await Assert.That(LocalOllamaAvailability.Installed(path => path == "/Applications/Ollama.app")).IsFalse();
    }
}
