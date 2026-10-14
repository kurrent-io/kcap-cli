using Capacitor.Cli.Harness.MuseAssistant;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Harness.MuseAssistant;

/// <summary>The lock is the open file handle: held for the holder's lifetime, released by the OS when
/// the holder goes away, never taken over.</summary>
public class MuseAssistantChatLockTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task A_held_lock_refuses_a_second_holder_until_released() {
        var path = Tmp.PathTo("scope", "chat.lock");

        using (var held = MuseAssistantChatLock.TryAcquire(path)) {
            await Assert.That(held).IsNotNull();
            await Assert.That(MuseAssistantChatLock.TryAcquire(path)).IsNull();
        }

        using var again = MuseAssistantChatLock.TryAcquire(path);
        await Assert.That(again).IsNotNull();
    }

    [Test]
    public async Task Acquire_waits_up_to_the_timeout_then_gives_up() {
        var path = Tmp.PathTo("chat.lock");
        var time = new FakeTimeProvider();

        using var held = MuseAssistantChatLock.TryAcquire(path);
        var waiting = MuseAssistantChatLock.AcquireAsync(path, TimeSpan.FromSeconds(1), time, default);
        for (var i = 0; !waiting.IsCompleted && i++ < 100;) { time.Advance(TimeSpan.FromMilliseconds(250)); await Task.Delay(1); }

        await Assert.That(await waiting).IsNull();
    }

    [Test]
    public async Task Acquire_succeeds_once_the_holder_lets_go() {
        var path = Tmp.PathTo("chat.lock");
        var held = MuseAssistantChatLock.TryAcquire(path);
        var time = new FakeTimeProvider();

        var waiting = MuseAssistantChatLock.AcquireAsync(path, TimeSpan.FromSeconds(60), time, default);
        held!.Dispose();
        for (var i = 0; !waiting.IsCompleted && i++ < 100;) { time.Advance(TimeSpan.FromMilliseconds(250)); await Task.Delay(1); }

        using var acquired = await waiting;
        await Assert.That(acquired).IsNotNull();
    }
}
