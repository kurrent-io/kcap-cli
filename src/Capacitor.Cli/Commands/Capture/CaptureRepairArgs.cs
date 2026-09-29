namespace Capacitor.Cli.Commands.Capture;

internal static class CaptureRepairArgs {
    public static string? Validate(string[] args) {
        var sessions = 0;
        for (var i = 1; i < args.Length; i++) {
            switch (args[i]) {
                case "--repair-capture" or "--dry-run" or "--claude" or "--codex" or "--yes" or "-y":
                    break;
                case "--session" or "--server-url":
                    var flag = args[i];
                    if (flag == "--session") sessions++;
                    if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith('-'))
                        return $"{flag} requires a value.";
                    break;
                default:
                    return $"{args[i]} cannot be combined with --repair-capture; select one --session and optionally --claude or --codex.";
            }
        }
        return sessions == 1 ? null : "--repair-capture requires exactly one explicit --session ID.";
    }
}
