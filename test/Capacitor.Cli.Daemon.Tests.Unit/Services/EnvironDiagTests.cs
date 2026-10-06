#pragma warning disable CA1305, CA2201, CA1307, CA1310
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

// Throwaway CI diagnostic: classifies what /proc/{pid}/environ returns right after Process.Start.
public class EnvironDiagTests {
    [Test]
    public async Task Diag() {
        if (!OperatingSystem.IsLinux()) return;

        var self   = File.ReadAllText("/proc/self/environ");
        var report = new StringBuilder();
        report.AppendLine($"uname={File.ReadAllText("/proc/sys/kernel/osrelease").Trim()} selfLen={self.Length}");
        foreach (var e in self.Split('\0')) if (e.StartsWith("KCAP_", StringComparison.Ordinal)) report.AppendLine($"self {e}");

        var counts = new ConcurrentDictionary<string, int>();
        var dumps  = new ConcurrentQueue<string>();
        var sw     = Stopwatch.StartNew();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => {
            while (sw.Elapsed < TimeSpan.FromSeconds(30)) {
                using var dummy = DummyProcess.StartSleep(30, new Dictionary<string, string> {
                    ["KCAP_AGENT_ID"] = "agent-xyz", ["KCAP_DAEMON_EPOCH"] = "epoch-1" });
                string raw;
                try { raw = File.ReadAllText($"/proc/{dummy.Pid}/environ"); } catch (Exception ex) { raw = "EXC:" + ex.GetType().Name; }
                var agent = raw.Split('\0').FirstOrDefault(e => e.StartsWith("KCAP_AGENT_ID=", StringComparison.Ordinal));
                var kind  = raw.StartsWith("EXC:", StringComparison.Ordinal) ? raw
                    : raw.Length == 0 ? "empty"
                    : agent is null ? (raw == self ? "absent:self" : "absent:other")
                    : agent == "KCAP_AGENT_ID=agent-xyz" ? "ok"
                    : $"other:{agent}";
                counts.AddOrUpdate(kind, 1, (_, n) => n + 1);
                if (kind is not ("ok" or "empty") && dumps.Count < 6) {
                    var at   = raw.IndexOf("KCAP_", StringComparison.Ordinal);
                    var now  = "";
                    try { now = File.ReadAllText($"/proc/{dummy.Pid}/environ"); } catch { }
                    dumps.Enqueue($"DUMP kind={kind} len={raw.Length} nowLen={now.Length} eqSelf={raw == self} firstKcapAt={at} "
                                + $"kcap=[{string.Join(",", raw.Split('\0').Where(e => e.StartsWith("KCAP_", StringComparison.Ordinal)))}] "
                                + $"tail={raw[Math.Max(0, raw.Length - 60)..].Replace('\0', '|')}");
                }
                dummy.Kill();
            }
        })));

        foreach (var (k, n) in counts) report.AppendLine($"{k}: {n}");
        foreach (var d in dumps) report.AppendLine(d);
        throw new Exception("ENVIRON-DIAG\n" + report);
    }
}
