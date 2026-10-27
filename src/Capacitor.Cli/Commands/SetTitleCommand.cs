using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

/// <summary><c>kcap set-title</c>: the session's own agent names it. A plain title is a rename; <c>--auto</c> is the
/// first-prompt title, which replaces only generated titles. A rename the server did not apply exits 3, so an agent
/// never reports a rename that did not happen. An <c>--auto</c> title that loses only says so: a title someone chose
/// outranking it is the intended outcome, and the hook asks for it silently on every first prompt.</summary>
class SetTitleCommand(ISessionsApi sessionsApi) {
    public const string AutoFlag = "--auto";

    // Not 2, the exit for an unusable server URL.
    const int NotApplied = 3;

    public async Task<int> Handle(string sessionId, string title, bool auto, TextWriter stderr) {
        SetTitleResult result;

        try {
            result = await sessionsApi.SetSessionTitleAsync(sessionId, title, rename: !auto);
        } catch (CapacitorApiException ex) {
            await stderr.WriteLineAsync(ex.Message);

            return 1;
        }

        switch (result) {
            case SetTitleResult.Applied:
                return 0;
            case SetTitleResult.NotApplied { Shown: var shown }:
                await stderr.WriteLineAsync(shown is null
                    ? "Title not applied: the session keeps its current title."
                    : $"Title not applied: the session keeps \"{shown}\".");

                return auto ? 0 : NotApplied;
            case SetTitleResult.Unconfirmed:
                await stderr.WriteLineAsync("Title sent, but this server cannot confirm it: it only titles a session that has no title yet.");

                return auto ? 0 : NotApplied;
            default:
                await stderr.WriteLineAsync($"The server has no session {sessionId} that you own.");

                return 1;
        }
    }
}
