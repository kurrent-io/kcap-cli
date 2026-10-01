namespace Capacitor.App.ViewModels.Onboarding;

/// One thing setup will do on this machine, as the Welcome page lists it.
public sealed record WelcomeItem(string Title, string Detail);

/// The first page: what Capacitor is, and what setup is about to touch, before anything does.
public sealed class WelcomeStepViewModel : IWizardStep {
    public static readonly IReadOnlyList<WelcomeItem> Items = [
        new("Signs you in", "With your work account, into your team's Capacitor workspace — or creates one."),
        new("Connects your harnesses", "Adds Capacitor's hooks and tools to the agents you pick, in their own config files."),
        new("Brings your history", "Uploads the past sessions you choose. Nothing leaves this machine until you do."),
        new("Runs the daemon", "A background service, so agents can be started on this machine from anywhere."),
    ];

    public WizardStepId Id         => WizardStepId.Welcome;
    public string       Title      => "Your coding agents, with a memory";
    public string       Eyebrow    => "Get started";
    public bool         Applicable => true;
    public bool         Satisfied  => true;
    public bool         Skippable  => false;
    public string?      NextLabel  => "Get started";

    public string Lede =>
        "Capacitor records the sessions your coding agents run on this machine and gives them tools to search " +
        "that history — so what you already worked out is there the next time you need it.";

    public Task OnEnterAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) => Task.FromResult(true);
}
