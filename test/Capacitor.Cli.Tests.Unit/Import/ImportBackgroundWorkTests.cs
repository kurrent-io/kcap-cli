using System.Collections.Concurrent;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Import;

/// <summary>
/// Guards the background-await decision in HandleImport. The variable this helper backs
/// gates both awaiting Task.WhenAll(backgroundTasks) (so the shared SemaphoreSlim isn't
/// disposed with tasks still running) and the Done-summary Titles/Summaries rows. A single
/// missing negation here caused an intermittent ObjectDisposedException in unit runs, so the
/// decision is pinned directly.
/// </summary>
public class ImportBackgroundWorkTests {
    [Test]
    public async Task HadBackgroundWork_is_false_for_an_empty_bag() {
        await Assert.That(ImportCommand.HadBackgroundWork(new ConcurrentBag<Task>())).IsFalse();
    }

    [Test]
    public async Task HadBackgroundWork_is_true_when_tasks_were_scheduled() {
        var bag = new ConcurrentBag<Task> { Task.CompletedTask };

        await Assert.That(ImportCommand.HadBackgroundWork(bag)).IsTrue();
    }
}
