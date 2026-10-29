using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Stands in for the person at the browser: GETs the authorize URL, takes its redirect to the CLI's
/// own 127.0.0.1 callback, and follows it in the background. It must not wait for that second hop:
/// the loopback browser starts reading its listener only once <see cref="TryOpen"/> returns.
/// </summary>
public sealed class AuthorizeRedirectLauncher : IBrowserLauncher, IDisposable {
    readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false });

    /// <summary>The callback GET, so a test can observe its failure rather than lose it.</summary>
    public Task Callback { get; private set; } = Task.CompletedTask;

    public bool TryOpen(string url) {
        using var authorize = _http.GetAsync(url).GetAwaiter().GetResult();

        var callback = authorize.Headers.Location
            ?? throw new InvalidOperationException($"authorize answered {(int)authorize.StatusCode} without a redirect");

        Callback = Task.Run(async () => {
            using var answered = await _http.GetAsync(callback);
        });

        return true;
    }

    public void Dispose() => _http.Dispose();
}
