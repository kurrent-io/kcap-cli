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
    public async Task An_upgrade_is_in_progress_until_its_lease_is_released() {
        Publish();
        using (var lease = await LifecycleLease.AcquireAsync(Root, TimeSpan.Zero, TimeProvider.System, CancellationToken.None)) {
            lease.Begin("upgrade", "staging", ServerStart.Allow);
            var journal = File.ReadAllText(Root.JournalPath);

            var active = Text(await Read());

            await Assert.That(active).Contains("in progress");
            await Assert.That(active).DoesNotContain("interrupted");
            await Assert.That(active).DoesNotContain("finishes or undoes");
            await Assert.That(File.ReadAllText(Root.JournalPath)).IsEqualTo(journal);
        }

        await Assert.That(Text(await Read())).Contains("interrupted upgrade");
    }

    [Test]
    public async Task The_search_field_names_the_embedding_model_or_says_keyword_only() {
        Running();

        await Assert.That(Text(await Read())).Contains("Search: by keyword only");

        LocalEmbeddingFile.Write(Root, LocalEmbeddingSetting.Ollama("mxbai-embed-large"));

        await Assert.That(Text(await Read())).Contains("Search: by meaning through Ollama with mxbai-embed-large while it answers, and by keyword");
        await Assert.That(Text(await Read())).DoesNotContain("not yet applied");

        LocalEmbeddingFile.Write(Root, LocalEmbeddingSetting.Ollama("mxbai-embed-large") with { Pending = true });

        await Assert.That(Text(await Read())).Contains("(not yet applied; kcap setup --local applies it)");
    }

    [Test]
    public async Task The_search_field_names_a_recorded_builtin_model_without_ollama() {
        Running();
        LocalEmbeddingFile.Write(Root, LocalEmbeddingSetting.Builtin(new string('a', 64)));

        var text = Text(await Read());

        await Assert.That(text).Contains("Search: by meaning with the built-in model, and by keyword");
        await Assert.That(text).DoesNotContain("Ollama");
    }

    const string Meaning = "Search: by meaning with the built-in model, and by keyword";

    const string OllamaLine = "Search: by meaning through Ollama with mxbai-embed-large while it answers, and by keyword";

    /// <summary>The server's own account of the built-in model wins over the recorded file whenever it answers; a 404
    /// from an older server or a silent one leaves the file's wording.</summary>
    [Test]
    [Arguments("""{"provider":"builtin","usable":true,"reason":null,"probe_head":[0.1,0.2,0.3,0.4]}""", Meaning)]
    [Arguments("""{"provider":"builtin","usable":false,"reason":"model_missing","probe_head":null}""",
        "Search: by keyword only: the built-in model is not usable (model_missing); run kcap setup --local")]
    [Arguments("""{"provider":"builtin","usable":false,"reason":"digest_mismatch","probe_head":null}""",
        "Search: by keyword only: the built-in model is not usable (digest_mismatch); run kcap setup --local")]
    [Arguments("""{"provider":"builtin","usable":false,"reason":"load_failed","probe_head":null}""",
        "Search: by keyword only: the built-in model is not usable (load_failed); run kcap local restart, and see kcap local logs if it persists")]
    [Arguments("""{"provider":"builtin","usable":false,"reason":"probe_failed","probe_head":null}""",
        "Search: by keyword only: the built-in model is not usable (probe_failed); run kcap local restart, and see kcap local logs if it persists")]
    [Arguments("""{"provider":"builtin","usable":false,"reason":null,"probe_head":null}""",
        "Search: by keyword only: the built-in model is not loaded yet")]
    [Arguments("""{"provider":"ollama","usable":true,"reason":null,"probe_head":null}""", OllamaLine)]
    [Arguments("{nope", OllamaLine)]
    [Arguments(null, OllamaLine)]
    public async Task The_search_field_follows_the_servers_embedding_health(string? body, string expected) {
        Running();
        Health(HttpStatusCode.OK);
        LocalEmbeddingFile.Write(Root, LocalEmbeddingSetting.Ollama("mxbai-embed-large"));

        var ready = Server.Override;
        Server.Override = path => path == "/healthz/embeddings"
            ? body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
            : ready!(path);

        await Assert.That(Text(await Read())).Contains(expected);
    }

    [Test]
    public async Task A_builtin_search_field_survives_a_server_that_does_not_answer_embeddings() {
        Running();
        Health(HttpStatusCode.OK);
        LocalEmbeddingFile.Write(Root, LocalEmbeddingSetting.Builtin(new string('a', 64)));

        var ready = Server.Override;
        Server.Override = path => path == "/healthz/embeddings" ? throw new HttpRequestException("refused") : ready!(path);

        await Assert.That(Text(await Read())).Contains(Meaning);
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
        Tmp.CreateDir("r/bin");

        await Assert.That(Text(await Read())).Contains("files are missing").And.Contains("kcap setup --local");
    }

    /// <summary>An uninstall removes the unit and <c>bin/</c> but keeps the registry: that is not a broken install, and
    /// no start can bring it back.</summary>
    [Test]
    public async Task An_uninstalled_installation_says_setup_reinstalls_it_and_offers_no_start() {
        Publish(withBinary: false);

        var report = await Read();

        await Assert.That(report.Headline).StartsWith("Uninstalled:");
        await Assert.That(Text(report)).Contains("kcap setup --local reinstalls it on the same port");
        await Assert.That(Text(report)).DoesNotContain("kcap local start starts it").And.DoesNotContain("installs and starts it");
    }

    [Test]
    public async Task A_purged_installation_names_its_retired_url_and_offers_no_start() {
        Root.EnsureCreated();
        LocalStateFiles.PublishRegistry(Root, InstallRegistry.Empty.WithActive(Active).RetireActive());

        var report = await Read();

        await Assert.That(report.Headline).StartsWith("Purged:");
        await Assert.That(Text(report)).Contains("Retired: http://localhost:9473").And.Contains("on a new port");
        await Assert.That(Text(report)).DoesNotContain("kcap local start");
    }

    /// <summary>A stranger on the port is reported, never stopped, and a start that could not listen is not offered as
    /// the fix.</summary>
    [Test]
    public async Task A_stopped_server_whose_port_another_process_holds_names_it_instead_of_offering_start() {
        Running();
        await Service.StopAsync(Unit, CancellationToken.None);
        _holders.Add(new(4242, "python3"));
        var calls = _log.Count;

        var report = await Read();

        await Assert.That(report.Headline).IsEqualTo("Stopped, and port 9473 is taken by another process.");
        await Assert.That(Text(report)).Contains("taken by python3 (pid 4242)").And.Contains("never stops a process it did not start");
        await Assert.That(Text(report)).DoesNotContain("kcap local start starts it");
        await Assert.That(_log.Skip(calls).Any(l => l.Contains("stop", StringComparison.OrdinalIgnoreCase))).IsFalse();
    }

    /// <summary>A restart passes through stopped on its way; while another command holds the lifecycle lock, status
    /// says so instead of recommending a start.</summary>
    [Test]
    public async Task A_stopped_server_under_another_commands_lock_reads_as_changing() {
        Running();
        await Service.StopAsync(Unit, CancellationToken.None);

        using (await LifecycleLease.AcquireAsync(Root, TimeSpan.Zero, TimeProvider.System, CancellationToken.None)) {
            var report = await Read();

            await Assert.That(report.Headline).StartsWith("Changing:");
            await Assert.That(Text(report)).DoesNotContain("kcap local start starts it");
        }

        await Assert.That((await Read()).Headline).IsEqualTo("Stopped.");
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
