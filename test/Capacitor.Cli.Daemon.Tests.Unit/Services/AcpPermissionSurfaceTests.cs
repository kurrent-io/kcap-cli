using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// <see cref="AcpPermissionSurface"/> presents an ACP permission on the desktop app's local broker
/// alongside the server's web card, first answer wins. These pin: the desktop's allow/deny maps back
/// to one of the agent's own offered options by kind; the server's answer is returned and dismisses
/// the desktop card; and an elicitation never registers a card at all.
/// </summary>
public class AcpPermissionSurfaceTests {
    static readonly AcpInteractionOption[] Options = [
        new("opt-allow", "Allow", null, "allow_once"),
        new("opt-reject", "Reject", null, "reject_once"),
    ];

    static AcpInteractionRequest Request(string kind = "permission") =>
        new("agent-1", "sess-1", kind, "Bash", null, "call-1", null, Options, false);

    // A server that never answers on its own — it resolves only when its token cancels, which is what
    // the desktop-wins path does. Lets a test guarantee the desktop is the first answer.
    static Func<AcpInteractionRequest, CancellationToken, Task<AcpInteractionDecision>> BlockingServer() =>
        (_, ct) => {
            var tcs = new TaskCompletionSource<AcpInteractionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => tcs.TrySetCanceled());
            return tcs.Task;
        };

    static async Task<PermissionPendingDto> NextPending(ChannelReader<PermissionStreamItem> reader) {
        while (await reader.WaitToReadAsync())
            while (reader.TryRead(out var item))
                if (item is PermissionStreamItem.Pending p) return p.Dto;
        throw new InvalidOperationException("no pending item was broadcast");
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
    public async Task ServerAnswer_IsReturned_AndDismissesTheDesktopCard() {
        var broker  = new PermissionPromptBroker();
        var surface = new AcpPermissionSurface(
            broker, "copilot", (_, _) => Task.FromResult(new AcpInteractionDecision("allow_once", "opt-allow", "Allow", null, null, null)));

        var decision = await surface.RequestAsync(Request(), CancellationToken.None);

        await Assert.That(decision.SelectedOptionId).IsEqualTo("opt-allow");
        await Assert.That(broker.PendingSnapshot()).IsEmpty();
    }

    [Test]
    public async Task Elicitation_GoesStraightToTheServer_WithNoDesktopCard() {
        var broker  = new PermissionPromptBroker();
        var surface = new AcpPermissionSurface(
            broker, "copilot", (_, _) => Task.FromResult(new AcpInteractionDecision("answered", "opt-allow", "Allow", null, null, null)));

        var decision = await surface.RequestAsync(Request("elicitation"), CancellationToken.None);

        await Assert.That(decision.Outcome).IsEqualTo("answered");
        await Assert.That(broker.PendingSnapshot()).IsEmpty();
    }
}
