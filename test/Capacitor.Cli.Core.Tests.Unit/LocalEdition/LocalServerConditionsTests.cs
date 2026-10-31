using System.Net;
using System.Net.Sockets;
using Capacitor.Cli.Core.LocalEdition;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.LocalEdition;

/// <summary>Each state an uninstall, a purge or a running command leaves on disk reads as its own condition, and only an
/// installation with its files in place reads as one a start can bring up.</summary>
public class LocalServerConditionsTests {
    [TempDir] public required TempDir Tmp { get; init; }

    LocalRoot Root => field ??= new(Tmp.PathTo("local"));

    static readonly LocalInstallation Active = new("inst-1", 51234, "http://localhost:51234", "1.4.0");

    async Task PublishAsync(InstallRegistry registry) {
        using var lease = await LifecycleLease.AcquireAsync(Root, TimeSpan.Zero, TimeProvider.System, CancellationToken.None);
        lease.PublishRegistry(registry);
    }

    void CreateBinary() {
        Directory.CreateDirectory(Path.GetDirectoryName(LocalServerConditions.ServerBinary(Root))!);
        File.WriteAllText(LocalServerConditions.ServerBinary(Root), "");
    }

    [Test]
    public async Task An_empty_root_is_not_installed() {
        await Assert.That(LocalServerConditions.Read(Root)).IsEqualTo(LocalServerCondition.NotInstalled);
    }

    [Test]
    public async Task An_installation_with_its_binary_is_installed() {
        await PublishAsync(InstallRegistry.Empty.WithActive(Active));
        CreateBinary();

        await Assert.That(LocalServerConditions.Read(Root)).IsEqualTo(LocalServerCondition.Installed);
    }

    [Test, RunOn(OS.Windows)]
    public async Task A_windows_server_binary_counts_as_installed() {
        await PublishAsync(InstallRegistry.Empty.WithActive(Active));
        Directory.CreateDirectory(Path.GetDirectoryName(LocalServerConditions.ServerBinary(Root))!);
        File.WriteAllText(LocalServerConditions.ServerBinary(Root) + ".exe", "");

        await Assert.That(LocalServerConditions.Read(Root)).IsEqualTo(LocalServerCondition.Installed);
    }

    [Test]
    public async Task A_registry_left_without_bin_is_uninstalled() {
        await PublishAsync(InstallRegistry.Empty.WithActive(Active));

        await Assert.That(LocalServerConditions.Read(Root)).IsEqualTo(LocalServerCondition.Uninstalled);
    }

    [Test]
    public async Task A_bin_without_the_server_binary_is_missing_files() {
        await PublishAsync(InstallRegistry.Empty.WithActive(Active));
        Directory.CreateDirectory(Path.Combine(Root.Directory, "bin"));

        await Assert.That(LocalServerConditions.Read(Root)).IsEqualTo(LocalServerCondition.FilesMissing);
    }

    [Test]
    public async Task A_retired_installation_with_none_active_is_purged() {
        await PublishAsync(InstallRegistry.Empty.WithActive(Active).RetireActive());

        await Assert.That(LocalServerConditions.Read(Root)).IsEqualTo(LocalServerCondition.Purged);
    }

    [Test]
    public async Task A_held_lifecycle_lock_is_changing_whatever_the_registry_says() {
        await PublishAsync(InstallRegistry.Empty.WithActive(Active));
        CreateBinary();

        using (await LifecycleLease.AcquireAsync(Root, TimeSpan.Zero, TimeProvider.System, CancellationToken.None))
            await Assert.That(LocalServerConditions.Read(Root)).IsEqualTo(LocalServerCondition.Changing);

        await Assert.That(LocalServerConditions.Read(Root)).IsEqualTo(LocalServerCondition.Installed);
    }

    [Test]
    public async Task A_journal_left_behind_is_interrupted() {
        await PublishAsync(InstallRegistry.Empty.WithActive(Active));
        CreateBinary();
        using (var lease = await LifecycleLease.AcquireAsync(Root, TimeSpan.Zero, TimeProvider.System, CancellationToken.None))
            lease.Begin("upgrade", "staging", ServerStart.Allow);

        await Assert.That(LocalServerConditions.Read(Root)).IsEqualTo(LocalServerCondition.Interrupted);
    }

    [Test]
    public async Task An_unreadable_registry_is_unreadable_not_absent() {
        Root.EnsureCreated();
        File.WriteAllText(Root.RegistryPath, "{not json");

        await Assert.That(LocalServerConditions.Read(Root)).IsEqualTo(LocalServerCondition.Unreadable);
    }

    sealed class Answering(Func<HttpResponseMessage> answer) : HttpMessageHandler {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(answer());
        }
    }

    sealed class Refusing : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused",
                new SocketException((int)SocketError.ConnectionRefused));
    }

    sealed class Silent : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely.");
    }

    sealed class Throwing(Exception ex) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw ex;
    }

    async Task InstallAsync() {
        await PublishAsync(InstallRegistry.Empty.WithActive(Active));
        CreateBinary();
    }

    Task<LocalServerReading> ReadAsync(HttpMessageHandler handler, Func<int, PortHolder?> findHolder, CancellationToken ct = default) =>
        LocalServerConditions.ReadAsync(Root, new HttpClient(handler), (port, _) => Task.FromResult(findHolder(port)), TimeProvider.System, ct);

    /// <summary>A 404 is never the kcap server's health answer, so the port is another program's even when the OS
    /// cannot say whose (another user's process, without root).</summary>
    [Test]
    public async Task A_foreign_answer_on_the_port_is_port_taken_without_naming_the_process() {
        await InstallAsync();

        var reading = await ReadAsync(new Answering(() => new HttpResponseMessage(HttpStatusCode.NotFound)), _ => null);

        await Assert.That(reading.Condition).IsEqualTo(LocalServerCondition.PortTaken);
        await Assert.That(reading.Port).IsEqualTo(Active.Port);
        await Assert.That(reading.HolderText).IsEqualTo("a web server that answers HTTP 404");
    }

    [Test]
    public async Task A_foreign_answer_names_the_holder_the_os_reports() {
        await InstallAsync();

        var reading = await ReadAsync(new Answering(() => new HttpResponseMessage(HttpStatusCode.NotFound)), _ => new(4242, "python3"));

        await Assert.That(reading.Condition).IsEqualTo(LocalServerCondition.PortTaken);
        await Assert.That(reading.HolderText).IsEqualTo("python3 (pid 4242)");
    }

    /// <summary>A listener that accepts and never answers, such as a bare TcpListener, is told apart only by the OS
    /// naming a process that is not the server.</summary>
    [Test]
    public async Task A_silent_listener_the_os_names_as_another_program_is_port_taken() {
        await InstallAsync();

        var reading = await ReadAsync(new Silent(), _ => new(812, "powershell"));

        await Assert.That(reading.Condition).IsEqualTo(LocalServerCondition.PortTaken);
        await Assert.That(reading.HolderText).IsEqualTo("powershell (pid 812)");
    }

    /// <summary>The server busy starting is silent too; a silence nobody can name stays installed, never port taken
    /// and never healthy.</summary>
    [Test]
    [Arguments("kcap-server")]
    [Arguments(null)]
    public async Task A_silence_from_the_server_or_from_nobody_known_stays_installed(string? command) {
        await InstallAsync();

        var reading = await ReadAsync(new Silent(), _ => command is null ? null : new(77, command));

        await Assert.That(reading.Condition).IsEqualTo(LocalServerCondition.Installed);
    }

    [Test]
    [Arguments(HttpStatusCode.OK)]
    [Arguments(HttpStatusCode.ServiceUnavailable)]
    public async Task An_answer_the_server_gives_stays_installed_and_looks_up_no_process(HttpStatusCode status) {
        await InstallAsync();
        var lookups = 0;

        var reading = await ReadAsync(new Answering(() => new HttpResponseMessage(status)), _ => { lookups++; return new(1, "nginx"); });

        await Assert.That(reading.Condition).IsEqualTo(LocalServerCondition.Installed);
        await Assert.That(lookups).IsEqualTo(0);
    }

    [Test]
    public async Task A_free_port_stays_installed_and_looks_up_no_process() {
        await InstallAsync();
        var lookups = 0;

        var reading = await ReadAsync(new Refusing(), _ => { lookups++; return new(1, "nginx"); });

        await Assert.That(reading.Condition).IsEqualTo(LocalServerCondition.Installed);
        await Assert.That(lookups).IsEqualTo(0);
    }

    /// <summary>The kcap server's host answers a 404 before its health route is mapped: that is the server starting,
    /// not another program, and not healthy either.</summary>
    [Test]
    public async Task A_kestrel_answer_the_health_route_never_gives_stays_installed() {
        await InstallAsync();
        var lookups = 0;

        var reading = await ReadAsync(new Answering(() => {
            var response = new HttpResponseMessage(HttpStatusCode.NotFound);
            response.Headers.TryAddWithoutValidation("Server", "Kestrel");
            return response;
        }), _ => { lookups++; return new(1, "nginx"); });

        await Assert.That(reading.Condition).IsEqualTo(LocalServerCondition.Installed);
        await Assert.That(lookups).IsEqualTo(0);
    }

    /// <summary>A lookup that fails says nothing about the port, and nothing against the installation's records:
    /// the reading is what the files say.</summary>
    [Test]
    public async Task A_process_lookup_that_fails_stays_installed() {
        await InstallAsync();

        var reading = await ReadAsync(new Silent(), _ => throw new UnauthorizedAccessException("no /proc"));

        await Assert.That(reading.Condition).IsEqualTo(LocalServerCondition.Installed);
        await Assert.That(reading.Port).IsEqualTo(Active.Port);
    }

    [Test]
    public async Task A_probe_that_fails_stays_installed() {
        await InstallAsync();

        var reading = await ReadAsync(new Throwing(new InvalidOperationException("handler disposed")), _ => null);

        await Assert.That(reading.Condition).IsEqualTo(LocalServerCondition.Installed);
    }

    [Test]
    public async Task A_cancelled_read_throws_rather_than_reading_installed() {
        await InstallAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.That(async () => await ReadAsync(new Silent(), _ => null, cts.Token)).Throws<OperationCanceledException>();
    }

    /// <summary>Only an installation a start could bring up is worth a request: a removed one is explained by its
    /// files.</summary>
    [Test]
    public async Task A_removed_installation_is_read_without_touching_the_port() {
        await PublishAsync(InstallRegistry.Empty.WithActive(Active));
        var handler = new Answering(() => new HttpResponseMessage(HttpStatusCode.NotFound));

        var reading = await ReadAsync(handler, _ => new(1, "nginx"));

        await Assert.That(reading.Condition).IsEqualTo(LocalServerCondition.Uninstalled);
        await Assert.That(handler.Requests).IsEqualTo(0);
    }

    [Test]
    public async Task Only_an_installed_server_has_no_explanation() {
        foreach (var condition in Enum.GetValues<LocalServerCondition>())
            await Assert.That(condition.Explanation is null).IsEqualTo(condition == LocalServerCondition.Installed);
    }
}
