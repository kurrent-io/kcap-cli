using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Commands;

/// <summary>
/// <c>kcap ignore</c> — the denylist half of the profile's capture scope. Paths listed here are
/// never captured, including when they sit inside an <see cref="AllowCommand"/> root.
/// </summary>
public sealed class IgnoreCommand(ConfigRoot root, ProfileContext profiles, UserHome home) {
    // JSON source-gen for init-only array properties leaves the value null when the JSON key is
    // absent, even though the C# initializer is `= []`. Treat null as empty on the way in.
    internal static readonly ProfilePathList List = new(
        "ignore", "ignored", "Ignoring",
        p => p.ExcludedPaths ?? [],
        (p, v) => p with { ExcludedPaths = v });

    public Task<int> HandleAsync(string[] args) => new PathListEditor(root, profiles, home, List).HandleAsync(args);

    /// <summary>Pure; exposed for testing. See <see cref="PathListEditor.ApplyAdd"/>.</summary>
    public static Profile ApplyAdd(Profile profile, string path, UserHome home)
        => PathListEditor.ApplyAdd(List, profile, path, home);

    /// <summary>Pure; exposed for testing. See <see cref="PathListEditor.ApplyRemove"/>.</summary>
    public static Profile ApplyRemove(Profile profile, string path, UserHome home)
        => PathListEditor.ApplyRemove(List, profile, path, home);
}
