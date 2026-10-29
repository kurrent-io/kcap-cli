using Capacitor.Cli.Harness.MuseAssistant;

namespace Capacitor.Cli.Tests.Unit.Harness.MuseAssistant;

public class MuseAssistantBoundaryTests {
    [Test]
    public async Task Each_flag_parses_to_its_boundary() {
        await Assert.That(MuseAssistantBoundary.Parse(["--chat", "c", "--after-seq", "41"]).Boundary).IsEqualTo(new MuseAssistantBoundary(MuseAssistantGrantBoundaries.AfterSeq, 41));
        await Assert.That(MuseAssistantBoundary.Parse(["--after-seq", "0"]).Boundary).IsEqualTo(new MuseAssistantBoundary(MuseAssistantGrantBoundaries.AfterSeq, 0));
        await Assert.That(MuseAssistantBoundary.Parse(["--empty-chat"]).Boundary).IsEqualTo(new MuseAssistantBoundary(MuseAssistantGrantBoundaries.EmptyChat, null));
        await Assert.That(MuseAssistantBoundary.Parse(["--include-history"]).Boundary).IsEqualTo(new MuseAssistantBoundary(MuseAssistantGrantBoundaries.History, null));
    }

    [Test]
    [Arguments((object)new string[] { })]
    [Arguments((object)new[] { "--chat", "c" })]
    [Arguments((object)new[] { "--after-seq" })]
    [Arguments((object)new[] { "--after-seq", "-1" })]
    [Arguments((object)new[] { "--after-seq", "+1" })]
    [Arguments((object)new[] { "--after-seq", "twelve" })]
    [Arguments((object)new[] { "--after-seq", "1.5" })]
    [Arguments((object)new[] { "--after-seq", "3", "--after-seq", "4" })]
    [Arguments((object)new[] { "--empty-chat", "--include-history" })]
    [Arguments((object)new[] { "--after-seq", "3", "--empty-chat" })]
    public async Task A_missing_malformed_or_ambiguous_boundary_is_an_error(string[] args) {
        var (boundary, error) = MuseAssistantBoundary.Parse(args);

        await Assert.That(boundary).IsNull();
        await Assert.That(error).IsNotNull();
    }
}
