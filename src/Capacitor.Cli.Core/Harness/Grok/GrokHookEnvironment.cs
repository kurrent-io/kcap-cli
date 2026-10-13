using System.Text.Json;

namespace Capacitor.Cli.Core.Harness.Grok;

/// <summary>
/// Grok also runs the hooks that Claude Code's and Cursor's configs declare, handing them Grok's own
/// payload, and sets <see cref="HookEventVar"/> on every hook it runs. A session started beneath a Grok
/// hook inherits that variable, so a hook is Grok's only when its payload also names that event under
/// Grok's camelCase <c>hookEventName</c>, a key no other vendor sends.
/// </summary>
public static class GrokHookEnvironment {
    public const string HookEventVar = "GROK_HOOK_EVENT";

    /// <summary>Set by the daemon on a Grok it hosts. The daemon records that session from its ACP
    /// stream, so kcap's own Grok hook, which loads inside the child too, must not record it again.</summary>
    public const string HostedVar = "KCAP_HOSTED_GROK";

    public static bool IsDaemonHosted(Func<string, string?> getEnv) => getEnv(HostedVar) == "1";

    public static bool IsGrokHook(Func<string, string?> getEnv) => !string.IsNullOrEmpty(getEnv(HookEventVar));

    public static bool IsGrokPayload(string body, Func<string, string?> getEnv) {
        var hookEvent = getEnv(HookEventVar);
        if (string.IsNullOrEmpty(hookEvent)) return false;

        try {
            using var doc = JsonDocument.Parse(body);

            return doc.RootElement.Str("hookEventName") == hookEvent;
        } catch (JsonException) {
            return false;
        }
    }
}
