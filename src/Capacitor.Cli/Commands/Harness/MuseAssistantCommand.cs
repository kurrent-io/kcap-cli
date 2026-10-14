using Capacitor.Cli.Harness.MuseAssistant;

namespace Capacitor.Cli.Commands.Harness;

/// <summary><c>kcap muse-assistant</c>: the recorder Muse's cron runs on its own VM, and the consent
/// commands the recording skill runs in the user's turn.</summary>
public sealed class MuseAssistantCommand(MuseAssistantIngest ingest, MuseAssistantScope scope) {
    internal const string UsageText =
        "Usage: kcap muse-assistant ingest <dir>\n" +
        "       kcap muse-assistant scope add --chat <id> (--after-seq <seq> | --empty-chat | --include-history)\n" +
        "         (an active grant with a different boundary is refused, exit 2: `scope remove` it first)\n" +
        "       kcap muse-assistant scope remove --chat <id>\n" +
        "       kcap muse-assistant scope reset | scope show\n" +
        "       kcap muse-assistant sweep";

    public async Task<int> HandleAsync(string[] args) {
        using var cts = new CancellationTokenSource();

        void Cancel(object? _, ConsoleCancelEventArgs e) { e.Cancel = true; cts.Cancel(); }

        Console.CancelKeyPress += Cancel;

        // Outside exit codes 0 to 4 the cron and the skill have no reading, and a stack trace is not one.
        // A removal that dies unexpectedly reports that recording could not be confirmed stopped:
        // retrying is always safe, and claiming it stopped would be the one wrong answer.
        var failure = args is [_, "scope", "remove" or "reset", ..] ? MuseAssistantExitCodes.NotRevoked : MuseAssistantExitCodes.Server;

        try {
            return (args.Length > 1 ? args[1] : null) switch {
                "ingest" when args.Length == 3 => await ingest.RunAsync(args[2], cts.Token),
                "sweep"  when args.Length == 2 => await scope.SweepAsync(cts.Token),
                "scope"                        => await HandleScopeAsync(args, cts.Token),
                _                              => Usage()
            };
        } catch (Exception ex) {
            var reason = cts.IsCancellationRequested ? "interrupted" : $"{ex.GetType().Name}: {ex.Message.ReplaceLineEndings(" ")}";
            Console.Error.WriteLine($"kcap muse-assistant {args[1]}: {reason}");

            return failure;
        } finally {
            Console.CancelKeyPress -= Cancel;
        }
    }

    async Task<int> HandleScopeAsync(string[] args, CancellationToken ct) {
        var chat = args.Length > 4 && args[3] == "--chat" && IsChatId(args[4]) ? args[4] : null;

        return (args.Length > 2 ? args[2] : null) switch {
            "add"    when chat is not null && IsBoundary(args[5..]) => await scope.AddAsync(chat, args, ct),
            "remove" when chat is not null && args.Length == 5      => await scope.RemoveAsync(chat, ct),
            "reset"  when args.Length == 3                          => await scope.ResetAsync(ct),
            "show"   when args.Length == 3                          => await scope.ShowAsync(ct),
            _                                                       => Usage()
        };
    }

    // A flag in the id's place means the id was left out; granting a chat named "--empty-chat" would be silent.
    static bool IsChatId(string value) => !string.IsNullOrWhiteSpace(value) && !value.StartsWith("--", StringComparison.Ordinal);

    static bool IsBoundary(string[] rest) => rest is ["--empty-chat"] or ["--include-history"] or ["--after-seq", _];

    static int Usage() {
        Console.Error.WriteLine(UsageText);
        return MuseAssistantExitCodes.Malformed;
    }
}
