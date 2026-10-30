namespace Capacitor.App.Services.LocalSetup;

public enum LocalUpgradePhase {
    Waiting,
    Recovering,
    Downloading,
    Verifying,
    Extracting,
    DownloadingModel,
    Stopping,
    Starting,
    Restarting,
}
