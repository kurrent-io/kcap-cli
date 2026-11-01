namespace Capacitor.App.Services.LocalSetup;

/// How the install page offers running kcap as a Windows service.
public enum WindowsServiceOffer {
    /// Not Windows, or a current service already runs kcap: the install uses it without asking.
    Hidden,
    On,
    /// Offered unticked: the user declined the administrator prompt before.
    Off,
}
