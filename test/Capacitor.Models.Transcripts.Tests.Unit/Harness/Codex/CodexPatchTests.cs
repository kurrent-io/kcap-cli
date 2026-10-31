using Capacitor.Models.Transcripts.Harness.Codex;
using TUnit.Assertions.Enums;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Codex;

/// Which files an apply_patch envelope targets: every add, update and delete header, in order,
/// once each, and never one of the envelope's control lines.
public class CodexPatchTests {
    [Test]
    public async Task Reads_every_add_update_and_delete_target_in_order() {
        const string patch = """
            *** Begin Patch
            *** Update File: src/a.cs
            @@ class A
            -old
            +new
            *** Add File: docs/b.md
            +hello
            *** Delete File: c.txt
            *** Update File: src/a.cs
            @@
            -x
            +y
            *** End Patch
            """;

        await Assert.That(CodexPatch.Targets(patch)).IsEquivalentTo(["src/a.cs", "docs/b.md", "c.txt"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_move_names_the_updated_file_and_not_a_second_target() {
        const string patch = "*** Begin Patch\n*** Update File: old/a.cs\n*** Move to: new/a.cs\n@@\n-x\n+y\n*** End Patch";

        await Assert.That(CodexPatch.Targets(patch)).IsEquivalentTo(new[] { "old/a.cs" });
    }

    [Test]
    public async Task Crlf_lines_and_leading_blank_lines_still_parse() {
        const string patch = "\r\n*** Begin Patch\r\n*** Update File: src/a.cs  \r\n@@\r\n-x\r\n+y\r\n*** End Patch\r\n";

        await Assert.That(CodexPatch.Targets(patch)).IsEquivalentTo(new[] { "src/a.cs" });
    }

    /// A context line repeating a header's text belongs to the file being patched.
    [Test]
    public async Task A_hunk_line_holding_header_text_is_not_a_target() {
        const string patch = "*** Begin Patch\n*** Update File: notes.md\n@@\n *** Update File: quoted.md\n-*** Delete File: gone.md\n+*** Add File: added.md\n*** End Patch";

        await Assert.That(CodexPatch.Targets(patch)).IsEquivalentTo(new[] { "notes.md" });
    }

    [Test]
    [Arguments("*** Begin Patch\n*** End Patch")]
    [Arguments("*** Begin Patch\n*** Update File:   \n*** End Patch")]
    [Arguments("*** Begin Patch\n@@\n+orphan hunk")]
    public async Task A_patch_naming_no_file_has_no_targets(string patch) {
        await Assert.That(CodexPatch.Targets(patch)).IsNotNull().And.IsEmpty();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("git status")]
    [Arguments("echo hi\n*** Begin Patch\n*** Update File: a.cs\n*** End Patch")]
    public async Task Text_that_is_not_a_patch_envelope_is_not_read(string? text) {
        await Assert.That(CodexPatch.Targets(text)).IsNull();
    }
}
