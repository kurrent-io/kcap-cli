using System.Text.Json;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Policy;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Codex;

/// <summary>A Codex hook's call as the policy sees it: a shell command by its command, a patch by every file its
/// headers name, so a path rule binds an edit Codex makes unasked.</summary>
public class CodexHookActionsTests {
    static JsonElement Input(string command) =>
        JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string> { ["command"] = command })).RootElement.Clone();

    [Test]
    public async Task A_shell_call_is_a_codex_shell_action() {
        var action = CodexHookActions.Normalize("Bash", Input("ls -la"), "/repo");

        await Assert.That(action.Kind).IsEqualTo(ActionKind.Shell);
        await Assert.That(action.Vendor).IsEqualTo("codex");
    }

    [Test]
    public async Task A_patch_is_an_edit_of_every_file_its_headers_name() {
        var patch = "*** Begin Patch\n*** Add File: notes.txt\n+hello\n*** Update File: /repo/src/a.cs\n*** Move to: src/b.cs\n@@\n*** Delete File: old.txt\n*** End Patch";

        var action = CodexHookActions.Normalize("apply_patch", Input(patch), "/repo");

        await Assert.That(action.Kind).IsEqualTo(ActionKind.FileEdit);
        await Assert.That(action.Paths).IsEquivalentTo(new[] { "/repo/notes.txt", "/repo/src/a.cs", "/repo/src/b.cs", "/repo/old.txt" });
    }

    /// Indentation around a header, or a header naming nothing, does not hide the files the patch does name.
    [Test]
    public async Task An_indented_header_still_names_its_file_and_a_blank_one_is_skipped() {
        var patch = "*** Begin Patch\n  *** Update File: secrets/key.txt\n*** Add File: \n*** End Patch";

        await Assert.That(CodexHookActions.Normalize("apply_patch", Input(patch), "/repo").Paths).IsEquivalentTo(new[] { "/repo/secrets/key.txt" });
    }

    /// A patch whose file cannot be placed must not read as an edit of only the files that could be.
    [Test]
    public async Task A_patch_with_no_resolvable_file_is_other() =>
        await Assert.That(CodexHookActions.Normalize("apply_patch", Input("*** Begin Patch\n*** End Patch"), "/repo").Kind)
            .IsEqualTo(ActionKind.Other);
}
