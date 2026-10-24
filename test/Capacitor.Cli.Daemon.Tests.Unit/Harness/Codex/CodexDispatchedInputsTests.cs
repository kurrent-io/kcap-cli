using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Harness.Codex;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

/// Which recorded user message a dispatched input's id lands on.
public class CodexDispatchedInputsTests {
    static AcpEventEnvelope User(string text) => new(Kind: AcpEventKind.UserMessage, Text: text);

    [Test]
    public async Task The_oldest_input_with_the_same_text_owns_each_message_once() {
        var inputs = new CodexDispatchedInputs();
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
    public async Task A_removed_input_claims_nothing_and_only_user_messages_are_stamped() {
        var inputs = new CodexDispatchedInputs();
        var removed = inputs.Add("go", Guid.NewGuid());
        inputs.Remove(removed);
        inputs.Add("note", Guid.NewGuid());

        await Assert.That(inputs.Stamp(User("go")).DispatchId).IsNull();
        await Assert.That(inputs.Stamp(new AcpEventEnvelope(Kind: AcpEventKind.AssistantText, Text: "note")).DispatchId).IsNull();
    }

    [Test]
    public async Task Past_capacity_the_oldest_unclaimed_input_is_evicted() {
        var inputs = new CodexDispatchedInputs();
        inputs.Add("stale", Guid.NewGuid());
        for (var i = 0; i < CodexDispatchedInputs.Capacity; i++) inputs.Add($"n{i}", Guid.NewGuid());

        await Assert.That(inputs.Stamp(User("stale")).DispatchId).IsNull();
        await Assert.That(inputs.Stamp(User("n0")).DispatchId).IsNotNull();
    }
}
