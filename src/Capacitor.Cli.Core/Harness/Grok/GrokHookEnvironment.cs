namespace Capacitor.Cli.Core.Harness.Grok;

/// <summary>
/// Grok also runs the hooks that Claude Code's and Cursor's configs declare, handing them Grok's own
/// payload, and sets <see cref="HookEventVar"/> on every hook it runs. That variable is how a kcap hook
/// installed for another vendor tells it is running inside Grok.
/// </summary>
public static class GrokHookEnvironment {
    public const string HookEventVar = "GROK_HOOK_EVENT";

    public static bool IsGrokHook(Func<string, string?> getEnv) => !string.IsNullOrEmpty(getEnv(HookEventVar));
}
