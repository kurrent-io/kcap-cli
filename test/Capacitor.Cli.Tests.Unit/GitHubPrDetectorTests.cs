using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit;

public class GitHubPrDetectorTests {
    [Test]
    public async Task Parses_gh_pr_view_json() {
        string? capturedCmd = null, capturedArgs = null;

        CommandRunner fake = (cmd, args, cwd, _) => {
            capturedCmd  = cmd;
            capturedArgs = args;
            return Task.FromResult<string?>(
                """{"number":12,"title":"Add thing","url":"https://github.com/o/r/pull/12","headRefName":"feat/x"}""");
        };

        var pr = await GitHubPrDetector.DetectAsync("/cwd", TimeSpan.FromSeconds(2), fake);

        await Assert.That(capturedCmd).IsEqualTo("gh");
        await Assert.That(capturedArgs).Contains("pr view");
        await Assert.That(pr!.Number).IsEqualTo(12);
        await Assert.That(pr.Title).IsEqualTo("Add thing");
        await Assert.That(pr.Url).IsEqualTo("https://github.com/o/r/pull/12");
        await Assert.That(pr.HeadRef).IsEqualTo("feat/x");
    }

    [Test]
    public async Task Null_output_yields_null() {
        CommandRunner fake = (_, _, _, _) => Task.FromResult<string?>(null); // no PR / gh failed
        await Assert.That(await GitHubPrDetector.DetectAsync("/cwd", TimeSpan.FromSeconds(2), fake)).IsNull();
    }
}
