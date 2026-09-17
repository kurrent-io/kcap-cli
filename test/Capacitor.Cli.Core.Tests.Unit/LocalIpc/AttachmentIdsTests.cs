using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

/// One definition of an acceptable id list: count, syntax, distinctness under Canonical.
public class AttachmentIdsTests {
    const string A = "0123456789abcdef0123456789abcdef";
    const string B = "fedcba9876543210fedcba9876543210";

    [Test]
    public async Task Null_and_empty_lists_are_acceptable() {
        await Assert.That(AttachmentIds.Validate(null)).IsNull();
        await Assert.That(AttachmentIds.Validate([])).IsNull();
    }

    [Test]
    public async Task Ten_distinct_ids_are_acceptable_and_eleven_are_not() {
        var ten = Enumerable.Range(0, 10).Select(i => Guid.NewGuid().ToString("N")).ToArray();
        await Assert.That(AttachmentIds.Validate(ten)).IsNull();
        await Assert.That(AttachmentIds.Validate([.. ten, Guid.NewGuid().ToString("N")]))
            .IsEqualTo("up to 10 attachments per message");
    }

    [Test]
    public async Task A_malformed_or_null_element_is_refused() {
        await Assert.That(AttachmentIds.Validate([A, "nope"])).IsEqualTo("malformed attachment id");
        await Assert.That(AttachmentIds.Validate([A, null])).IsEqualTo("malformed attachment id");
    }

    [Test]
    public async Task Ids_equal_under_canonical_form_are_a_duplicate() {
        await Assert.That(AttachmentIds.Validate([A, A.ToUpperInvariant()])).IsEqualTo("duplicate attachment id");
        await Assert.That(AttachmentIds.Validate([A, B])).IsNull();
        await Assert.That(AttachmentIds.Canonical(A.ToUpperInvariant())).IsEqualTo(A);
    }
}
