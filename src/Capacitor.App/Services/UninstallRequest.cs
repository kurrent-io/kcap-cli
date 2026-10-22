namespace Capacitor.App.Services;

/// The prompt to show and the arguments the relaunched copy of the app runs the uninstall with.
public sealed record UninstallRequest(LifecyclePrompt Prompt, IReadOnlyList<string> RelaunchArgs);
