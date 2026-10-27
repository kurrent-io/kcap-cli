namespace Capacitor.App.Platform.Mac;

/// A `kcap` on the terminal PATH that is not the app's bundled copy; `RemoveCommand` is null when
/// its origin is unknown.
public sealed record OtherKcapInstall(string Path, string? RemoveCommand);
