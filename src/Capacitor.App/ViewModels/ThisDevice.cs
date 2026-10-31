namespace Capacitor.App.ViewModels;

/// <summary>What copy calls the machine the app runs on.</summary>
public static class ThisDevice {
    public static readonly string Name = OperatingSystem.IsMacOS() ? "this Mac" : "this computer";
}
