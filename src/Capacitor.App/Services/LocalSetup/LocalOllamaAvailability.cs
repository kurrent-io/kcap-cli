using System.Text.Json;

namespace Capacitor.App.Services.LocalSetup;

/// Only checks whether Ollama answers, or is on disk; never starts it or downloads a model.
public static class LocalOllamaAvailability {
    // The installer's Ollama client takes no proxy and follows no redirect; the probe must reach what it reaches.
    static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) {
        Timeout = TimeSpan.FromSeconds(3),
    };

    // A GUI app's PATH leaves out Homebrew and /usr/local, so the CLI's usual homes are named outright. Ollama's Windows
    // installer is per user and puts it under %LOCALAPPDATA%.
    static readonly string[] InstallPaths = OperatingSystem.IsWindows()
        ? Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } local ? [Path.Combine(local, "Programs", "Ollama", "ollama.exe")] : []
        : ["/Applications/Ollama.app", "/opt/homebrew/bin/ollama", "/usr/local/bin/ollama"];

    public static bool Installed() => Installed(path => Directory.Exists(path) || File.Exists(path));

    public static bool Installed(Func<string, bool> exists) => InstallPaths.Any(exists);

    public static Task<bool> CheckAsync(CancellationToken ct) =>
        CheckAsync(Http, new Uri("http://localhost:11434/api/version"), ct);

    public static async Task<bool> CheckAsync(HttpClient http, Uri endpoint, CancellationToken ct) {
        try {
            using var response = await http.GetAsync(endpoint, ct);
            if (!response.IsSuccessStatusCode) return false;
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return body.RootElement.ValueKind == JsonValueKind.Object
                && body.RootElement.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(version.GetString());
        } catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is OperationCanceledException && !ct.IsCancellationRequested) {
            return false;
        }
    }
}
