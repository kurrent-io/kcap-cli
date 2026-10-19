using System.Xml.Linq;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

public class LocalServerUnitTests {
    [TempDir] public required TempDir Tmp { get; init; }

    LocalServerUnit Unit => field ??= LocalServerUnit.ForInstallation(new LocalRoot(Tmp.PathTo("local")), 51234);

    [Test]
    public async Task The_unit_runs_the_current_bundle_with_its_root_and_port() {
        await Assert.That(Unit.ServerBinary).IsEqualTo(Tmp.PathTo("local", "bin", "current", "kcap-server"));
        await Assert.That(Unit.Environment).Contains(new KeyValuePair<string, string>(LocalRoot.EnvVar, Tmp.PathTo("local")));
        await Assert.That(Unit.Environment).Contains(new KeyValuePair<string, string>("Local__Port", "51234"));
        await Assert.That(Unit.BaseUrl).IsEqualTo("http://localhost:51234");
    }

    [Test]
    public async Task Without_an_embedding_model_the_unit_names_no_embedding_settings() {
        await Assert.That(Unit.Environment.Select(e => e.Key)).IsEquivalentTo([LocalRoot.EnvVar, "Local__Port"]);
    }

    [Test]
    public async Task A_recorded_embedding_model_reaches_the_server_as_environment() {
        var root = new LocalRoot(Tmp.CreateDir("embedded"));
        LocalEmbeddingFile.Write(root, new LocalEmbeddingSetting("mxbai-embed-large"));

        var unit = LocalServerUnit.ForInstallation(root, 51234);

        await Assert.That(unit.Environment).Contains(new KeyValuePair<string, string>("Embedding__Provider", "ollama"));
        await Assert.That(unit.Environment).Contains(new KeyValuePair<string, string>("Embedding__Ollama__Model", "mxbai-embed-large"));
        await Assert.That(unit.Environment).Contains(new KeyValuePair<string, string>("Embedding__Ollama__BaseUrl", "http://localhost:11434"));
        await Assert.That(LocalServerSystemdUnit.Unit(unit)).Contains("Environment=Embedding__Ollama__Model=mxbai-embed-large\n");
    }

    [Test]
    public async Task The_plist_restarts_a_crashed_server_and_gives_it_time_to_stop_postgres() {
        var dict = XDocument.Parse(LocalServerLaunchdUnit.Plist(Unit)).Root!.Element("dict")!;

        await Assert.That(Value(dict, "Label")!.Value).IsEqualTo("io.kurrent.kcap.local");
        await Assert.That(Value(dict, "ProgramArguments")!.Elements("string").Select(e => e.Value)).IsEquivalentTo([Unit.ServerBinary]);
        await Assert.That(Value(dict, "KeepAlive")!.ToString(SaveOptions.DisableFormatting)).Contains("<key>SuccessfulExit</key><false />");
        await Assert.That(Value(dict, "ProcessType")!.Value).IsEqualTo("Standard");
        await Assert.That(Value(dict, "ExitTimeOut")!.Value).IsEqualTo("90");
        await Assert.That(Value(dict, "StandardErrorPath")!.Value).IsEqualTo(Unit.OutputLog);
    }

    [Test]
    public async Task The_systemd_unit_stops_the_server_alone_and_restarts_it_on_failure() {
        var unit = LocalServerSystemdUnit.Unit(Unit with { ServerBinary = "/home/a b/kcap-server" });

        await Assert.That(unit).Contains("ExecStart=\"/home/a b/kcap-server\"\n");
        await Assert.That(unit).Contains("KillMode=mixed\n");
        await Assert.That(unit).Contains("Restart=on-failure\n");
        await Assert.That(unit).Contains("TimeoutStopSec=90\n");
        await Assert.That(unit).Contains("Environment=Local__Port=51234\n");
        await Assert.That(unit).Contains($"StandardOutput=append:{Unit.OutputLog}\n");
        await Assert.That(unit).Contains($"StandardError=append:{Unit.OutputLog}\n");
    }

    /// <summary>The daemon's service verbs list, refresh, replace and remove what they match by name; the local
    /// server's unit must never be one of them.</summary>
    [Test]
    public async Task Daemon_service_listings_never_match_the_local_server() {
        var home = new UserHome(Tmp.PathTo("home"));
        Tmp.CreateFile(["home", "Library", "LaunchAgents", "io.kurrent.kcap.local.plist"], LocalServerLaunchdUnit.Plist(Unit));
        Tmp.CreateFile(["home", ".config", "systemd", "user", "kcap-local.service"], LocalServerSystemdUnit.Unit(Unit));

        await Assert.That(new LaunchdServiceManager(home, TimeProvider.System).ListInstalled()).IsEmpty();
        await Assert.That(new SystemdServiceManager(home).ListInstalled()).IsEmpty();
        await Assert.That(LaunchdUnit.IdFromPlistFileName("io.kurrent.kcap.local.plist")).IsNull();
        await Assert.That(SystemdUnit.IdFromUnitFileName("kcap-local.service")).IsNull();
    }

    [Test]
    public async Task Preparing_the_log_directory_makes_it_owner_only() {
        if (OperatingSystem.IsWindows()) return;

        Unit.PrepareLogDirectory();

        await Assert.That(File.GetUnixFileMode(Path.GetDirectoryName(Unit.OutputLog)!))
            .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    static XElement? Value(XElement dict, string key) =>
        dict.Elements("key").FirstOrDefault(k => k.Value == key)?.ElementsAfterSelf().First();
}
