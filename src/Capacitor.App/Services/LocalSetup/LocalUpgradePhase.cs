namespace Capacitor.App.Services.LocalSetup;

public enum LocalUpgradePhase {
    Waiting,
    Recovering,
    Downloading,
    Stopping,
    Starting,
    Restarting,
}
