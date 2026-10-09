using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class ImportDiscoveryReportTests {
    [Test]
    [Arguments("{}")]
    [Arguments("{\"repos\":null}")]
    [Arguments("{\"repos\":[null]}")]
    [Arguments("{\"repos\":[{\"owner\":\"org\",\"name\":\"repo\",\"sessions\":-1}]}")]
    [Arguments("{\"repos\":[],\"unmatched_sessions\":-1}")]
    public async Task Invalid_reports_are_rejected_before_the_view_reads_them(string json) =>
        await Assert.That(ImportDiscoveryReport.Parse(json)).IsNull();

    [Test]
    public async Task A_legacy_report_without_windows_still_loads() {
        var report = ImportDiscoveryReport.Parse("""{"repos":[{"owner":"org","name":"repo","sessions":3}],"unmatched_sessions":0}""");
        await Assert.That(report).IsNotNull();
        await Assert.That(report!.Repos.Single().Slug).IsEqualTo("org/repo");
        await Assert.That(report.Windows).IsEmpty();
    }
}
