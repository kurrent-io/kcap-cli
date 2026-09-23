using System.IO.Pipes;
using System.Text;

namespace Capacitor.Cli.Core.Tests.Unit;

public class ProcessPipeDrainTests {
    [Test]
    public async Task A_stop_returns_bytes_already_read_while_the_writer_stays_open() {
        await using var outbound = new AnonymousPipeServerStream(PipeDirection.Out);
        await using var inbound = new AnonymousPipeClientStream(PipeDirection.In, outbound.GetClientHandleAsString());
        using var reader = new StreamReader(inbound, leaveOpen: true);
        using var stop = new CancellationTokenSource();

        var task = ProcessPipeDrain.Start(reader, cap: 8192, stop.Token);
        await outbound.WriteAsync(Encoding.UTF8.GetBytes("faketool 9.9.9\n"));
        await Task.Delay(100);
        stop.Cancel();

        var text = await task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(text).Contains("9.9.9");
    }

    [Test]
    public async Task A_leading_utf8_bom_is_not_part_of_the_text() {
        await using var outbound = new AnonymousPipeServerStream(PipeDirection.Out);
        await using var inbound = new AnonymousPipeClientStream(PipeDirection.In, outbound.GetClientHandleAsString());
        using var reader = new StreamReader(inbound, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        var task = ProcessPipeDrain.Start(reader, cap: 8192, CancellationToken.None);
        var bytes = Encoding.UTF8.GetPreamble().Concat("1.2.3\n"u8.ToArray()).ToArray();
        await outbound.WriteAsync(bytes);
        outbound.Dispose();

        var text = await task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(text).DoesNotContain("\uFEFF");
        await Assert.That(VendorVersionResolver.ExtractVersionToken(text)).IsEqualTo("1.2.3");
    }
}
