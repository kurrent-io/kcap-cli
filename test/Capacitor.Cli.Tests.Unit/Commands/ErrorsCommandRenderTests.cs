using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ErrorsCommandRenderTests {
    static ErrorEntry Error(string tool, string message, string? inputJson) =>
        new("s-1", null, null, 7, tool, message, DateTimeOffset.UnixEpoch) {
            ToolInput = inputJson is null ? null : JsonDocument.Parse(inputJson).RootElement.Clone(),
        };

    [Test]
    public async Task Every_line_of_a_multi_line_error_keeps_the_entrys_indent() {
        var text = ErrorsCommand.Render(Error("Bash", "error CS1002: ; expected\r\n  at Program.cs(3,9)\nBuild FAILED.", null));

        var lines = text.Split(Environment.NewLine);

        await Assert.That(lines).Contains("    error CS1002: ; expected");
        await Assert.That(lines).Contains("      at Program.cs(3,9)");
        await Assert.That(lines).Contains("    Build FAILED.");
    }

    [Test]
    [Arguments("""{"command":"dotnet build"}""", "dotnet build")]
    [Arguments("""{"file_path":"C:\\work\\Program.cs","offset":3}""", @"C:\work\Program.cs")]
    [Arguments("""{"command":["git","status"]}""", "git status")]
    [Arguments("""{"query":"retry policy"}""", "retry policy")]
    public async Task The_failed_calls_target_is_shown(string input, string expected) {
        var text = ErrorsCommand.Render(Error("Tool", "failed", input));

        await Assert.That(text).Contains($"    input: {expected}{Environment.NewLine}");
    }

    [Test]
    public async Task An_input_with_no_named_target_is_shown_whole_on_one_line() {
        await Assert.That(ErrorsCommand.InputSummary(JsonDocument.Parse("""{"a":1,"b":"two"}""").RootElement))
            .IsEqualTo("""{"a":1,"b":"two"}""");
    }

    [Test]
    public async Task A_long_input_is_cut() {
        var summary = ErrorsCommand.InputSummary(JsonDocument.Parse($$"""{"command":"{{new string('x', 500)}}"}""").RootElement);

        await Assert.That(summary!.Length).IsEqualTo(200);
        await Assert.That(summary).EndsWith("...");
    }

    [Test]
    public async Task No_input_adds_no_input_line() {
        await Assert.That(ErrorsCommand.Render(Error("Bash", "boom", null))).DoesNotContain("input:");
    }
}
