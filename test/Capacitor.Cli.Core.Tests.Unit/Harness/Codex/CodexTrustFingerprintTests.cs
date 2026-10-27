using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Codex;

public class CodexTrustFingerprintTests {
    [TempHome] public required TempHome Home { get; init; }

    CodexPaths Paths => new(Home, Home.PathTo(".codex"));

    string Fingerprint(string? hooks, string? config) {
        Home.CreateDir(".codex");
        foreach (var (name, text) in new[] { ("hooks.json", hooks), ("config.toml", config) }) {
            var path = Home.PathTo(".codex", name);
            if (text is null) File.Delete(path);
            else File.WriteAllText(path, text);
        }
        return CodexTrustFingerprint.Of(Paths);
    }

    [Test]
    public async Task Same_files_same_fingerprint() =>
        await Assert.That(Fingerprint("{}", "a = 1")).IsEqualTo(Fingerprint("{}", "a = 1"));

    [Test]
    public async Task Editing_hooks_json_changes_it() =>
        await Assert.That(Fingerprint("{}", "a = 1")).IsNotEqualTo(Fingerprint("{ }", "a = 1"));

    [Test]
    public async Task Editing_config_toml_alone_changes_it() =>
        await Assert.That(Fingerprint("{}", "a = 1")).IsNotEqualTo(Fingerprint("{}", "a = 2"));

    [Test]
    public async Task An_absent_file_differs_from_an_empty_one() =>
        await Assert.That(Fingerprint("{}", null)).IsNotEqualTo(Fingerprint("{}", ""));

    [Test]
    public async Task Moving_bytes_between_the_two_files_changes_it() =>
        await Assert.That(Fingerprint("ab", "c")).IsNotEqualTo(Fingerprint("a", "bc"));
}
