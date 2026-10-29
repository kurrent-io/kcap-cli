namespace Capacitor.Cli.Core.Setup;

/// <summary>Whether this process runs inside a Muse Assistant VM: the runtime user's home is
/// <c>/home/hatch</c>, its persistent workspace is <c>~/workspace</c>, and the image's own tools sit
/// under <c>/opt/hatch-image/bin</c>. All three together are the signature.</summary>
public static class MuseAssistantEnvironment {
    public const string HatchHome = "/home/hatch";
    public const string ImageBin  = "/opt/hatch-image/bin";

    // Joined with '/', not Path.Combine: the VM is Linux, and the paths must compare the same on a
    // Windows test host.
    public static string WorkspaceOf(UserHome home) => home.Path.TrimEnd('/') + "/workspace";

    public static bool IsMuseAssistant(UserHome home) => IsMuseAssistant(home, Directory.Exists);

    public static bool IsMuseAssistant(UserHome home, Func<string, bool> directoryExists) =>
        home.Path.TrimEnd('/') == HatchHome
        && directoryExists(WorkspaceOf(home))
        && directoryExists(ImageBin);
}
