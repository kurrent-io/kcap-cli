using Capacitor.Cli.Core.Auth;
using Spectre.Console;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Resolves which provider to use for tenant discovery: explicit --github/--workos flags or a
/// non-interactive default win (<see cref="OAuthLoginFlow.ChooseDiscoveryProvider"/>); otherwise
/// prompts. The WorkOS option is shown as the unbranded "Continue" (primary), GitHub as secondary.
/// </summary>
public static class DiscoveryProviderPrompt {
    public static string Resolve(string[] args) {
        var chosen = OAuthLoginFlow.ChooseDiscoveryProvider(args, isInteractive: !HeadlessEnvironment.IsHeadless());
        if (chosen is not null) return chosen;

        return AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("How would you like to sign in?")
                .AddChoices(AuthProvider.WorkOS, AuthProvider.GitHubApp)
                .UseConverter(p => p == AuthProvider.GitHubApp ? "Continue with GitHub" : "Continue"));
    }
}
