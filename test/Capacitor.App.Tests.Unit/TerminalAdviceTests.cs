using Capacitor.App.Services.LocalSetup;

namespace Capacitor.App.Tests.Unit;

/// Every failure message `kcap setup --local` can put on the Install page, verbatim apart from its
/// interpolated values, with what the page shows for it.
public class TerminalAdviceTests {
    public static IEnumerable<Func<(string Message, string Shown)>> CliMessages() {
        // LocalPreflight
        yield return () => ("There is no Intel Mac build of the local edition; it needs an Apple silicon Mac.",
            "There is no Intel Mac build of the local edition; it needs an Apple silicon Mac.");
        yield return () => ("The musl builds of kcap do not support a local installation.",
            "The musl builds of kcap do not support a local installation.");
        yield return () => ("The local edition runs on macOS for now.", "The local edition runs on macOS for now.");
        yield return () => (
            "The local installation's sockets must have paths of at most 103 bytes: /Users/me/very/long/run/.s.PGSQL.5432 is 110 bytes; /Users/me/very/long/data/kurrentdb/kurrent.sock is 120 bytes. Set KCAP_LOCAL_ROOT to a directory at least 17 bytes shorter.",
            "The local installation's sockets must have paths of at most 103 bytes: /Users/me/very/long/run/.s.PGSQL.5432 is 110 bytes; /Users/me/very/long/data/kurrentdb/kurrent.sock is 120 bytes.");
        yield return () => (
            "/Users/shared is writable by every user, so the local server refuses to start under it. Run chmod o-w on it, or set KCAP_LOCAL_ROOT to a directory under your home.",
            "/Users/shared is writable by every user, so the local server refuses to start under it.");
        yield return () => (
            "/Users/me/loop cannot be resolved (too many symbolic links). Set KCAP_LOCAL_ROOT to a directory under your home.",
            "/Users/me/loop cannot be resolved (too many symbolic links).");
        yield return () => (
            "The setup journal /Users/me/.local/kcap/journal.json cannot be read (bad json). Fix or remove it, then run setup again.",
            "The setup journal /Users/me/.local/kcap/journal.json cannot be read (bad json).");
        yield return () => (
            "The installation registry /Users/me/.local/kcap/registry.json cannot be read (bad json). Fix or remove it, then run setup again.",
            "The installation registry /Users/me/.local/kcap/registry.json cannot be read (bad json).");
        yield return () => (
            "The configuration file /Users/me/.config/kcap/config.json cannot be read. Fix or remove it, then run setup again.",
            "The configuration file /Users/me/.config/kcap/config.json cannot be read.");
        yield return () => (
            "The profile \"cloud\" points at https://acme.kcap.ai, which is not this machine's local installation. Converting a machine that uses a cloud server arrives in a later release.",
            "The profile \"cloud\" points at https://acme.kcap.ai, which is not this machine's local installation. Converting a machine that uses a cloud server arrives in a later release.");
        yield return () => (
            "Found leftover state from another installation: /Users/me/.local/kcap/bin already exists. Nothing was changed; remove it or uninstall that installation first.",
            "Found leftover state from another installation: /Users/me/.local/kcap/bin already exists. Nothing was changed; remove it or uninstall that installation first.");
        yield return () => ("\"1.x\" is not a version of the form 1.2.3 or 1.2.3-beta.1.",
            "\"1.x\" is not a version of the form 1.2.3 or 1.2.3-beta.1.");
        yield return () => (
            "The kcap on PATH (/usr/local/bin/kcap) is 1.2.0, and a local installation needs a kcap newer than 1.3.0. Update or remove it before setting up a local installation.",
            "The kcap on PATH (/usr/local/bin/kcap) is 1.2.0, and a local installation needs a kcap newer than 1.3.0. Update or remove it before setting up a local installation.");
        yield return () => (
            "The desktop app's kcap (/Applications/kcap.app/Contents/MacOS/kcap) is 1.2.0, and a local installation needs a kcap newer than 1.3.0. Update the desktop app before setting up a local installation.",
            "The desktop app's kcap (/Applications/kcap.app/Contents/MacOS/kcap) is 1.2.0, and a local installation needs a kcap newer than 1.3.0. Update the desktop app before setting up a local installation.");
        yield return () => (
            "The kcap on PATH (/usr/local/bin/kcap) did not report its version, and a local installation needs a kcap newer than 1.3.0. Update or remove it before setting up a local installation.",
            "The kcap on PATH (/usr/local/bin/kcap) did not report its version, and a local installation needs a kcap newer than 1.3.0. Update or remove it before setting up a local installation.");

        // LocalInstaller
        yield return () => ("Setup was cancelled.", "Setup was cancelled.");
        yield return () => ("Something other than the new local server answers on http://127.0.0.1:5123. Nothing was kept.",
            "Something other than the new local server answers on http://127.0.0.1:5123. Nothing was kept.");
        yield return () => (
            "Setup could not finish its commit (disk full). Nothing was removed; run kcap setup --local again to finish it.",
            "Setup could not finish its commit (disk full). Nothing was removed.");
        yield return () => (
            "The launchd LaunchAgent ai.kcap.local (/Users/me/Library/LaunchAgents/ai.kcap.local.plist) is not provably this installation's, so it was left alone and the profile was not changed.",
            "The launchd LaunchAgent ai.kcap.local (/Users/me/Library/LaunchAgents/ai.kcap.local.plist) is not provably this installation's, so it was left alone and the profile was not changed.");
        yield return () => (
            "Something other than this installation's local server answers on http://127.0.0.1:5123, so the profile was not changed.",
            "Something other than this installation's local server answers on http://127.0.0.1:5123, so the profile was not changed.");
        yield return () => ("The profile could not be published (locked).", "The profile could not be published (locked).");
        yield return () => (
            "The local installation's binaries are missing, but postgres is running under /Users/me/.local/kcap. Nothing was changed.",
            "The local installation's binaries are missing, but postgres is running under /Users/me/.local/kcap. Nothing was changed.");
        yield return () => (
            "The download failed. Setup could not be undone (busy). The setup journal is kept; the next kcap setup --local retries the cleanup.",
            "The download failed. Setup could not be undone (busy). The setup journal is kept.");

        // LocalSetupCommit
        yield return () => (
            "The profile \"default\" in /Users/me/.config/kcap could not be pointed at http://127.0.0.1:5123: locked. The setup journal is kept; fix the configuration, then run kcap setup --local again.",
            "The profile \"default\" in /Users/me/.config/kcap could not be pointed at http://127.0.0.1:5123: locked. The setup journal is kept.");
        yield return () => (
            "The profile \"default\" in /Users/me/.config/kcap could not be pointed at http://127.0.0.1:5123: locked. Fix the configuration, then run kcap setup --local again.",
            "The profile \"default\" in /Users/me/.config/kcap could not be pointed at http://127.0.0.1:5123: locked.");
        yield return () => (
            "The local server at http://127.0.0.1:5123 did not confirm it left setup mode, even after a restart. The setup journal is kept; run kcap setup --local again to finish it.",
            "The local server at http://127.0.0.1:5123 did not confirm it left setup mode, even after a restart. The setup journal is kept.");
        yield return () => (
            "The local server did not confirm it left setup mode, and the launchd LaunchAgent ai.kcap.local (/Users/me/Library/LaunchAgents/ai.kcap.local.plist) is not provably this installation's, so it was not restarted. The setup journal is kept; run kcap setup --local again to finish it.",
            "The local server did not confirm it left setup mode, and the launchd LaunchAgent ai.kcap.local (/Users/me/Library/LaunchAgents/ai.kcap.local.plist) is not provably this installation's, so it was not restarted. The setup journal is kept.");
        yield return () => (
            "The installation registry /Users/me/.local/kcap/registry.json cannot be read (bad json). Fix or remove it, then run kcap setup --local again.",
            "The installation registry /Users/me/.local/kcap/registry.json cannot be read (bad json).");

        // LocalSetupRecovery
        yield return () => (
            "phase: \"merging\" needs a newer kcap to finish. The setup journal /Users/me/.local/kcap/journal.json was left as it is; update kcap, then run kcap setup --local again.",
            "phase: \"merging\" needs a newer kcap to finish. The setup journal /Users/me/.local/kcap/journal.json was left as it is.");
        yield return () => (
            "schema 9 is newer than this kcap. The setup journal /Users/me/.local/kcap/journal.json was not written by this kcap and was left as it is, with nothing changed; fix or remove it, then run kcap setup --local again.",
            "schema 9 is newer than this kcap. The setup journal /Users/me/.local/kcap/journal.json was not written by this kcap and was left as it is, with nothing changed.");
        yield return () => (
            "The interrupted setup cannot be finished: /Users/me/.local/kcap/bin/current/kcap-server is missing. The setup journal /Users/me/.local/kcap/journal.json is kept.",
            "The interrupted setup cannot be finished: /Users/me/.local/kcap/bin/current/kcap-server is missing. The setup journal /Users/me/.local/kcap/journal.json is kept.");
        yield return () => (
            "The interrupted setup cannot be finished: the launchd LaunchAgent ai.kcap.local (/Users/me/Library/LaunchAgents/ai.kcap.local.plist) is not provably this installation's. Nothing was changed and the setup journal /Users/me/.local/kcap/journal.json is kept.",
            "The interrupted setup cannot be finished: the launchd LaunchAgent ai.kcap.local (/Users/me/Library/LaunchAgents/ai.kcap.local.plist) is not provably this installation's. Nothing was changed and the setup journal /Users/me/.local/kcap/journal.json is kept.");

        // LocalSetupRollback, LocalUnitLock, LocalServerReadiness, LocalStackProbe
        yield return () => (
            "Setup could not be undone: postgres is running under /Users/me/.local/kcap. Nothing was removed.",
            "Setup could not be undone: postgres is running under /Users/me/.local/kcap. Nothing was removed.");
        yield return () => (
            "Setup could not be undone: it cannot tell which installation the launchd LaunchAgent ai.kcap.local (/Users/me/Library/LaunchAgents/ai.kcap.local.plist) belongs to. Nothing was removed.",
            "Setup could not be undone: it cannot tell which installation the launchd LaunchAgent ai.kcap.local (/Users/me/Library/LaunchAgents/ai.kcap.local.plist) belongs to. Nothing was removed.");
        yield return () => ("Another kcap is changing the local server unit. Wait for it to finish, then try again.",
            "Another kcap is changing the local server unit. Wait for it to finish, then try again.");
        yield return () => (
            "The local server did not report ready on http://127.0.0.1:5123 within 10 minutes. See /Users/me/.local/kcap/logs/server.out.log and the logs beside it.",
            "The local server did not report ready on http://127.0.0.1:5123 within 10 minutes. See /Users/me/.local/kcap/logs/server.out.log and the logs beside it.");
        yield return () => (
            "The local server's service stopped, but postgres is running under /Users/me/.local/kcap. Nothing was removed.",
            "The local server's service stopped, but postgres is running under /Users/me/.local/kcap. Nothing was removed.");

        // LocalBundleClient, LocalBundleManifest, LocalBundleUnpacker
        yield return () => (
            "No local bundle is published at https://dl.kcap.ai/local/osx-arm64/kcap-local-1.2.3-osx-arm64.json. Set KCAP_LOCAL_BUNDLE_VERSION to a published version, or KCAP_LOCAL_BUNDLE_BASE_URL to another location.",
            "No local bundle is published at https://dl.kcap.ai/local/osx-arm64/kcap-local-1.2.3-osx-arm64.json.");
        yield return () => ("Could not download https://dl.kcap.ai/local/a.tar.gz: The SSL connection could not be established.",
            "Could not download https://dl.kcap.ai/local/a.tar.gz: The SSL connection could not be established.");
        yield return () => ("The bundle's checksum does not match its manifest.", "The bundle's checksum does not match its manifest.");
        yield return () => ("The bundle manifest names a different signing key.", "The bundle manifest names a different signing key.");
    }

    [Test]
    [MethodDataSource(nameof(CliMessages))]
    public async Task Each_installer_message_keeps_its_cause_and_loses_its_commands((string Message, string Shown) c) {
        var stripped = TerminalAdvice.Strip(c.Message);

        await Assert.That(stripped).IsEqualTo(c.Shown);
        await Assert.That(stripped!).DoesNotContain("kcap setup");
        await Assert.That(stripped!).DoesNotContain("chmod");
        await Assert.That(stripped!).DoesNotContain("KCAP_");
    }

    [Test]
    [Arguments("kcap local 1.2.0 was left as it was.")]
    [Arguments("kcap local could not be upgraded.")]
    [Arguments("kcap local is already at 1.2.0.")]
    public async Task Local_product_names_are_kept_while_command_advice_is_removed(string status) {
        await Assert.That(TerminalAdvice.Strip(status + " Run kcap local upgrade to try again.")).IsEqualTo(status);
        await Assert.That(TerminalAdvice.Strip(status + " kcap local logs shows why.")).IsEqualTo(status);
    }

    [Test]
    public async Task A_message_that_is_only_advice_leaves_nothing() {
        await Assert.That(TerminalAdvice.Strip("Fix the configuration, then run kcap setup --local again.")).IsNull();
    }
}
