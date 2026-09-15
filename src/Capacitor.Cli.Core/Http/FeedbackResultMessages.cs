using System.Diagnostics;

namespace Capacitor.Cli.Core.Http;

/// <summary>The sentence a person reads for each refusal the feedback lane distinguishes. Refusals
/// only: a success is presented by the surface that sent it.</summary>
public static class FeedbackResultMessages {
    public static string? ForRefusal(FeedbackResult result) => result switch {
        FeedbackResult.Sent                                    => null,
        FeedbackResult.NotConfigured                           => "This server doesn't have support intake enabled.",
        FeedbackResult.Unavailable                             => "Support intake isn't configured on this server — ask your admin.",
        FeedbackResult.NoEmailOnFile                           => "Your account has no email on file — sign in to the web app once, then retry.",
        FeedbackResult.RateLimited                             => "You've sent several reports recently — try again in a few minutes.",
        FeedbackResult.TemporarilyUnavailable(var retryAfter)  => $"Couldn't reach Kurrent support (temporary) — try again{Suffix(retryAfter)}",
        FeedbackResult.Invalid(var message)                    => message,
        _                                                      => throw new UnreachableException()
    };

    static string Suffix(TimeSpan? retryAfter) =>
        retryAfter is { } delta ? $" in {(int)Math.Ceiling(delta.TotalSeconds)}s." : ".";
}
