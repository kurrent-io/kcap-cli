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

    /// <summary>The daemon's pid, set on a Grok it hosts, whose session the daemon records from its ACP
    /// stream; kcap's own Grok hook loads inside that child too and must not record it again.</summary>
    public const string HostedVar = "KCAP_HOSTED_GROK";

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
