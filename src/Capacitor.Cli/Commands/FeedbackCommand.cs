using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

/// <summary>
/// <c>kcap feedback (--bug | --feedback) [-m|--message &lt;text&gt;]</c> — files a bug report or
/// sends feedback to Kurrent support through the tenant's <c>POST /api/feedback</c>, which resolves
/// the reporter's email server-side and forwards to the auth proxy's Plain sink.
///
/// <para>Follows <see cref="ValidatePlanCommand"/>'s <c>Handle</c>/<c>HandleCore</c> split: <c>Handle</c>
/// owns argument parsing and the interactive prompt; <c>HandleCore</c> takes an already-resolved
/// category and message so tests can drive it without a real stdin.</para>
/// <para>A local installation has no support intake, so there <paramref name="mail"/> is set and the
/// report becomes an email draft instead.</para>
/// </summary>
public sealed class FeedbackCommand(IFeedbackApi feedbackApi, Func<FeedbackSubmission, FeedbackMailOutcome>? mail = null) {
    /// <summary>
    /// The pinned success line (post-spike variant: promises email replies). Printed to stdout so
    /// <c>kcap feedback ... | ...</c> can capture it; everything else in this command writes to stderr.
    /// </summary>
    internal const string SuccessPrefix = "✓ Sent to Kurrent support as ";

    const string BugFlag      = "--bug";
    const string FeedbackFlag = "--feedback";

    const string InteractivePrompt = "What's going on? (end with an empty line)";

    public Task<int> HandleAsync(string[] args) =>
        HandleAsync(args, Console.IsInputRedirected, Console.ReadLine);

    /// <summary>
    /// Test-friendly entry point: <paramref name="stdinIsRedirected"/> and <paramref name="readLine"/>
    /// stand in for <see cref="Console.IsInputRedirected"/>/<see cref="Console.ReadLine"/> so the
    /// TTY-vs-piped branch and the interactive prompt's line collection are exercised without a real
    /// terminal or process stdin.
    /// </summary>
    internal async Task<int> HandleAsync(string[] args, bool stdinIsRedirected, Func<string?> readLine) {
        var isBug      = args.Contains(BugFlag);
        var isFeedback = args.Contains(FeedbackFlag);

        // Exactly one of --bug/--feedback: neither and both are the same usage error, naming both
        // flags so the fix is obvious either way.
        if (isBug == isFeedback) {
            await Console.Error.WriteLineAsync("Usage: kcap feedback (--bug | --feedback) [-m|--message <text>]");
            await Console.Error.WriteLineAsync("  Pass exactly one of --bug or --feedback.");

            return 1;
        }

        var category   = isBug ? FeedbackCategory.Bug : FeedbackCategory.Feedback;
        var rawMessage = GetMessageArg(args);

        if (rawMessage is null) {
            // stdin is not a TTY (piped/redirected): there is no one to prompt, so -m is mandatory.
            if (stdinIsRedirected) {
                await Console.Error.WriteLineAsync("A message is required.");

                return 1;
            }

            await Console.Error.WriteLineAsync(InteractivePrompt);
            rawMessage = ReadInteractiveMessage(readLine);
        }

        var message = rawMessage.Trim();

        if (message.Length == 0) {
            await Console.Error.WriteLineAsync("A message is required.");

            return 1;
        }

        return mail is null ? await HandleCore(feedbackApi, category, message) : await HandleMail(mail, category, message);
    }

    internal static async Task<int> HandleMail(Func<FeedbackSubmission, FeedbackMailOutcome> mail, FeedbackCategory category, string message) {
        var submission = new FeedbackSubmission(category, message, Guid.NewGuid(), FeedbackSource.Cli);
        var outcome    = mail(submission);

        var (opened, cutShort, copied) = (outcome.Opened, outcome.Draft.CutShort, outcome.Copied);

        await Console.Error.WriteLineAsync((opened, cutShort, copied) switch {
            (true, false, false) => $"Opened an email to {FeedbackMail.Address} in your mail app. Send it from there; the report is below in case no draft appeared:",
            (true, false, true)  => $"Opened an email to {FeedbackMail.Address} in your mail app. Send it from there; a copy is on your clipboard.",
            (true, true, true)   => $"Opened an email to {FeedbackMail.Address}, cut short to fit a mail link. The full report is on your clipboard: paste it over the draft's text.",
            (true, true, false)  => $"Opened an email to {FeedbackMail.Address}, cut short to fit a mail link. Paste the full report below over the draft's text:",
            (false, _, true)     => $"Couldn't open a mail app. The report is on your clipboard: paste it into an email to {FeedbackMail.Address}.",
            (false, _, false)    => $"Couldn't open a mail app. Email the report below to {FeedbackMail.Address}:",
        });

        // A launched handler is no proof a draft appeared (xdg-open returns before it finds one), so
        // without the clipboard the terminal is the one copy of the report that is sure to exist.
        if (!copied) {
            await Console.Out.WriteLineAsync();
            await Console.Out.WriteLineAsync(FeedbackMail.Body(submission));
        }

        return opened || copied ? 0 : 1;
    }

    /// <summary>Test-friendly core: caller owns the <see cref="IFeedbackApi"/>. <paramref name="message"/>
    /// is already trimmed and non-empty.</summary>
    internal static async Task<int> HandleCore(IFeedbackApi feedbackApi, FeedbackCategory category, string message) {
        try {
            var submission = new FeedbackSubmission(category, message, Guid.NewGuid(), FeedbackSource.Cli);

            return await ReportResultAsync(await feedbackApi.SubmitAsync(submission));
        } catch (CapacitorApiException ex) {
            await Console.Error.WriteLineAsync(ex.Message);

            return 1;
        }
    }

    static async Task<int> ReportResultAsync(FeedbackResult result) {
        if (result is FeedbackResult.Sent(var reporterEmail)) {
            await Console.Out.WriteLineAsync($"{SuccessPrefix}{reporterEmail} — replies will reach you by email.");

            return 0;
        }

        await Console.Error.WriteLineAsync(FeedbackResultMessages.ForRefusal(result));

        return 1;
    }

    static string? GetMessageArg(string[] args) {
        for (var i = 0; i < args.Length - 1; i++) {
            if (args[i] is "-m" or "--message") return args[i + 1];
        }

        return null;
    }

    /// <summary>
    /// Collects lines from <paramref name="readLine"/> until an empty line (or end of input,
    /// e.g. Ctrl+D) and joins them with newlines. Pure and injectable so tests can drive the
    /// multi-line collection without a real TTY.
    /// </summary>
    internal static string ReadInteractiveMessage(Func<string?> readLine) {
        var lines = new List<string>();

        while (true) {
            var line = readLine();

            if (string.IsNullOrEmpty(line)) break;

            lines.Add(line);
        }

        return string.Join('\n', lines);
    }
}
