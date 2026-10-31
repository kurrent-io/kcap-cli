using Capacitor.Cli.Service.Installation;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Service.Tests.Unit.Installation;

public class PendingRemovalsTests {
    const string Product = @"C:\Program Files\kcap";

    [Test]
    public async Task Deletions_of_the_directory_and_its_contents_are_dropped_whatever_their_flag() {
        string[] queue = [
            @"*1\??\C:\Program Files\kcap\service\kcap-service.exe", "",
            @"\??\C:\Program Files\kcap\service", "",
            @"\??\C:\Program Files\kcap", "",
        ];

        await Assert.That(PendingRemovals.Without(queue, Product)).IsEmpty();
    }

    [Test]
    public async Task Other_operations_survive_in_order() {
        string[] queue = [
            @"\??\C:\Temp\old.dll", "",
            @"\??\C:\Program Files\kcap\service\kcap-service.exe", "",
            @"\??\C:\Program Files\kcap-other\a.exe", "",
            @"\??\C:\Staged\b.exe", @"!\??\C:\Program Files\kcap\service\b.exe",
        ];

        await Assert.That(PendingRemovals.Without(queue, Product)).IsEquivalentTo([
            @"\??\C:\Temp\old.dll", "",
            @"\??\C:\Program Files\kcap-other\a.exe", "",
            @"\??\C:\Staged\b.exe", @"!\??\C:\Program Files\kcap\service\b.exe",
        ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task The_match_ignores_case_and_a_trailing_separator() {
        string[] queue = [@"\??\c:\program files\KCAP\", ""];

        await Assert.That(PendingRemovals.Without(queue, Product + @"\")).IsEmpty();
    }
}
