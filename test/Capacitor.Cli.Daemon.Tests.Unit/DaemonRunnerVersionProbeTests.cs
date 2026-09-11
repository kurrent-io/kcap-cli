namespace Capacitor.Cli.Daemon.Tests.Unit;

/// <summary>
/// <see cref="DaemonRunner.ParseProbedVersion"/> — the pure half of the vendor CLI <c>--version</c>
/// probe whose result the daemon advertises as <c>cli_version</c> in its capability payload (read by
/// the borrowed-review pre-flight) and feeds to <see cref="DaemonRunner.CliVersionAllowed"/>.
///
/// <para>The live defect: GitHub Copilot CLI prints its version at the end of a sentence and then a
/// second "update available" line. Tokenising on spaces alone glued the newline and the next line's
/// first word onto the version, advertising <c>"1.0.75.\nRun"</c> — which no version parser accepts.
/// Each case below is real observed output from the installed CLI.</para>
/// </summary>
public class DaemonRunnerVersionProbeTests {
    [Test]
    public async Task Copilot_multiline_output_yields_just_the_version() {
        const string observed = "GitHub Copilot CLI 1.0.75.\nRun 'copilot update' to check for updates.";

        await Assert.That(DaemonRunner.ParseProbedVersion(observed)).IsEqualTo("1.0.75");
    }

    [Test]
    public async Task Copilot_version_is_usable_by_the_range_check() {
        // Not merely cosmetic: the advertised string has to parse, or every certification range
        // check for this vendor fails closed on a perfectly good build.
        var parsed = DaemonRunner.ParseProbedVersion("GitHub Copilot CLI 1.0.75.\nRun 'copilot update' to check for updates.");

        await Assert.That(DaemonRunner.CliVersionAllowed(parsed, ">=1.0.0")).IsTrue();
    }

    [Test]
    public async Task Windows_line_endings_are_also_separators() {
        await Assert.That(DaemonRunner.ParseProbedVersion("GitHub Copilot CLI 1.0.75.\r\nRun 'copilot update'."))
            .IsEqualTo("1.0.75");
    }

    [Test]
    [Arguments("2.1.212 (Claude Code)", "2.1.212")]        // claude
    [Arguments("codex-cli 0.144.3", "0.144.3")]            // codex — first token has no digits
    [Arguments("2026.07.23-e383d2b", "2026.07.23-e383d2b")] // cursor-agent
    [Arguments("0.50.0", "0.50.0")]                         // gemini
    [Arguments("v1.2.3", "1.2.3")]                          // a "v"-prefixed build
    public async Task Single_line_vendor_output_is_unchanged(string observed, string expected) {
        await Assert.That(DaemonRunner.ParseProbedVersion(observed)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("command not found")]
    public async Task Output_with_no_version_token_resolves_to_null(string observed) {
        await Assert.That(DaemonRunner.ParseProbedVersion(observed)).IsNull();
    }

    /// <summary>A vendor whose <c>--version</c> writes more than the OS pipe buffer — some print a
    /// banner or an "update available" notice — must not deadlock the probe. Its redirected streams
    /// are drained while it runs, so it can finish writing and exit and the version is read; reading
    /// only after the wait would park the child on a full pipe and hang the probe until its timeout
    /// killed it, the silent multi-minute daemon startup this guards against. POSIX stub, so Windows
    /// is skipped like the other real-binary probe checks.</summary>
    [Test]
    public async Task A_version_whose_output_exceeds_the_pipe_buffer_is_drained_not_deadlocked() {
        Skip.Unless(!OperatingSystem.IsWindows(), "The stub binary is a POSIX shell script.");
        using var tmp = new TempDir();
        // ~500 KB to stderr — past every platform's pipe buffer — before the version line on stdout.
        var cli = tmp.CreateFile(
            "faketool", "#!/bin/sh\nyes 0123456789ABCDEFGHIJ | head -c 500000 1>&2\necho 'faketool 9.9.9'\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        await Assert.That(DaemonRunner.ProbeCliVersionForLaunch(cli)).IsEqualTo("9.9.9");
    }

    /// <summary>The version sits at the front and a flood follows on the same stream: the probe keeps
    /// only a bounded prefix (so a noisy CLI cannot grow the buffer without limit) yet must keep
    /// draining past the cap, or the child blocks on the full pipe and the probe stalls to its
    /// timeout. Guards both halves — a drain that stopped at the cap would fail this.</summary>
    [Test]
    public async Task A_version_at_the_front_survives_a_trailing_flood_on_the_same_stream() {
        Skip.Unless(!OperatingSystem.IsWindows(), "The stub binary is a POSIX shell script.");
        using var tmp = new TempDir();
        var cli = tmp.CreateFile(
            "faketool", "#!/bin/sh\necho 'faketool 9.9.9'\nyes 0123456789ABCDEFGHIJ | head -c 500000\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        await Assert.That(DaemonRunner.ProbeCliVersionForLaunch(cli)).IsEqualTo("9.9.9");
    }
}
