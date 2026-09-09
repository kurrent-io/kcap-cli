using Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Core.Tests.Unit.PullRequests.Readers.GitHubCli;

internal sealed class GhHarness : IDisposable {
    public readonly FakeGhProcessRunner Process = new();
    public readonly FakeTimeProvider Time = new();
    public readonly GitHubCliReaderProvider Provider;
    public readonly string? GhPath;

    public GhHarness(TempDir tmp, bool installed = true) {
        string dir = tmp.CreateDir("bin");
        if (installed) {
            GhPath = tmp.CreateFile(["bin", OperatingSystem.IsWindows() ? "gh.exe" : "gh"]);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(GhPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var runner = new GitHubCliRunner(Process, null, name => name == "PATH" ? dir : null);
        Provider = new GitHubCliReaderProvider(runner, Time);
    }

    public static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "gh", name));

    public void SignedIn(params string[] hosts) => SignedIn(hosts, "octocat");
    // A distinct parameter shape, not an overload on arg count: `SignedIn(login, params hosts)` is ambiguous with the
    // params-only overload for a single-host call, since C# prefers the fixed-arity candidate with an empty expansion.
    public void SignedIn(string[] hosts, string login) {
        var entries = hosts.Select(host => $"\"{host}\":[{{\"state\":\"success\",\"active\":true,\"host\":\"{host}\",\"login\":\"{login}\"}}]");
        Process.When(["auth", "status"], "{\"hosts\":{" + string.Join(',', entries) + "}}");
    }

    public string[] LastArgs => Process.Calls[^1].Args;

    public void Dispose() => Provider.Dispose();
}
