using System.Runtime.InteropServices;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.Commands;

/// <summary>
/// The feedback lane of a local installation, which has no support intake: the report becomes a
/// prefilled email the user sends from their own mail app, so the reply address is theirs.
/// </summary>
public static class FeedbackMail {
    public const string Address = "team@kcap.ai";

    /// <summary>Windows' shell and several mail clients drop or truncate a mailto URL past about 2,048
    /// characters, so the body is cut to keep the whole link under this.</summary>
    public const int MaxUrlLength = 2000;

    /// <summary>Copies the full report first, so a draft cut to fit the link can say where the rest is.</summary>
    public static FeedbackMailOutcome Send(FeedbackSubmission submission, IBrowserLauncher launcher, Func<string, bool> copy) {
        var body   = Body(submission);
        var copied = copy(body);
        var draft  = Draft(submission.Category, body, copied);

        return new FeedbackMailOutcome(draft, launcher.TryOpen(draft.Url), copied);
    }

    /// <summary>A desktop message already ends in its client trailer, so only the OS is added to it.</summary>
    public static string Body(FeedbackSubmission submission) {
        var client = submission.Source == FeedbackSource.Desktop ? "" : $"kcap cli {CapacitorVersion.CurrentDisplay()} · ";

        return $"{submission.Message.Trim()}\n\n---\n{client}{RuntimeInformation.OSDescription}";
    }

    public static FeedbackMailDraft Draft(FeedbackCategory category, string body, bool copied) {
        var subject = category == FeedbackCategory.Bug ? "kcap bug report" : "kcap feedback";
        var full    = Url(subject, body);
        if (full.Length <= MaxUrlLength) return new FeedbackMailDraft(full, CutShort: false);

        var note = copied
            ? "\n\n[Cut short to fit the mail link. The full report is on your clipboard: paste it over this text.]"
            : "\n\n[Cut short to fit the mail link.]";

        // The longest prefix whose link still fits; encoded length grows with the prefix, so it bisects.
        int lo = 0, hi = body.Length;
        while (lo < hi) {
            var mid = (lo + hi + 1) / 2;
            if (Url(subject, Prefix(body, mid) + note).Length <= MaxUrlLength) lo = mid;
            else hi = mid - 1;
        }

        return new FeedbackMailDraft(Url(subject, Prefix(body, lo).TrimEnd() + note), CutShort: true);
    }

    // RFC 6068 wants CRLF line breaks in a mailto body.
    static string Url(string subject, string body) =>
        $"mailto:{Address}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body.ReplaceLineEndings("\r\n"))}";

    // A lone high surrogate does not escape.
    static string Prefix(string text, int length) =>
        length > 0 && char.IsHighSurrogate(text[length - 1]) ? text[..(length - 1)] : text[..length];
}
