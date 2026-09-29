using Capacitor.App.Services.Onboarding;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

public enum ConnectChoice { Discover, Paste, Create }

/// <summary>
/// Intent only: nothing here reaches the network or writes anything. The Sign-in step runs
/// whatever this stages. A pasted server is normalized by the SAME rule the operation uses, then
/// validated by the gate's shared server-URL validator, so "Next accepted it" and "the gate can be
/// satisfied by it" can never disagree. Discovery is single sign-on only; a server on another
/// provider is reached by name or URL, where its own auth config picks the flow.
/// </summary>
public sealed class ConnectStepViewModel : ReactiveObject, IWizardStep {
    ConnectChoice _choice = ConnectChoice.Discover;
    string        _serverInputText = "";
    string?       _inputError;

    public WizardStepId Id         => WizardStepId.Connect;
    public string       Title      => "Connect to Capacitor";
    public bool         Applicable => true;

    public ConnectChoice Choice {
        get => _choice;
        set {
            this.RaiseAndSetIfChanged(ref _choice, value);
            Restate();
        }
    }

    public string ServerInputText {
        get => _serverInputText;
        set {
            this.RaiseAndSetIfChanged(ref _serverInputText, value);
            InputError = null; // editing clears the stale complaint
            Restate();
        }
    }

    public string? InputError {
        get => _inputError;
        private set => this.RaiseAndSetIfChanged(ref _inputError, value);
    }

    // Radio-button facets: the enum stays the single source of truth.
    public bool DiscoverSelected {
        get => Choice == ConnectChoice.Discover;
        set { if (value) Choice = ConnectChoice.Discover; }
    }

    public bool PasteSelected {
        get => Choice == ConnectChoice.Paste;
        set { if (value) Choice = ConnectChoice.Paste; }
    }

    public bool CreateSelected {
        get => Choice == ConnectChoice.Create;
        set { if (value) Choice = ConnectChoice.Create; }
    }

    /// What the Sign-in step will run; null while the paste input is unusable.
    public ConnectIntent? Intent => Choice switch {
        ConnectChoice.Discover                                 => new ConnectIntent.Discover(),
        ConnectChoice.Create                                   => new ConnectIntent.Create(),
        ConnectChoice.Paste when UsableServer() is { } server   => new ConnectIntent.Paste(server),
        _                                                      => null
    };

    public bool Satisfied => Intent is not null;

    /// The Sign-in step's answer to a retarget: come back here with the workspace filled in.
    public void Prefill(string target) {
        ServerInputText = target;
        Choice          = ConnectChoice.Paste;
    }

    public Task OnEnterAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) {
        if (direction != WizardNavigation.Next || Intent is not null) return Task.FromResult(true);

        InputError = "Enter a workspace name (e.g. acme) or a full https:// server URL.";

        return Task.FromResult(false);
    }

    string? UsableServer() {
        if (string.IsNullOrWhiteSpace(ServerInputText)) return null;

        var resolved = WizardSignInOperation.ResolveServer(ServerInputText);

        return OnboardingGate.ValidServerUrl(resolved) ? resolved : null;
    }

    void Restate() {
        this.RaisePropertyChanged(nameof(Intent));
        this.RaisePropertyChanged(nameof(Satisfied));
        this.RaisePropertyChanged(nameof(DiscoverSelected));
        this.RaisePropertyChanged(nameof(PasteSelected));
        this.RaisePropertyChanged(nameof(CreateSelected));
    }
}
