using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Tests.Unit.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

public class CodexModelCatalogProbeTests {
    sealed class ExitedProcess : IAcpProcess {
        public int  Pid       => 1;
        public bool HasExited { get; private set; }
        public int? ExitCode  => 0;
        public Task WaitForExitAsync(TimeSpan? timeout = null) { HasExited = true; return Task.CompletedTask; }
        public Task TerminateAsync(TimeSpan? timeout = null) { HasExited = true; return Task.CompletedTask; }
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    sealed class BrokenPipeProcess : IAcpProcess {
        public int  Pid       => 1;
        public bool HasExited => false;
        public int? ExitCode  => null;
        public Task WaitForExitAsync(TimeSpan? timeout = null) => throw new IOException("Broken pipe");
        public Task TerminateAsync(TimeSpan? timeout = null) => throw new IOException("Broken pipe");
        public ValueTask DisposeAsync() => throw new IOException("Broken pipe");
    }

    static JsonObject Model(string model, string displayName, bool hidden = false) =>
        new() { ["id"] = model, ["model"] = model, ["displayName"] = displayName, ["hidden"] = hidden };

    static Task<IReadOnlyList<VendorModelOption>?> Probe(FakeCodexAppServer fake, IAcpProcess? process = null) =>
        CodexModelCatalogProbe.RunAsync(
            () => Task.FromResult<(CodexAppServerConnection, IAcpProcess)>((fake.ConnectClient(), process ?? new ExitedProcess())),
            "9.9.9", TimeProvider.System, NullLogger.Instance, CancellationToken.None);

    /// <summary>Pins that the advertised list is Codex's own answer for its account: every page, in
    /// Codex's order, its hidden models left out, labelled as Codex labels them.</summary>
    [Test]
    public async Task Offers_the_visible_models_codex_lists_across_pages() {
        await using var fake = new FakeCodexAppServer {
            ModelPages = [
                [Model("gpt-6-astra", "GPT-6-Astra"), Model("gpt-reserve", "Reserve", hidden: true)],
                [Model("gpt-6-sol", "GPT-6-Sol")],
            ],
        };

        var models = await Probe(fake);

        await Assert.That(models).IsEquivalentTo(new[] {
            new VendorModelOption("gpt-6-astra", "GPT-6-Astra"),
            new VendorModelOption("gpt-6-sol", "GPT-6-Sol"),
        }, CollectionOrdering.Matching);
        await Assert.That(fake.ReceivedMethods).IsEquivalentTo(new[] { "initialize", "model/list", "model/list" }, CollectionOrdering.Matching);
    }

    /// <summary>No listed model reads as no catalog, so the launcher keeps its other sources.</summary>
    [Test]
    public async Task An_empty_answer_is_no_catalog() {
        await using var fake = new FakeCodexAppServer { ModelPages = [[Model("gpt-reserve", "Reserve", hidden: true)]] };

        await Assert.That(await Probe(fake)).IsNull();
    }

    /// <summary>A partial list would override the server's catalog and hide launchable models, so an
    /// answer that is not complete — a malformed later page, or pages past the cap — is no catalog.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task An_incomplete_answer_is_no_catalog(bool malformedLaterPage) {
        await using var fake = new FakeCodexAppServer {
            ModelPages         = [.. Enumerable.Range(0, malformedLaterPage ? 2 : 11).Select(i => new JsonArray(Model($"m{i}", $"M{i}")))],
            MalformedModelPage = malformedLaterPage ? 1 : null,
        };

        await Assert.That(await Probe(fake)).IsNull();
    }

    [Test]
    [Arguments("""{"data":[],"nextCursor":123}""")]
    [Arguments("""{"data":[],"nextCursor":""}""")]
    [Arguments("""{"nextCursor":null}""")]
    [Arguments("""{"data":[]}""")]
    public async Task A_page_with_a_malformed_list_or_cursor_is_rejected(string page) {
        using var doc = JsonDocument.Parse(page);

        await Assert.That(CodexModelCatalogProbe.TryParsePage(doc.RootElement, [], out _)).IsFalse();
    }

    /// <summary>Pins that a child whose pipes broke on the way out neither fails the probe nor costs it
    /// the answer it already has.</summary>
    [Test]
    public async Task A_failing_shutdown_keeps_the_answer() {
        await using var fake = new FakeCodexAppServer { ModelPages = [[Model("gpt-6-astra", "GPT-6-Astra")]] };

        await Assert.That(await Probe(fake, new BrokenPipeProcess()))
            .IsEquivalentTo(new[] { new VendorModelOption("gpt-6-astra", "GPT-6-Astra") });
    }

    /// <summary>Pins that a child starting after the deadline is shut down rather than left running.</summary>
    [Test]
    public async Task A_child_that_starts_after_the_deadline_is_shut_down() {
        var time     = new FakeTimeProvider();
        var spawned  = new TaskCompletionSource<(CodexAppServerConnection, IAcpProcess)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process  = new ExitedProcess();
        await using var fake = new FakeCodexAppServer();

        var probe = CodexModelCatalogProbe.RunAsync(() => spawned.Task, "9.9.9", time, NullLogger.Instance, CancellationToken.None);
        time.Advance(CodexModelCatalogProbe.Deadline);

        await Assert.That(await probe.WaitAsync(WaitHarness.Bounded)).IsNull();

        spawned.SetResult((fake.ConnectClient(), process));
        await WaitHarness.PollUntilAsync(() => process.Disposed);
    }

    [Test]
    public async Task A_codex_that_cannot_start_is_no_catalog() {
        var models = await CodexModelCatalogProbe.RunAsync(
            () => Task.FromException<(CodexAppServerConnection, IAcpProcess)>(new InvalidOperationException("no codex")),
            "9.9.9", TimeProvider.System, NullLogger.Instance, CancellationToken.None);

        await Assert.That(models).IsNull();
    }
}
