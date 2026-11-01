using Capacitor.Cli.Core.Setup.Intelligence;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// One answer the background intelligence page offers: a local agent's harness, a provider, or not now.
public sealed class IntelligenceOption : ReactiveObject {
    public const string ProviderToken = "provider";

    bool _isSelected;

    IntelligenceOption(string token, string label, string detail) {
        Token  = token;
        Label  = label;
        Detail = detail;
    }

    internal static IntelligenceOption LocalAgent(string harness) => new(harness, $"My local agent: {HarnessLabel(harness)}",
        $"Runs on this machine through {HarnessLabel(harness)}'s own sign-in and quota. Nothing goes anywhere it does not already send.");

    internal static IntelligenceOption Provider() => new(ProviderToken, "A provider, with my API key",
        "Anthropic, OpenAI, OpenRouter or Cohere. kcap tries the key with one call before saving it.");

    internal static IntelligenceOption NotNow() => new(IntelligenceAnswer.NoneToken, "Not now",
        "Sessions keep their opening message as a title. Choose later with kcap local intelligence.");

    public string Token  { get; }
    public string Label  { get; }
    public string Detail { get; }

    public bool IsLocalAgent => Token is not ProviderToken and not IntelligenceAnswer.NoneToken;

    public bool IsSelected {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    static string HarnessLabel(string harness) => harness switch {
        HeadlessHarnessProbe.Claude => "Claude Code",
        HeadlessHarnessProbe.Codex  => "Codex",
        _                           => harness,
    };
}
