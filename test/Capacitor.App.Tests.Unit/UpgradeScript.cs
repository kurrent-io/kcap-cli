using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

/// Scripted `kcap local upgrade --json` lines, in the CLI's wire shape, and runs for a FakeKcapCli.
static class UpgradeScript {
    public const string ServerUrl = "http://localhost:5123";

    public static string Planned(string from, string to) =>
        $$"""{"v":1,"event":"upgrade_planned","from_version":"{{from}}","version":"{{to}}"}""";

    public static string Switched(string version) => $$"""{"v":1,"event":"switched","version":"{{version}}"}""";

    public static string Upgraded(string from, string to) =>
        $$"""{"v":1,"event":"upgraded","server_url":"{{ServerUrl}}","from_version":"{{from}}","version":"{{to}}"}""";

    public static string NothingToDo(string reason, string version, string? installed) =>
        installed is null
            ? $$"""{"v":1,"event":"nothing_to_do","version":"{{version}}","reason":"{{reason}}"}"""
            : $$"""{"v":1,"event":"nothing_to_do","version":"{{version}}","installed_version":"{{installed}}","reason":"{{reason}}"}""";

    public static string Failed(string code, string message, bool? switched) =>
        switched is null
            ? $$"""{"v":1,"event":"failed","code":"{{code}}","message":"{{message}}"}"""
            : $$"""{"v":1,"event":"failed","code":"{{code}}","message":"{{message}}","switched":{{(switched.Value ? "true" : "false")}}}""";

    /// Prints the lines on stdout and exits at once.
    public static Func<Action<StreamedLine>, CancellationToken, CancellationToken, Task<StreamingResult>> Prints(
            int exitCode, params string[] lines) =>
        (onLine, _, _) => {
            foreach (var line in lines) onLine(new StreamedLine(ProcessStreamKind.Stdout, line));
            return Task.FromResult(new StreamingResult(exitCode, false, []));
        };
}
