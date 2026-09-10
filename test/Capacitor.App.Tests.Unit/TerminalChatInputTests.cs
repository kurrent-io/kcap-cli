using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class TerminalChatInputTests {
    static async Task<(TerminalTabViewModel Terminal, TerminalChatInput Input, FakeTerminalAttachClient Client, FakeTimeProvider Time)> BuildAttachedAsync() {
        var daemon = new FakeDaemonClientService();
        var factory = new FakeTerminalAttachClientFactory();
        var time = new FakeTimeProvider();
        var terminal = new TerminalTabViewModel("a1", daemon, factory.Factory, () => new FakeTerminalSurface(), time);
        var input = new TerminalChatInput(terminal);
        daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { Status = "Running" });
        Dispatcher.UIThread.RunJobs();
        await (terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
        var client = factory.Created.Single();
        await client.TriggerAttached([]);
        return (terminal, input, client, time);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Mirrors_the_terminal_availability_and_wording() {
        await RunOnUiAsync(async () => {
            var (terminal, input, client, time) = await BuildAttachedAsync();
            await Assert.That(input.Availability).IsEqualTo(SendAvailability.Ready);
            await Assert.That(input.Hint).IsEqualTo("Enter sends · Shift+Enter for a new line");
            await Assert.That(input.CanAcceptText).IsTrue();

            var raised = new List<string>();
            input.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);
            await Assert.That(await input.SendAsync("hello", CancellationToken.None)).IsTrue();
            await Assert.That(input.Availability).IsEqualTo(SendAvailability.Sending);
            await Assert.That(input.Hint).IsEqualTo("Sending…");
            await Assert.That(raised).Contains(nameof(ChatInput.Availability));

            time.Advance(TimeSpan.FromMilliseconds(150));
            await terminal.PendingDeliveryForTesting!;
            await Assert.That(input.Availability).IsEqualTo(SendAvailability.Ready);
            await Assert.That(client.SentInput).Count().IsEqualTo(2);
            input.Dispose();
            await terminal.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Dispose_detaches_from_the_terminal() {
        await RunOnUiAsync(async () => {
            var (terminal, input, client, _) = await BuildAttachedAsync();
            input.Dispose();
            var raised = 0;
            input.PropertyChanged += (_, _) => raised++;
            client.Result.SetResult(new AttachOutcome.Exited(0));
            await terminal.CurrentRunForTesting!;
            await Assert.That(raised).IsEqualTo(0);
            await Assert.That(await input.SendAsync("x", CancellationToken.None)).IsFalse();
            await terminal.TeardownAsync();
        });
    }
}
