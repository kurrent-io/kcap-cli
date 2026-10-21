using Capacitor.Cli.Harness.Claude;

namespace Capacitor.Cli.Tests.Unit.Harness.Claude;

public class ClaudeMemoryImportTests {
    static ClaudeMemoryFile Note(string? name = "My Note", string? description = "One line", string? type = "feedback",
                                 string body = "Body text.", string fileName = "my_note.md") =>
        new(fileName, name, description, type, body);

    [Test]
    [Arguments("user", "preference")]
    [Arguments("Feedback", "feedback")]
    [Arguments("project", "project")]
    [Arguments("reference", "reference")]
    [Arguments("lesson", "reference")]
    [Arguments(null, "reference")]
    public async Task Type_maps_onto_a_kind_the_server_accepts(string? type, string kind) =>
        await Assert.That(ClaudeMemoryImport.Map(Note(type: type)).Kind).IsEqualTo(kind);

    [Test]
    public async Task An_unknown_type_is_reported() =>
        await Assert.That(ClaudeMemoryImport.Map(Note(type: "lesson")).Notes).Contains("unknown type 'lesson', saved as reference");

    [Test]
    public async Task Slug_is_the_normalised_name_or_else_the_file_name() {
        await Assert.That(ClaudeMemoryImport.Map(Note(name: "My Note!")).Slug).IsEqualTo("my-note");
        await Assert.That(ClaudeMemoryImport.Map(Note(name: null)).Slug).IsEqualTo("my-note");
    }

    [Test]
    public async Task Description_is_one_line_cut_to_the_server_limit() {
        var mapped = ClaudeMemoryImport.Map(Note(description: "a\nb   " + new string('c', 400)));

        await Assert.That(mapped.Description!.Length).IsEqualTo(ClaudeMemoryImport.DescriptionMaxChars);
        await Assert.That(mapped.Description).StartsWith("a b c");
        await Assert.That(mapped.Notes).Contains("description cut to 300 chars");
    }

    [Test]
    [Arguments("!!!", "Body", "One line", "name has no usable slug characters")]
    [Arguments("n", "  \n ", "One line", "empty body")]
    [Arguments("n", "Body", " ", "no description")]
    public async Task A_note_the_server_would_refuse_is_skipped(string name, string body, string description, string reason) =>
        await Assert.That(ClaudeMemoryImport.Map(Note(name: name, body: body, description: description)).SkipReason).IsEqualTo(reason);

    [Test]
    public async Task Body_keeps_its_markdown_but_not_the_blank_lines_around_it() =>
        await Assert.That(ClaudeMemoryImport.Map(Note(body: "\n\n    indented code\n- item\n\n")).Content).IsEqualTo("    indented code\n- item");

    [Test]
    public async Task A_body_over_64_KiB_is_skipped() =>
        await Assert.That(ClaudeMemoryImport.Map(Note(body: new string('x', ClaudeMemoryImport.ContentMaxUtf8Bytes + 1))).SkipReason)
            .IsEqualTo("body over 64 KiB");

    [Test]
    public async Task A_slug_repeated_within_the_batch_is_skipped() {
        var plan = ClaudeMemoryImport.Plan([Note(fileName: "a.md"), Note(fileName: "b.md")]);

        await Assert.That(plan[0].SkipReason).IsNull();
        await Assert.That(plan[1].SkipReason).IsEqualTo("same slug as an earlier file");
    }
}
