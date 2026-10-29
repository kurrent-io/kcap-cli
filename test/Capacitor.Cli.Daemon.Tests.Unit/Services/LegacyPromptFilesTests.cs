using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class LegacyPromptFilesTests {
    [TempDir] public required TempDir Tmp { get; init; }

    /// <summary>A daemon that wrote launch prompts to files and stopped without cleaning up leaves
    /// them under its state directory; the next start must remove them, since nothing else will.</summary>
    [Test]
    public async Task Sweep_removes_the_prompt_files_an_older_daemon_left_behind() {
        Tmp.CreateFile(["prompts", "a1", "prompt.md"], "the whole task");

        LegacyPromptFiles.Sweep(Tmp.Path);

        await Assert.That(Directory.Exists(Tmp.PathTo("prompts"))).IsFalse();
    }

    [Test]
    public async Task Sweep_tolerates_a_state_directory_without_prompts() {
        LegacyPromptFiles.Sweep(Tmp.Path);

        await Assert.That(Directory.Exists(Tmp.Path)).IsTrue();
    }
}
