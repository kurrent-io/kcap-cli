using Capacitor.App.Services.Onboarding;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

public enum ConnectChoice { Discover, Paste }

/// <summary>
/// How the Sign-in step reaches a workspace. Intent only: nothing here reaches the network or
/// writes anything. A pasted server is normalized by the SAME rule the operation uses, then
/// validated by the gate's shared server-URL validator, so "Continue accepted it" and "the gate can
/// be satisfied by it" can never disagree. Discovery is single sign-on only; a server on another
/// provider is reached by name or URL, where its own auth config picks the flow.
/// </summary>
public sealed class ConnectChoiceViewModel : ReactiveObject {
    internal const string InvalidServerMessage = "Enter a workspace name (e.g. acme) or a full https:// server URL.";

    ConnectChoice _choice = ConnectChoice.Discover;
    bool          _useDeviceCode;
    string        _serverInputText = "";
    string?       _inputError;

    public ConnectChoice Choice {
        get => _choice;
        set {
            this.RaiseAndSetIfChanged(ref _choice, value);
            Restate();
        }
    }

    /// Discovery through a device code rather than a browser on this machine.
    public bool UseDeviceCode {
        get => _useDeviceCode;
        set {
            this.RaiseAndSetIfChanged(ref _useDeviceCode, value);
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

    /// What the Sign-in step will run; null while the paste input is unusable.
    public ConnectIntent? Intent => Choice switch {
        ConnectChoice.Discover                               => new ConnectIntent.Discover(UseDeviceCode),
        ConnectChoice.Paste when UsableServer() is { } server => new ConnectIntent.Paste(server),
        _                                                    => null
    };

    /// The re-auth dialog's target, and a WorkOS "I already have a workspace" answer.
    public void Prefill(string target) {
        ServerInputText = target;
        Choice          = ConnectChoice.Paste;
    }

    /// False, with the complaint shown, when the staged choice cannot run.
    public bool Validate() {
        if (Intent is not null) return true;

        InputError = InvalidServerMessage;

        return false;
    }

    string? UsableServer() {
        if (string.IsNullOrWhiteSpace(ServerInputText)) return null;

        var resolved = WizardSignInOperation.ResolveServer(ServerInputText);

        return OnboardingGate.ValidServerUrl(resolved) ? resolved : null;
    }

    void Restate() => this.RaisePropertyChanged(nameof(Intent));
}
