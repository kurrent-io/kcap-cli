using System.Net;
using System.Net.Sockets;
using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;
using Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

/// <summary>Each state a crash, a failed stop or a taken port can leave the stack in reads as a verdict and what to
/// do; only a running, committed, ready server reads as ready.</summary>
[ExcludeOn(OS.Windows)]
public class LocalStatusReaderTests {
    // Short: a test binds the Postgres socket under the root, and the path has to fit in sockaddr_un.
    [TempDir("lsr")] public required TempDir Tmp { get; init; }

    readonly List<string> _log = [];

    readonly List<PortHolder> _holders = [];

    UserHome Home => field ??= NewHome();

    LocalRoot Root => field ??= LocalRoot.Resolve(Tmp.PathTo("r"), Home);

    FakeLocalServerService Service => field ??= new(_log, Home);

    FakeLocalServer Server => field ??= new(_log, Root);

    LocalServerUnit Unit => LocalServerUnit.ForInstallation(Root, Active.Port);

    static readonly LocalInstallation Active = new("abc123", 9473, "http://localhost:9473", "1.2.3");

    UserHome NewHome() {
        Tmp.CreateDir("home");
        return new(Tmp.GetResolvedPath("home"));
    }

    Task<LocalStatusReport> Read(string cliVersion = "1.2.3") =>
        new LocalStatusReader(new LocalStack(Root, Home, Service, new HttpClient(Server), TimeProvider.System, new FakeOutgoingRecorders([]), new ConfigRoot(Tmp.PathTo("cfg"))), cliVersion, _ => _holders.FirstOrDefault())
            .ReadAsync(CancellationToken.None);

    void Publish(bool withBinary = true) {
        Root.EnsureCreated();
        LocalStateFiles.PublishRegistry(Root, InstallRegistry.Empty.WithActive(Active));

        if (withBinary) Tmp.CreateFile(["r", "bin", "current", "kcap-server"]);
    }

    void Running() {
        Publish();
        Service.Install(Unit);
    }

    void Health(HttpStatusCode code, string kurrentdb = "Healthy", string postgres = "Healthy", string? why = null) {
        var description = why is null ? "null" : $"\"{why}\"";
        var body = "{\"status\":\"x\",\"results\":{"
                 + $"\"kurrentdb\":{{\"status\":\"{kurrentdb}\"}},"
                 + $"\"postgres\":{{\"status\":\"{postgres}\",\"description\":{description}}}"
                 + "}}";

        Server.Override = path => path == "/healthz/ready"
            ? new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
            : null;
    }

    static string Text(LocalStatusReport report) =>
        string.Join("\n", [report.Headline, .. report.Fields.Select(f => $"{f.Key}: {f.Value}"), .. report.Notes]);

    [Test]
    public async Task The_search_field_names_the_embedding_model_or_says_keyword_only() {
        Running();

        await Assert.That(Text(await Read())).Contains("Search: by keyword only");

        LocalEmbeddingFile.Write(Root, "mxbai-embed-large");

        await Assert.That(Text(await Read())).Contains("Search: by meaning and keyword, embedding with mxbai-embed-large through Ollama");
    }

    [Test]
    public async Task No_installation_says_how_to_make_one() {
        var report = await Read();

        await Assert.That(report.Ready).IsFalse();
        await Assert.That(report.Headline).Contains("No local installation");
        await Assert.That(Text(report)).Contains("kcap setup --local");
    }

    [Test]
    public async Task An_unreadable_registry_is_named() {
        Root.EnsureCreated();
        File.WriteAllText(Root.RegistryPath, "{not json");

        await Assert.That((await Read()).Headline).Contains(Root.RegistryPath);
    }

    [Test]
    public async Task An_interrupted_setup_says_the_next_lifecycle_command_recovers_it() {
        Root.EnsureCreated();
        using (var lease = await LifecycleLease.AcquireAsync(Root, TimeSpan.Zero, TimeProvider.System, CancellationToken.None))
            lease.Begin(LocalSetupJournal.Operation, LocalSetupJournal.Pending, ServerStart.Fenced);

        var report = await Read();

        await Assert.That(report.Headline).Contains("interrupted setup").And.Contains("pending");
        await Assert.That(Text(report)).Contains("kcap local start");
    }

    [Test]
    public async Task An_installation_with_no_unit_is_started_by_kcap_local_start() {
        Publish();

        var report = await Read();

        await Assert.That(report.Headline).Contains("no fake unit is installed");
        await Assert.That(Text(report)).Contains("kcap local start installs and starts it");
    }

    [Test]
    public async Task Missing_server_files_send_the_user_to_setup() {
        Publish(withBinary: false);

        await Assert.That(Text(await Read())).Contains("files are missing").And.Contains("kcap setup --local");
    }

    [Test]
    public async Task A_unit_serving_another_root_names_it() {
        Publish();
        var other = Tmp.PathTo("elsewhere");
        Service.OwnerOverride = new(new OwnerReading.Root(other), new OwnerReading.Root(other));

        var report = await Read();

        await Assert.That(report.Headline).Contains("serves another installation");
        await Assert.That(Text(report)).Contains($"KCAP_LOCAL_ROOT={other}");
    }

    [Test]
    public async Task A_unit_naming_another_existing_spelling_is_explained_not_guessed() {
        Publish();
        var before = Directory.GetFileSystemEntries(Root.Directory);
        Service.OwnerOverride = new(new OwnerReading.Root(Tmp.CreateDir("other")), OwnerReading.Absent.Instance);

        var report = await Read();

        await Assert.That(report.Headline).Contains("may be this root by another spelling");
        await Assert.That(Text(report)).Contains("under the lifecycle lock");
        await Assert.That(Directory.GetFileSystemEntries(Root.Directory)).IsEquivalentTo(before);
    }

    [Test]
    public async Task An_unreadable_unit_names_the_reason() {
        Publish();
        Service.OwnerOverride = new(new OwnerReading.Unreadable("launchctl print failed"), OwnerReading.Absent.Instance);

        var report = await Read();

        await Assert.That(report.Headline).Contains("could not be read");
        await Assert.That(Text(report)).Contains("launchctl print failed");
    }

    [Test]
    public async Task A_stopped_server_says_how_to_start_it() {
        Running();
        await Service.StopAsync(Unit, CancellationToken.None);

        var report = await Read();

        await Assert.That(report.Headline).IsEqualTo("Stopped.");
        await Assert.That(Text(report)).Contains("kcap local start starts it");
    }

    [Test]
    public async Task A_stopped_server_that_left_postgres_alive_says_so() {
        Running();
        await Service.StopAsync(Unit, CancellationToken.None);
        Tmp.CreateDir("r/run");

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(Path.Combine(Root.Directory, "run", LocalStackProbe.PostgresSocketName)));
        listener.Listen();

        var report = await Read();

        await Assert.That(Text(report)).Contains("Though nothing runs the server, Postgres still accepts connections").And.Contains("Postgres: up");
    }

    /// <summary>A probe of the instance lock holds it for a moment, and a concurrent start could be bringing the server
    /// up in that moment; held here, any probe would read it as a live server.</summary>
    [Test]
    public async Task Status_never_probes_the_instance_lock() {
        Running();
        await Service.StopAsync(Unit, CancellationToken.None);
        var lockPath = Tmp.CreateFile(["r", "run", LocalStackProbe.ServerLockName]);

        using var held = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        await Assert.That(Text(await Read())).DoesNotContain("instance lock");
    }

    [Test]
    public async Task A_ready_server_reports_each_store_up() {
        Running();
        Health(HttpStatusCode.OK);

        var report = await Read();

        await Assert.That(report.Ready).IsTrue();
        await Assert.That(report.Headline).IsEqualTo("Ready on http://localhost:9473.");
        await Assert.That(Text(report)).Contains("KurrentDB: up").And.Contains("Postgres: up").And.Contains("Version: 1.2.3");
    }

    [Test]
    public async Task A_store_that_is_down_is_named_with_its_reason() {
        Running();
        Health(HttpStatusCode.ServiceUnavailable, postgres: "Unhealthy", why: "connection refused");

        var report = await Read();

        await Assert.That(report.Ready).IsFalse();
        await Assert.That(report.Headline).Contains("a store is down");
        await Assert.That(Text(report)).Contains("Postgres: unhealthy: connection refused").And.Contains("KurrentDB: up");
    }

    [Test]
    public async Task A_running_server_that_does_not_answer_yet_is_starting() {
        Running();
        Server.Override = _ => throw new HttpRequestException("Connection refused");

        var report = await Read();

        await Assert.That(report.Headline).Contains("Starting: the server does not answer on port 9473");
        await Assert.That(Text(report)).Contains("kcap local logs");
    }

    [Test]
    public async Task A_port_another_process_holds_is_named() {
        Running();
        Server.Override = _ => throw new HttpRequestException("Connection refused");
        _holders.Add(new(1, "nginx"));

        var report = await Read();

        await Assert.That(report.Headline).Contains("port 9473 is taken");
        await Assert.That(Text(report)).Contains("taken by nginx (pid 1)");
    }

    [Test]
    public async Task A_server_that_keeps_exiting_on_a_taken_port_says_both() {
        Running();
        Service.ActivityOverride = new UnitActivity.Restarting(1);
        _holders.Add(new(1, "nginx"));

        var report = await Read();

        await Assert.That(report.Headline).Contains("exited with code 1 and the fake unit is starting it again");
        await Assert.That(Text(report)).Contains("taken by nginx");
    }

    [Test]
    public async Task A_server_that_exited_for_good_says_restart() {
        Running();
        Service.ActivityOverride = new UnitActivity.Exited(0);

        var report = await Read();

        await Assert.That(report.Headline).Contains("will not be started again");
        await Assert.That(Text(report)).Contains("kcap local restart");
    }

    [Test]
    public async Task A_server_still_fenced_is_not_ready() {
        Running();
        Health(HttpStatusCode.OK);
        Server.WhileAllowed = FakeAuthConfig.Pending;

        var report = await Read();

        await Assert.That(report.Ready).IsFalse();
        await Assert.That(report.Headline).Contains("Running fenced");
    }

    [Test]
    public async Task A_stranger_answering_on_the_port_is_not_ready() {
        Running();
        Health(HttpStatusCode.OK);
        Server.WhileAllowed = FakeAuthConfig.Forbidden;

        var report = await Read();

        await Assert.That(report.Ready).IsFalse();
        await Assert.That(report.Headline).Contains("does not answer as this installation");
    }

    [Test]
    public async Task A_stack_behind_this_kcap_says_so() {
        Running();
        Health(HttpStatusCode.OK);

        var report = await Read(cliVersion: "1.3.0");

        await Assert.That(report.Ready).IsTrue();
        await Assert.That(Text(report)).Contains("The stack is behind this kcap (1.3.0)");
    }
}
