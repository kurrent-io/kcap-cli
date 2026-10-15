using System.Net;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class UpdateCheckCancellationTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    /// <summary>The registry stalls forever, so the caller's token is the only thing that can end
    /// the check: a hang here means some step on the path does not take it. The cancelled fetch must
    /// still arm the backoff, which the second, answerable step proves by never being reached.</summary>
    [Test]
    public async Task Cancelling_the_token_ends_a_stalled_fetch_and_arms_the_backoff() {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new SequencedHttpScript(
            async ct => {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);

                throw new InvalidOperationException("a stalled step never answers");
            },
            SequencedHttpScript.Reply(HttpStatusCode.OK, """{"version":"0.13.0"}"""));

        using var http = new HttpClient(script) {
            BaseAddress = new Uri("http://registry.invalid/"),
            Timeout     = Timeout.InfiniteTimeSpan,
        };
        var npm = new NpmRegistryClient(http);

        using var cts = new CancellationTokenSource();
        var check = UpdateCommand.CheckForUpdateAsync(forceCheck: false, "latest", Config.Root, npm, TimeProvider.System, cts.Token);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await cts.CancelAsync();

        var first = await check.WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(first.Latest).IsNull();
        await Assert.That(first.FromCache).IsTrue();

        var second = await UpdateCommand.CheckForUpdateAsync(forceCheck: false, "latest", Config.Root, npm, TimeProvider.System);

        await Assert.That(second.Latest).IsNull();
        await Assert.That(script.Count).IsEqualTo(1);
    }
}
