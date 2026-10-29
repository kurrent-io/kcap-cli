namespace Capacitor.App.Services;

/// The prompt to show and the local-data consent it captures.
public sealed record UninstallRequest(LifecyclePrompt Prompt, bool PurgeLocal);
