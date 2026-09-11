using System.Text.Json;
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// <see cref="AcpPermissionSurface"/> presents an ACP permission on the desktop app's local broker
/// alongside the server's web card, first answer wins. These pin: the desktop's allow/deny maps back
/// to one of the agent's own offered options by kind; a generic allow never escalates to a standing
/// grant; the server id is correlated onto the local card; every settlement is audited; an oversized
/// payload never raises a local card; and an elicitation never registers one.
/// </summary>
public class AcpPermissionSurfaceTests {
    static readonly AcpInteractionOption[] Options = [
        new("opt-allow", "Allow", null, "allow_once"),
        new("opt-reject", "Reject", null, "reject_once"),
    ];

    static AcpInteractionRequest Request(string kind = "permission", string? toolName = "Bash", AcpInteractionOption[]? options = null) =>
        new("agent-1", "sess-1", kind, toolName, null, "call-1", null, options ?? Options, false);

    // A server that never answers on its own — it resolves only when its token cancels, which is what
    // the desktop-wins path does. Lets a test guarantee the desktop is the first answer. When given a
    // server id it fires the correlation callback first, exactly as the real connection does.
    static Func<AcpInteractionRequest, Action<string>?, CancellationToken, Task<AcpInteractionDecision>> BlockingServer(string? serverRequestId = null) =>
        (_, onServerRequestId, ct) => {
            if (serverRequestId is not null) onServerRequestId?.Invoke(serverRequestId);
            var tcs = new TaskCompletionSource<AcpInteractionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => tcs.TrySetCanceled());
            return tcs.Task;
        };

    static Func<AcpInteractionRequest, Action<string>?, CancellationToken, Task<AcpInteractionDecision>> AnsweringServer(AcpInteractionDecision decision) =>
        (_, _, _) => Task.FromResult(decision);

    static async Task<PermissionPendingDto> NextPending(ChannelReader<PermissionStreamItem> reader, Func<PermissionPendingDto, bool>? until = null) {
        until ??= _ => true;
        while (await reader.WaitToReadAsync())
            while (reader.TryRead(out var item))
                if (item is PermissionStreamItem.Pending p && until(p.Dto)) return p.Dto;
        throw new InvalidOperationException("no matching pending item was broadcast");
    }

    [Test]
    public async Task DesktopAllow_MapsToTheAgentsAllowOption() {
        var broker = new PermissionPromptBroker();
        var (_, reader) = broker.Subscribe();
        var surface = new AcpPermissionSurface(broker, "copilot", BlockingServer());

        var task = surface.RequestAsync(Request(), CancellationToken.None);

        var pending = await NextPending(reader);
        await Assert.That(pending.Vendor).IsEqualTo("copilot");
        await Assert.That(pending.ToolName).IsEqualTo("Bash");
        broker.TrySettle(pending.RequestId, new PermissionDecision("allow", null, null), "allow", PermissionSettlements.SourceApp);

        var decision = await task;
        await Assert.That(decision.Outcome).IsEqualTo("allow_once");
        await Assert.That(decision.SelectedOptionId).IsEqualTo("opt-allow");
    }

    [Test]
    public async Task DesktopDeny_MapsToADenyOutcome() {
        var broker = new PermissionPromptBroker();
        var (_, reader) = broker.Subscribe();
        var surface = new AcpPermissionSurface(broker, "copilot", BlockingServer());

        var task = surface.RequestAsync(Request(), CancellationToken.None);
        var pending = await NextPending(reader);
        broker.TrySettle(pending.RequestId, PermissionSettlements.DenyDecision, "deny", PermissionSettlements.SourceApp);

        var decision = await task;
        await Assert.That(decision.Outcome).IsEqualTo("deny");
    }

    [Test]
    public async Task DesktopAllow_WithOnlyAStandingGrantOffered_FailsClosed() {
        // The desktop shows a generic "Allow"; the agent offers only a permanent grant. Honouring
        // the click would grant more than was shown, so it denies rather than escalates.
        AcpInteractionOption[] alwaysOnly = [new("opt-always", "Always allow", null, "allow_always"), new("opt-reject", "Reject", null, "reject_once")];
        var broker = new PermissionPromptBroker();
        var (_, reader) = broker.Subscribe();
        var surface = new AcpPermissionSurface(broker, "copilot", BlockingServer());

        var task = surface.RequestAsync(Request(options: alwaysOnly), CancellationToken.None);
        var pending = await NextPending(reader);
        broker.TrySettle(pending.RequestId, new PermissionDecision("allow", null, null), "allow", PermissionSettlements.SourceApp);

        var decision = await task;
        await Assert.That(decision.Outcome).IsEqualTo("deny");
        await Assert.That(decision.SelectedOptionId).IsNull();
    }

    [Test]
    public async Task ServerRequestId_IsCorrelatedOntoTheLocalCard() {
        var broker = new PermissionPromptBroker();
        var (_, reader) = broker.Subscribe();
        var surface = new AcpPermissionSurface(broker, "copilot", BlockingServer(serverRequestId: "srv-99"));

        var task = surface.RequestAsync(Request(), CancellationToken.None);

        var correlated = await NextPending(reader, until: d => d.ServerRequestId is not null);
        await Assert.That(correlated.ServerRequestId).IsEqualTo("srv-99");

        broker.TrySettle(correlated.RequestId, new PermissionDecision("allow", null, null), "allow", PermissionSettlements.SourceApp);
        await task;
    }

    [Test]
    public async Task SettledDecision_IsWrittenToTheAuditLog() {
        using var tmp = new TempDir();
        var logPath = tmp.PathTo("permission-decisions.jsonl");
        var log = new PermissionDecisionLog(tmp.Path, NullLogger.Instance);
        var broker = new PermissionPromptBroker();
        var (_, reader) = broker.Subscribe();
        var surface = new AcpPermissionSurface(broker, "copilot", BlockingServer(), log);

        var task = surface.RequestAsync(Request(), CancellationToken.None);
        var pending = await NextPending(reader);
        broker.TrySettle(pending.RequestId, new PermissionDecision("allow", null, null), "allow", PermissionSettlements.SourceApp);
        await task;

        var line = (await File.ReadAllTextAsync(logPath)).Trim();
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        await Assert.That(root.GetProperty("outcome").GetString()).IsEqualTo("allow");
        await Assert.That(root.GetProperty("source").GetString()).IsEqualTo(PermissionSettlements.SourceApp);
        await Assert.That(root.GetProperty("agent_id").GetString()).IsEqualTo("agent-1");
        await Assert.That(root.GetProperty("tool_name").GetString()).IsEqualTo("Bash");
        await Assert.That(root.GetProperty("vendor").GetString()).IsEqualTo("copilot");
    }

    [Test]
    public async Task OversizedToolName_SkipsTheDesktopCard_ServerOnly() {
        var broker = new PermissionPromptBroker();
        var surface = new AcpPermissionSurface(
            broker, "copilot", AnsweringServer(new AcpInteractionDecision("allow_once", "opt-allow", "Allow", null, null, null)));

        var giantToolName = new string('x', 4096);
        var decision = await surface.RequestAsync(Request(toolName: giantToolName), CancellationToken.None);

        await Assert.That(decision.SelectedOptionId).IsEqualTo("opt-allow");
        await Assert.That(broker.PendingSnapshot()).IsEmpty();
    }

    [Test]
    public async Task ServerAnswer_IsReturned_AndDismissesTheDesktopCard() {
        var broker  = new PermissionPromptBroker();
        var surface = new AcpPermissionSurface(
            broker, "copilot", AnsweringServer(new AcpInteractionDecision("allow_once", "opt-allow", "Allow", null, null, null)));

        var decision = await surface.RequestAsync(Request(), CancellationToken.None);

        await Assert.That(decision.SelectedOptionId).IsEqualTo("opt-allow");
        await Assert.That(broker.PendingSnapshot()).IsEmpty();
    }

    [Test]
    public async Task Elicitation_GoesStraightToTheServer_WithNoDesktopCard() {
        var broker  = new PermissionPromptBroker();
        var surface = new AcpPermissionSurface(
            broker, "copilot", AnsweringServer(new AcpInteractionDecision("answered", "opt-allow", "Allow", null, null, null)));

        var decision = await surface.RequestAsync(Request("elicitation"), CancellationToken.None);

        await Assert.That(decision.Outcome).IsEqualTo("answered");
        await Assert.That(broker.PendingSnapshot()).IsEmpty();
    }
}
