using System.Text;
using Capacitor.Cli.Commands;
using Spectre.Console;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ImportConsoleTests {
    /// <summary>
    /// Chain workers call <see cref="ImportCommand.ImportDisplay.Line"/> and write diagnostics from
    /// several threads at once, so an injected writer with no lock of its own must never see two
    /// writes in flight together.
    /// </summary>
    [Test]
    public async Task concurrent_writes_never_overlap_on_an_injected_writer() {
        var out_ = new OverlapProbe();
        var err  = new OverlapProbe();
        var ansi = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(new StringWriter()) });

        var console = new ImportConsole(out_, err, new StringReader(""), ansi, OutputRedirected: true, InputRedirected: true);
        var display = new ImportCommand.ImportDisplay { Terminal = console };

        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => {
            display.Line($"line {i}");
            console.Error.WriteLine($"error {i}");
        });

        await Assert.That(out_.Overlaps).IsEqualTo(0);
        await Assert.That(err.Overlaps).IsEqualTo(0);
        await Assert.That(out_.Writes).IsEqualTo(64);
    }

    sealed class OverlapProbe : TextWriter {
        int _inFlight;
        int _overlaps;
        int _writes;

        public int Overlaps => _overlaps;
        public int Writes   => _writes;

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value) {
            if (Interlocked.Increment(ref _inFlight) > 1) Interlocked.Increment(ref _overlaps);
            Thread.Sleep(1);
            Interlocked.Decrement(ref _inFlight);
            Interlocked.Increment(ref _writes);
        }
    }
}
