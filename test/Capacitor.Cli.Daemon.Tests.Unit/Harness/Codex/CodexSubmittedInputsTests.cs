using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Harness.Codex;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

/// Which recorded user message a submitted input's dispatch lands on.
public class CodexSubmittedInputsTests {
    static AcpEventEnvelope User(string text) => new(Kind: AcpEventKind.UserMessage, Text: text);

    [Test]
    public async Task The_oldest_input_with_the_same_text_owns_each_message_once() {
        var inputs = new CodexSubmittedInputs(new FakeTimeProvider());
        Guid first = Guid.NewGuid(), second = Guid.NewGuid(), other = Guid.NewGuid();
        inputs.Add("again", first);
        inputs.Add("other", other);
        inputs.Add("again", second);

        await Assert.That(inputs.Stamp(User("again")).DispatchId).IsEqualTo(first);
        await Assert.That(inputs.Stamp(User("again")).DispatchId).IsEqualTo(second);
        await Assert.That(inputs.Stamp(User("again")).DispatchId).IsNull();
        await Assert.That(inputs.Stamp(User("other")).DispatchId).IsEqualTo(other);
    }

    [Test]
    public async Task An_earlier_input_without_a_dispatch_claims_its_own_message_and_leaves_the_dispatch_for_the_next() {
        var inputs = new CodexSubmittedInputs(new FakeTimeProvider());
        var dispatch = Guid.NewGuid();
        inputs.Add("same", null);
        inputs.Add("same", dispatch);

        await Assert.That(inputs.Stamp(User("same")).DispatchId).IsNull();
        await Assert.That(inputs.Stamp(User("same")).DispatchId).IsEqualTo(dispatch);
    }

    [Test]
    public async Task A_removed_input_claims_nothing_and_only_user_messages_are_stamped() {
        var inputs = new CodexSubmittedInputs(new FakeTimeProvider());
        var removed = inputs.Add("go", Guid.NewGuid());
        inputs.Remove(removed);
        inputs.Add("note", Guid.NewGuid());

        await Assert.That(inputs.Stamp(User("go")).DispatchId).IsNull();
        await Assert.That(inputs.Stamp(new AcpEventEnvelope(Kind: AcpEventKind.AssistantText, Text: "note")).DispatchId).IsNull();
    }

    [Test]
    public async Task A_burst_keeps_every_dispatch_until_its_message_arrives() {
        var inputs = new CodexSubmittedInputs(new FakeTimeProvider());
        var dispatches = Enumerable.Range(0, 200).Select(_ => Guid.NewGuid()).ToList();
        for (var i = 0; i < dispatches.Count; i++) inputs.Add($"n{i}", dispatches[i]);

        var stamped = Enumerable.Range(0, dispatches.Count).Select(i => inputs.Stamp(User($"n{i}")).DispatchId).ToList();

        await Assert.That(stamped).IsEquivalentTo(dispatches.Cast<Guid?>(), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task An_input_whose_message_never_arrived_is_dropped_once_it_expires() {
        var time = new FakeTimeProvider();
        var inputs = new CodexSubmittedInputs(time);
        inputs.Add("stale", Guid.NewGuid());
        time.Advance(CodexSubmittedInputs.Expiry - TimeSpan.FromSeconds(1));
        var fresh = Guid.NewGuid();
        inputs.Add("fresh", fresh);
        time.Advance(TimeSpan.FromSeconds(2));

        await Assert.That(inputs.Stamp(User("stale")).DispatchId).IsNull();
        await Assert.That(inputs.Stamp(User("fresh")).DispatchId).IsEqualTo(fresh);
    }
}
