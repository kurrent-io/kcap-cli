using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Commands;

/// <summary>
/// <c>kcap allow</c> — the allowlist half of the profile's capture scope. While it is empty every
/// path is capturable, which is the default and what every profile predating it does. Add one root
/// and capture narrows to it and its descendants; <see cref="IgnoreCommand"/> still subtracts
/// within. Removing the last entry widens capture back to everything, so the last
/// <c>--remove</c> is the one to think about.
/// </summary>
public sealed class AllowCommand(ConfigRoot root, ProfileContext profiles, UserHome home) {
    internal static readonly ProfilePathList List = new(
        "allow", "allowed", "Allowing",
        p => p.AllowedPaths ?? [],
        (p, v) => p with { AllowedPaths = v },
        EmptyNote: "every path is capturable");

    public Task<int> HandleAsync(string[] args) => new PathListEditor(root, profiles, home, List).HandleAsync(args);

    /// <summary>Pure; exposed for testing. See <see cref="PathListEditor.ApplyAdd"/>.</summary>
    public static Profile ApplyAdd(Profile profile, string path, UserHome home)
        => PathListEditor.ApplyAdd(List, profile, path, home);

    /// <summary>Pure; exposed for testing. See <see cref="PathListEditor.ApplyRemove"/>.</summary>
    public static Profile ApplyRemove(Profile profile, string path, UserHome home)
        => PathListEditor.ApplyRemove(List, profile, path, home);
}
