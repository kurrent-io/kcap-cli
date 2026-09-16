using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Commands;

/// <summary>
/// One of the profile's path lists, plus the words its command speaks in. <c>kcap ignore</c> and
/// <c>kcap allow</c> differ only in which array they edit and how they phrase the result.
/// </summary>
/// <param name="Verb">The command name, as it appears in usage and in "Not in &lt;verb&gt; list".</param>
/// <param name="Adjective">Past participle for a listed path — "ignored", "allowed".</param>
/// <param name="Gerund">Sentence-leading form for a fresh entry — "Ignoring", "Allowing".</param>
/// <param name="EmptyNote">What an empty list means, for a list where that is not obvious. "No
/// allowed paths" reads like nothing is captured when it means the opposite, and emptying the allow
/// list widens capture to the whole machine — so both places that can report it say so.</param>
sealed record ProfilePathList(
    string                           Verb,
    string                           Adjective,
    string                           Gerund,
    Func<Profile, string[]>          Get,
    Func<Profile, string[], Profile> With,
    string?                          EmptyNote = null);
