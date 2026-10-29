using System.Globalization;

namespace Capacitor.Cli.Harness.MuseAssistant;

/// <summary>The consent boundary as the granting turn states it: the newest seq Muse read, a chat with
/// nothing in it yet, or the whole chat.</summary>
public sealed record MuseAssistantBoundary(string Kind, long? AfterSeq) {
    public const string Usage = "exactly one of --after-seq <seq>, --empty-chat or --include-history";

    public static (MuseAssistantBoundary? Boundary, string? Error) Parse(string[] args) {
        var given = args.Count(a => a is "--after-seq" or "--empty-chat" or "--include-history");
        if (given != 1) return (null, $"{Usage} is required");

        if (args.Contains("--empty-chat"))      return (new(MuseAssistantGrantBoundaries.EmptyChat, null), null);
        if (args.Contains("--include-history")) return (new(MuseAssistantGrantBoundaries.History, null), null);

        var at = Array.IndexOf(args, "--after-seq");
        if (at + 1 >= args.Length || !long.TryParse(args[at + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var seq))
            return (null, $"--after-seq needs a non-negative integer, the newest seq read from the chat in this turn; {Usage} is required");

        return (new(MuseAssistantGrantBoundaries.AfterSeq, seq), null);
    }
}
