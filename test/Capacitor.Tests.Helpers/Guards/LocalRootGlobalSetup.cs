using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Tests.Helpers.Guards;

/// <summary>
/// Assembly-wide pin for the local installation's root, so nothing under test reaches the
/// developer's real <c>~/.config/kcap/local/</c> and its lifecycle lock. The value cannot be created,
/// for the reason <see cref="DaemonPathsGlobalSetup"/> gives: whatever reaches it was meant to be
/// handed a root of its own.
/// </summary>
public class LocalRootGlobalSetup {
    static readonly TempDir Dir = new("nolocal");

    public static string SentinelLocalRoot => Path.Combine(Dir.Path, "not-a-directory", "local");

    /// <summary>A root nothing creates, so its registry reads as absent.</summary>
    public static string AbsentLocalRoot => Path.Combine(Dir.Path, "absent");

    [BeforeEvery(Assembly)]
    public static void PinLocalRoot() {
        Dir.CreateFile("not-a-directory");
        Environment.SetEnvironmentVariable(LocalRoot.EnvVar, SentinelLocalRoot);
    }

    [AfterEvery(Assembly)]
    public static void CleanupLocalRoot() {
        Environment.SetEnvironmentVariable(LocalRoot.EnvVar, null);
        Dir.Dispose();
    }
}
