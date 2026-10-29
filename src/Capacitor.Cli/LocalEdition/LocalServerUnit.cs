using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>
/// The OS service that runs the local server. Its names share no prefix with a daemon unit's
/// (<c>io.kurrent.kcap.daemon.*</c>, <c>kcap-daemon-*.service</c>), so no daemon-service listing, refresh,
/// replace or removal can match it.
///
/// <para>The server reads its root and port from the environment and needs no arguments: the bundle is a
/// local-edition build. The binary is reached through <c>bin/current</c>, so an upgrade switches the
/// version without rewriting the unit.</para>
/// </summary>
sealed record LocalServerUnit(string ServerBinary, LocalRoot Root, int Port) {
    public const string ProductionLabel       = "io.kurrent.kcap.local";
    public const string ProductionSystemdName = "kcap-local.service";

    /// <summary>The launchd label. Tests that load a real unit give it a name of its own.</summary>
    public string Label { get; init; } = ProductionLabel;

    public string SystemdName { get; init; } = ProductionSystemdName;

    /// <summary>The embedding setting the server runs with; a decline, a provider this release does not know, or
    /// none searches by keyword.</summary>
    public LocalEmbeddingSetting? Embedding { get; init; }

    public static LocalServerUnit ForInstallation(LocalRoot root, int port) =>
        new(Path.Combine(root.Directory, "bin", "current", "kcap-server"), root, port) { Embedding = LocalEmbeddingFile.Read(root) };

    /// <summary>Whether the unit already runs the server as <paramref name="setting"/> asks, its pending flag aside.</summary>
    public bool Serves(LocalEmbeddingSetting? setting) => Served(Embedding) == Served(setting);

    static (LocalEmbeddingProvider, string?, string?)? Served(LocalEmbeddingSetting? setting) =>
        setting is { Provider: LocalEmbeddingProvider.Ollama or LocalEmbeddingProvider.Builtin } ? (setting.Provider, setting.Model, setting.ModelSetId) : null;

    public string BaseUrl => $"http://localhost:{Port}";

    /// <summary>What the process writes before its own logging starts, or if it dies before it can.</summary>
    public string OutputLog => Path.Combine(Root.Directory, "logs", "server.out.log");

    /// <summary>The service manager opens <see cref="OutputLog"/> before the server runs, so its directory must
    /// already exist.</summary>
    public void PrepareLogDirectory() {
        Root.EnsureCreated();

        var logs = Path.GetDirectoryName(OutputLog)!;
        if (OperatingSystem.IsWindows()) {
            Directory.CreateDirectory(logs);
            return;
        }

        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(logs, ownerOnly);
        File.SetUnixFileMode(logs, ownerOnly);
    }

    /// <summary>The embedding settings are environment because environment outranks what the web UI saves: the
    /// model fixes the stored vectors' width, so only setup changes it.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Environment => [
        new(LocalRoot.EnvVar, Root.Directory),
        new("Local__Port", Port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        .. Embedding switch {
            { Provider: LocalEmbeddingProvider.Builtin } => new KeyValuePair<string, string>[] { new("Embedding__Provider", "builtin") },
            { Provider: LocalEmbeddingProvider.Ollama, Model: { } model } => [
                new("Embedding__Provider", "ollama"),
                new("Embedding__Ollama__Model", model),
                new("Embedding__Ollama__BaseUrl", OllamaClient.DefaultUrl.ToString().TrimEnd('/')),
            ],
            _ => [],
        },
    ];
}
