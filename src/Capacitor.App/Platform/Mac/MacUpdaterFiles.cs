using Capacitor.Cli.Core;
using Velopack.Locators;

namespace Capacitor.App.Platform.Mac;

internal sealed record MacUpdaterFiles(IReadOnlyList<string> Directories, IReadOnlyList<string> Logs) {
    public static MacUpdaterFiles? ForCurrentBundle(string bundle, UserHome home) =>
        OperatingSystem.IsMacOS() ? Resolve(VelopackLocator.Current, bundle, home, Path.GetTempPath()) : null;

    internal static MacUpdaterFiles? Resolve(IVelopackLocator locator, string bundle, UserHome home, string temp) {
        if (locator.RootAppDir is not { } root || CanonicalPath.Resolve(root) != CanonicalPath.Resolve(bundle)
            || locator.AppId is not { Length: > 0 } id || !id.Any(char.IsAsciiLetterOrDigit)
            || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) return null;

        var directories = new List<string>();
        if (locator.PackagesDir is { } packages && Path.IsPathFullyQualified(packages)
            && Path.GetFileName(packages) == "packages" && Path.GetDirectoryName(packages) is { } cache
            && Path.GetFileName(cache) == id) directories.Add(cache);
        if (locator.AppTempDir is { } scratch && Path.IsPathFullyQualified(scratch)
            && Path.GetFileName(scratch) == id) directories.Add(scratch);
        var log = $"velopack_{id}.log";
        return new(directories, [Path.Combine(home.Path, "Library", "Logs", log), Path.Combine(temp, log)]);
    }
}
