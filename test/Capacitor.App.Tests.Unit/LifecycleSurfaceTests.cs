using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

/// LifecycleSurface is Avalonia-free (the composition root supplies the dialog factory), so none
/// of these need AvaloniaSession.
public class LifecycleSurfaceTests {
    static LifecyclePrompt Prompt(string kind) => new(kind, "1.0.0", "1.1.0", false, "disclosure text");

    static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null) {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met in time");
            await Task.Delay(10);
        }
    }

    /// Records every showPrompt invocation and hands back a caller-controlled TaskCompletionSource
    /// per call, so a test can pin exactly when each dialog "resolves" without any real window.
    sealed class ScriptedPromptShower {
        public readonly List<(LifecyclePrompt Prompt, CancellationToken Ct, TaskCompletionSource<bool> Tcs)> Calls = [];

        public Task<bool> ShowAsync(LifecyclePrompt prompt, CancellationToken ct) {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Calls.Add((prompt, ct, tcs));
            return tcs.Task;
        }
    }

    [Test]
    public async Task ConfirmAsync_serializes_a_second_call_until_the_first_resolves() {
        var shower = new ScriptedPromptShower();
        var surface = new LifecycleSurface(_ => { }, _ => { }, shower.ShowAsync);

        var task1 = surface.ConfirmAsync(Prompt(LifecyclePrompt.KindRepair), CancellationToken.None);
        await WaitUntilAsync(() => shower.Calls.Count == 1);

        var task2 = surface.ConfirmAsync(Prompt(LifecyclePrompt.KindTakeover), CancellationToken.None);
        await Task.Delay(50); // give a broken (non-serializing) implementation a chance to show it early
        await Assert.That(shower.Calls.Count).IsEqualTo(1);

        shower.Calls[0].Tcs.SetResult(true);
        var result1 = await task1.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result1).IsTrue();

        await WaitUntilAsync(() => shower.Calls.Count == 2, TimeSpan.FromSeconds(5));
        shower.Calls[1].Tcs.SetResult(false);
        var result2 = await task2.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result2).IsFalse();
    }

    /// A cancelled dialog must resolve false AND release the gate, or a follow-up ConfirmAsync
    /// deadlocks behind it forever.
    [Test]
    public async Task ConfirmAsync_cancelled_dialog_resolves_false_and_releases_the_gate() {
        var shower = new ScriptedPromptShower();
        var surface = new LifecycleSurface(_ => { }, _ => { }, shower.ShowAsync);

        using var cts = new CancellationTokenSource();
        var task1 = surface.ConfirmAsync(Prompt(LifecyclePrompt.KindRepair), cts.Token);
        await WaitUntilAsync(() => shower.Calls.Count == 1);

        cts.Cancel();
        // Simulates WireDialogCancellation closing the real window: the dialog factory resolves
        // its own tcs false on cancellation rather than ConfirmAsync throwing.
        shower.Calls[0].Tcs.TrySetResult(false);

        var result1 = await task1.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result1).IsFalse();

        var task2 = surface.ConfirmAsync(Prompt(LifecyclePrompt.KindTakeover), CancellationToken.None);
        await WaitUntilAsync(() => shower.Calls.Count == 2, TimeSpan.FromSeconds(5));
        shower.Calls[1].Tcs.SetResult(true);
        var result2 = await task2.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result2).IsTrue();
    }

    /// A cancel that lands while still QUEUED (the first dialog is still open, so the gate hasn't
    /// even been acquired yet) must also resolve false without ever calling showPrompt — and must
    /// not disturb the first call's own eventual resolution.
    [Test]
    public async Task ConfirmAsync_cancelled_while_queued_resolves_false_without_showing_a_dialog() {
        var shower = new ScriptedPromptShower();
        var surface = new LifecycleSurface(_ => { }, _ => { }, shower.ShowAsync);

        var task1 = surface.ConfirmAsync(Prompt(LifecyclePrompt.KindRepair), CancellationToken.None);
        await WaitUntilAsync(() => shower.Calls.Count == 1);

        using var cts = new CancellationTokenSource();
        var task2 = surface.ConfirmAsync(Prompt(LifecyclePrompt.KindTakeover), cts.Token);
        cts.Cancel();

        var result2 = await task2.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result2).IsFalse();
        await Assert.That(shower.Calls.Count).IsEqualTo(1); // task2 never showed

        shower.Calls[0].Tcs.SetResult(true);
        var result1 = await task1.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result1).IsTrue();
    }

    // TryConfirmAsync distinguishes "never reached the factory" (null) from a shown-and-declined
    // dialog (false); ConfirmAsync collapses both to false.
    [Test]
    public async Task TryConfirmAsync_cancelled_while_queued_returns_null_without_showing_a_dialog() {
        var shower = new ScriptedPromptShower();
        var surface = new LifecycleSurface(_ => { }, _ => { }, shower.ShowAsync);

        var task1 = surface.ConfirmAsync(Prompt(LifecyclePrompt.KindRepair), CancellationToken.None);
        await WaitUntilAsync(() => shower.Calls.Count == 1);

        using var cts = new CancellationTokenSource();
        var task2 = surface.TryConfirmAsync(Prompt(LifecyclePrompt.KindTakeover), cts.Token);
        cts.Cancel();

        var result2 = await task2.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result2).IsNull();
        await Assert.That(shower.Calls.Count).IsEqualTo(1); // task2 never showed

        shower.Calls[0].Tcs.SetResult(true);
        var result1 = await task1.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result1).IsTrue();
    }

    [Test]
    public async Task TryConfirmAsync_returns_the_dialogs_result_for_a_genuinely_shown_dialog() {
        var surface = new LifecycleSurface(_ => { }, _ => { }, (_, _) => Task.FromResult(false));

        var result = await surface.TryConfirmAsync(Prompt(LifecyclePrompt.KindRepair), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(result).IsFalse();
    }
}
