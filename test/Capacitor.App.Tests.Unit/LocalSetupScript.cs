using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.App.Tests.Unit;

/// Scripted `kcap setup --local --json` runs for a FakeKcapCli, and the journal a real one leaves.
static class LocalSetupScript {
    public const string ServerUrl = "http://127.0.0.1:5123";

    public static string Committed => $$"""{"v":1,"event":"committed","server_url":"{{ServerUrl}}"}""";

    public static string Failed(string code, string message = "it went wrong") =>
        $$"""{"v":1,"event":"failed","code":"{{code}}","message":"{{message}}"}""";

    /// Prints the lines on stdout and exits at once.
    public static Func<Action<StreamedLine>, CancellationToken, CancellationToken, Task<StreamingResult>> Prints(
            int exitCode, params string[] lines) =>
        (onLine, _, _) => {
            foreach (var line in lines) onLine(new StreamedLine(ProcessStreamKind.Stdout, line));
            return Task.FromResult(new StreamingResult(exitCode, false, []));
        };

    /// An interrupted setup, past recording its embedding answer unless <paramref name="recorded"/> is false.
    public static LocalRoot PendingSetup(TempConfigRoot config, bool recorded = true) {
        var root = new LocalRoot(config.PathTo("local"));
        root.EnsureCreated();
        LocalStateFiles.WriteJournal(root, new(LocalSetupState.Operation, "committing", ServerStart.Deny, null,
            DateTimeOffset.UnixEpoch, new Dictionary<string, string>()));
        if (recorded) LocalEmbeddingFile.Write(root, LocalEmbeddingSetting.Ollama("mxbai-embed-large"));

        return root;
    }

    public static LocalRoot NoSetup(TempConfigRoot config) => new(config.PathTo("local"));
}
