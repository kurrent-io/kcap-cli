namespace Capacitor.App.Services.Update;

public sealed record UpdateCheckProgress(string Message, int? Percent = null) {
    public bool Indeterminate => Percent is null;
    public double Value => Percent ?? 0;
    public string PercentText => Percent is { } percent ? $"{percent}%" : "";
}
