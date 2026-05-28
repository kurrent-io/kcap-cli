namespace Kapacitor.Cli.Tests.Unit.Import;

using Kapacitor.Cli.Commands;

public class ImportDisplayGridTests {
    [Test, NotInParallel("ConsoleStreams")]
    public async Task plan_grid_renders_no_by_source_when_single_source() {
        var output = CaptureNonTtyOutput(d => d.WritePlanGrid(
            new ImportCommand.ClassificationCounts(New: 5, Partial: 0, AlreadyLoaded: 0, TooShort: 0, Excluded: 0, ProbeError: 0),
            bySource: new Dictionary<string, ImportCommand.ClassificationCounts> {
                ["claude"] = new(New: 5, Partial: 0, AlreadyLoaded: 0, TooShort: 0, Excluded: 0, ProbeError: 0),
            }));

        await Assert.That(output).DoesNotContain("By source");
    }

    [Test, NotInParallel("ConsoleStreams")]
    public async Task plan_grid_renders_by_source_section_when_multiple_sources() {
        var output = CaptureNonTtyOutput(d => d.WritePlanGrid(
            new ImportCommand.ClassificationCounts(New: 7, Partial: 0, AlreadyLoaded: 0, TooShort: 0, Excluded: 0, ProbeError: 0),
            bySource: new Dictionary<string, ImportCommand.ClassificationCounts> {
                ["claude"] = new(New: 4, Partial: 0, AlreadyLoaded: 0, TooShort: 0, Excluded: 0, ProbeError: 0),
                ["codex"]  = new(New: 3, Partial: 0, AlreadyLoaded: 0, TooShort: 0, Excluded: 0, ProbeError: 0),
            }));

        await Assert.That(output).Contains("By source");
        await Assert.That(output).Contains("claude");
        await Assert.That(output).Contains("codex");
    }

    [Test, NotInParallel("ConsoleStreams")]
    public async Task plan_grid_renders_no_by_source_when_breakdown_null() {
        var output = CaptureNonTtyOutput(d => d.WritePlanGrid(
            new ImportCommand.ClassificationCounts(New: 5, Partial: 0, AlreadyLoaded: 0, TooShort: 0, Excluded: 0, ProbeError: 0),
            bySource: null));

        await Assert.That(output).DoesNotContain("By source");
    }

    [Test, NotInParallel("ConsoleStreams")]
    public async Task done_grid_renders_no_by_source_when_single_source() {
        var output = CaptureNonTtyOutput(d => d.WriteDoneGrid(
            MakeFinal(loaded: 5),
            bySource: new Dictionary<string, ImportCommand.FinalCounts> {
                ["claude"] = MakeFinal(loaded: 5),
            }));

        await Assert.That(output).DoesNotContain("By source");
    }

    [Test, NotInParallel("ConsoleStreams")]
    public async Task done_grid_renders_by_source_section_when_multiple_sources() {
        var output = CaptureNonTtyOutput(d => d.WriteDoneGrid(
            MakeFinal(loaded: 7),
            bySource: new Dictionary<string, ImportCommand.FinalCounts> {
                ["claude"] = MakeFinal(loaded: 4),
                ["codex"]  = MakeFinal(loaded: 3),
            }));

        await Assert.That(output).Contains("By source");
        await Assert.That(output).Contains("claude");
        await Assert.That(output).Contains("codex");
    }

    [Test, NotInParallel("ConsoleStreams")]
    public async Task done_grid_renders_no_by_source_when_breakdown_null() {
        var output = CaptureNonTtyOutput(d => d.WriteDoneGrid(
            MakeFinal(loaded: 5),
            bySource: null));

        await Assert.That(output).DoesNotContain("By source");
    }

    static ImportCommand.FinalCounts MakeFinal(int loaded) => new(
        Loaded: loaded,
        Resumed: 0,
        AlreadyLoaded: 0,
        TooShort: 0,
        Excluded: 0,
        ProbeError: 0,
        Errored: 0,
        TitlesGenerated: 0,
        TitlesSkipped: 0,
        TitlesFailed: 0,
        SummariesGenerated: 0,
        SummariesFailed: 0,
        RanBackground: false,
        RequestedSummaries: false
    );

    static string CaptureNonTtyOutput(Action<ImportCommand.ImportDisplay> render) {
        var sw      = new StringWriter();
        var prevOut = Console.Out;
        Console.SetOut(sw);
        try {
            render(new ImportCommand.ImportDisplay { Tty = false });
        } finally {
            Console.SetOut(prevOut);
        }
        return sw.ToString();
    }
}
