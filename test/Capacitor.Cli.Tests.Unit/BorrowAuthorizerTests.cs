using System.Diagnostics;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Behaviour tests for <see cref="BorrowAuthorizer"/> — the daemon-side gate deciding whether a
/// cwd may be borrowed (a read-only reviewer run in it). Uses real temp dirs, real symlinks, and
/// real <c>git init</c> repos so the canonicalization + git-root + allowlist policy is exercised
/// against the actual filesystem rather than mocks.
/// </summary>
public class BorrowAuthorizerTests {
    [Test]
    public async Task Absent_path_is_not_allowed_with_path_absent_reason() {
        var missing = Path.Combine(Path.GetTempPath(), "kcap-borrow-missing-" + Guid.NewGuid().ToString("N")[..8]);

        var result = await new BorrowAuthorizer(new DaemonConfig()).AuthorizeBorrowAsync(missing);

        await Assert.That(result.Allowed).IsFalse();
        await Assert.That(result.Reason).IsEqualTo("path_absent");
        await Assert.That(result.CanonicalCwd).IsNull();
        await Assert.That(result.CanonicalGitRoot).IsNull();
    }

    [Test]
    public async Task GitRooted_cwd_with_empty_allowlist_is_allowed() {
        var repo = MakeTempRepo();
        try {
            var result = await new BorrowAuthorizer(new DaemonConfig()).AuthorizeBorrowAsync(repo);

            await Assert.That(result.Allowed).IsTrue();
            await Assert.That(result.CanonicalGitRoot).IsEqualTo(BorrowAuthorizer.Canonicalize(repo));
        } finally {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Test]
    public async Task NonRepo_cwd_with_empty_allowlist_is_not_allowed() {
        var tmp = Directory.CreateTempSubdirectory("kcap-borrow-norepo-");
        try {
            var result = await new BorrowAuthorizer(new DaemonConfig()).AuthorizeBorrowAsync(tmp.FullName);

            await Assert.That(result.Allowed).IsFalse();
            await Assert.That(result.Reason).IsEqualTo("not_allowed");
            await Assert.That(result.CanonicalGitRoot).IsNull();
        } finally {
            tmp.Delete(recursive: true);
        }
    }

    [Test]
    public async Task NonRepo_cwd_matching_explicit_allowlist_is_allowed() {
        var tmp = Directory.CreateTempSubdirectory("kcap-borrow-norepo-allow-");
        try {
            var canonical  = BorrowAuthorizer.Canonicalize(tmp.FullName);
            var authorizer = new BorrowAuthorizer(new DaemonConfig { AllowedRepoPaths = [canonical] });

            var result = await authorizer.AuthorizeBorrowAsync(tmp.FullName);

            await Assert.That(result.Allowed).IsTrue();
            await Assert.That(result.Reason).IsNull();
        } finally {
            tmp.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Symlinked_cwd_resolving_into_allowed_git_root_is_allowed() {
        var repo       = MakeTempRepo();
        var linkParent = Directory.CreateTempSubdirectory("kcap-borrow-link-");
        var link       = Path.Combine(linkParent.FullName, "link-to-repo");
        try {
            Directory.CreateSymbolicLink(link, repo);

            var result = await new BorrowAuthorizer(new DaemonConfig()).AuthorizeBorrowAsync(link);

            await Assert.That(result.Allowed).IsTrue();
            await Assert.That(result.CanonicalCwd).IsEqualTo(BorrowAuthorizer.Canonicalize(repo));
        } finally {
            linkParent.Delete(recursive: true);
            Directory.Delete(repo, recursive: true);
        }
    }

    [Test]
    public async Task Symlinked_cwd_escaping_nonempty_allowlist_is_not_allowed() {
        var allowedRoot = Directory.CreateTempSubdirectory("kcap-borrow-allowed-");
        var outsideRoot = Directory.CreateTempSubdirectory("kcap-borrow-outside-");
        var link        = Path.Combine(allowedRoot.FullName, "escape-link");
        try {
            Directory.CreateSymbolicLink(link, outsideRoot.FullName);

            var authorizer = new BorrowAuthorizer(
                new DaemonConfig { AllowedRepoPaths = [BorrowAuthorizer.Canonicalize(allowedRoot.FullName)] }
            );

            var result = await authorizer.AuthorizeBorrowAsync(link);

            await Assert.That(result.Allowed).IsFalse();
            await Assert.That(result.Reason).IsEqualTo("not_allowed");
        } finally {
            // Remove the symlink itself first so recursive delete never has to reason about
            // whether it would otherwise be followed into outsideRoot.
            File.Delete(link);
            allowedRoot.Delete(recursive: true);
            outsideRoot.Delete(recursive: true);
        }
    }

    static string MakeTempRepo() {
        var root = Path.Combine(Path.GetTempPath(), "kcap-borrow-repo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        Run(root, "init", "-q");

        return root;
    }

    static void Run(string cwd, params string[] args) {
        var psi = new ProcessStartInfo("git", args) {
            WorkingDirectory       = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();

        if (proc.ExitCode != 0) {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {proc.StandardError.ReadToEnd()}");
        }
    }
}
