using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Microsoft.Win32.SafeHandles;

namespace Capacitor.Cli.Services;

static class ServiceFiles {
    const UnixFileMode OwnerOnly    = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    const UnixFileMode OwnerOnlyDir = OwnerOnly | UnixFileMode.UserExecute;
    const UnixFileMode SharedWrite  = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    /// <summary>Writes a service unit readable only by its owner, or fails without leaving one behind.
    ///
    /// <para><see cref="File.WriteAllText(string,string)"/> defaults to <c>-rw-r--r--</c> (verified on a
    /// real launchd install), and a unit carries the server URL, the profile, and possibly a command that
    /// produces a credential.</para>
    ///
    /// <para>Order matters, and each step closes a specific hole: the staging inode is created
    /// EXCLUSIVELY with its mode requested at creation; the mode is then verified and repaired
    /// <b>through the open handle, before any content is written</b>, so no populated file ever exists at
    /// a permissive mode and there is no pathname to re-resolve; only then is content written and the file
    /// renamed into place. The post-rename check exists because a rename onto an existing target does not
    /// preserve the source mode on every filesystem, and if it fails the live file is REMOVED — reporting
    /// a failed install while leaving a readable credential-bearing unit where launchd would read it is
    /// the failure this is meant to prevent.</para></summary>
    /// <param name="verifyFinal">Test seam. Production passes null and gets the real post-rename check;
    /// a test supplies a failing one to prove the rollback, which is otherwise only reachable on a
    /// filesystem that does not preserve mode across a rename.</param>
    /// <param name="tightenDirectory">Test seam. Production passes null and gets the real chmod; a test
    /// supplies a no-op one to prove the refusal, which is otherwise only reachable through a directory
    /// the current user does not own.</param>
    /// <param name="durable">Flushes the content and the directory entry, so a power loss leaves the old unit or
    /// the new one. Every directory level below the home is synced.</param>
    /// <param name="home">Bounds the durable directory creation; without it the walk reaches the filesystem root.</param>
    public static void WriteOwnerOnly(
            string path, string content, Encoding? encoding = null, Action<string>? verifyFinal = null,
            Action<string>? tightenDirectory = null, bool durable = false, UserHome? home = null) {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory)) {
            if (durable) DurableDirectory.Ensure(directory, DurableDirectory.AnchorFor(directory, home), OwnerOnlyDir);
            else CreateDirectory(directory);
            RequireNoSharedWrite(directory, tightenDirectory);
            RequireNoWorldWritableAncestor(directory);
            RequireNoForeignOwnedAncestor(directory);
        }

        // Full GUID: the staging name must not be guessable by a local process racing to pre-create it,
        // and CreateNew turns such a collision into a hard failure rather than a followed symlink.
        var staging = path + ".tmp-" + Guid.NewGuid().ToString("N");

        try {
            WriteStaging(staging, content, encoding, durable);
            FaultPoints.Hit($"rename:{path}");
            File.Move(staging, path, overwrite: true);
            FaultPoints.Hit($"renamed:{path}");
            if (durable && !string.IsNullOrEmpty(directory)) DurableDirectory.Sync(directory);
        } catch (Exception) {
            TryDelete(staging);

            throw;
        }

        try {
            (verifyFinal ?? RequireOwnerOnlyPath)(path);
        } catch (Exception) {
            TryDelete(path);   // never leave an insecure unit at the live path

            throw;
        }
    }

    static void WriteStaging(string staging, string content, Encoding? encoding, bool durable) {
        var options = new FileStreamOptions {
            Mode   = FileMode.CreateNew,       // never follow or truncate an existing entry
            Access = FileAccess.Write
        };

        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnly;

        using var stream = new FileStream(staging, options);

        // Before content: UnixCreateMode is still filtered through the process umask, so the requested
        // mode is a request, not a result. Checked on the handle so it cannot be a different file.
        RequireOwnerOnlyHandle(stream.SafeFileHandle, staging);

        using var writer = encoding is null ? new StreamWriter(stream) : new StreamWriter(stream, encoding);

        writer.Write(content);

        if (!durable) return;

        writer.Flush();
        FaultPoints.Hit($"flush:{staging}");
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Creates the unit directory, and every ancestor it has to create, owner-only — so the umask
    /// cannot decide who may replace a unit.
    ///
    /// <para>The plain overload applies <c>0777 &amp; ~umask</c>, and umask 002 is the default wherever a
    /// user's primary group is their own name — the <c>pam_umask</c> usergroups behaviour Debian and Ubuntu
    /// enable. That yields a group-writable directory, which <see cref="RequireNoSharedWrite"/> then has to
    /// repair; asking for the mode at creation leaves no window in which it is wrong.</para>
    ///
    /// <para>One level at a time because the mode-taking overload applies it to the LEAF only: every
    /// ancestor it creates on the way still lands <c>0777 &amp; ~umask</c>, and a writable parent is a
    /// rename away from replacing the unit directory whole. Ancestors that already exist are left as the
    /// operator has them — <c>~/.config</c> is not this code's to tighten.</para>
    ///
    /// <para>Then chmod'd, because the requested mode is filtered through the umask exactly like the
    /// default one: it is a request, not a result. A restrictive umask strips the OWNER bits — under
    /// umask 077 the directory lands <c>0700</c>, but under umask 400 it lands <c>0300</c>, which the unit
    /// can still be written into and which <c>ListInstalled</c> then cannot enumerate. An explicit chmod
    /// is not filtered.</para></summary>
    static void CreateDirectory(string directory) {
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(directory); return; }

        var missing = new Stack<string>();

        for (var d = directory; !string.IsNullOrEmpty(d) && !Directory.Exists(d); d = Path.GetDirectoryName(d)!)
            missing.Push(d);

        while (missing.Count > 0) {
            var d = missing.Pop();

            Directory.CreateDirectory(d, OwnerOnlyDir);
            File.SetUnixFileMode(d, OwnerOnlyDir);
        }
    }

    /// <summary>Strips group and world write from the unit directory, and refuses the install if they
    /// survive — owner-only mode on the unit is no protection when someone else can replace the unit and
    /// choose what the daemon runs.
    ///
    /// <para>Repaired rather than refused outright, because the bits alone do not say another account is
    /// involved: a user-private group has exactly one member, and a directory we can chmod is one no other
    /// account controls. A chmod that fails is the case worth refusing, and the re-read is what decides —
    /// not the attempt.</para></summary>
    static void RequireNoSharedWrite(string directory, Action<string>? tighten) {
        if (OperatingSystem.IsWindows()) return;   // ACL-governed, inherited from the user profile

        if ((File.GetUnixFileMode(directory) & SharedWrite) == 0) return;

        try {
            if (tighten is not null) tighten(directory);
            else File.SetUnixFileMode(directory, File.GetUnixFileMode(directory) & ~SharedWrite);
        } catch (Exception) { /* the re-read decides, not the attempt */ }

        if ((File.GetUnixFileMode(directory) & SharedWrite) != 0)
            throw new InvalidOperationException(
                $"Refusing to write a service unit into a group- or world-writable directory: {directory}. "
              + "Another local account could replace the unit and choose what the daemon runs. Remove those "
              + "write bits with `chmod g-w,o-w` and re-run the install.");
    }

    /// <summary>Requires EXACTLY owner read+write on the open handle, repairing once.
    ///
    /// <para>Exactly, not "nothing extra": a restrictive umask can strip the owner bits too, and a
    /// <c>0000</c> unit is one launchd cannot read — an install that reported success while producing a
    /// service that never starts, and a mode the docs promise is <c>0600</c>.</para></summary>
    static void RequireOwnerOnlyHandle(SafeFileHandle handle, string path) {
        if (OperatingSystem.IsWindows()) return;

        if (File.GetUnixFileMode(handle) == OwnerOnly) return;

        File.SetUnixFileMode(handle, OwnerOnly);   // umask does not apply to an explicit chmod

        var mode = File.GetUnixFileMode(handle);

        if (mode != OwnerOnly)
            throw new InvalidOperationException(
                $"Could not establish owner-only permissions on {path} (mode is {mode}). A service unit may "
              + "carry a token-producing command, so installation fails rather than continuing.");
    }

    /// <summary>Refuses when something ABOVE the unit directory is world-writable — an account that can
    /// write a parent can rename the unit directory away and supply its own, whatever mode the unit and its
    /// own directory carry.
    ///
    /// <para>World-writable only, and group-writable deliberately not: umask 002 is the default wherever a
    /// user's primary group is their own name, so group-write on a home directory's path is the ordinary
    /// case rather than a finding, and refusing on it would fail the install this check is attached to. A
    /// world-writable directory on the way to someone's home is produced by no umask and is never
    /// deliberate. The group-write case is reported by <c>kcap daemon doctor</c>, which can advise where
    /// this cannot safely block.</para></summary>
    static void RequireNoWorldWritableAncestor(string directory, bool exceptItself = false) {
        // The unit directory itself is included and costs nothing after RequireNoSharedWrite, which has
        // stripped its world-write bit or thrown. Before that runs, it is excluded by its lexical and its
        // physical path alike, while its target's parents are still walked.
        string[] itself = exceptItself ? [Path.GetFullPath(directory), DirectoryExposure.Physical(directory)] : [];
        if (DirectoryExposure.GrantingWrite(directory, UnixFileMode.OtherWrite).FirstOrDefault(d => !itself.Contains(d))
            is not { } offender) return;

        throw new InvalidOperationException(
            $"Refusing to write a service unit below a world-writable directory: {offender}. Any local "
          + "account can rename the unit directory out from under it and choose what the daemon runs. "
          + $"Remove that write bit with `chmod o-w {offender}` and re-run the install.");
    }

    /// <summary>The ancestor checks <see cref="WriteOwnerOnly"/> makes, run on their own so an install can
    /// refuse before it touches the unit it would replace. The unit directory's own mode is left out:
    /// the writer repairs that one rather than refusing it.</summary>
    public static void RequireSafeAncestors(string unitPath) {
        if (Path.GetDirectoryName(unitPath) is not { Length: > 0 } directory) return;
        RequireNoWorldWritableAncestor(directory, exceptItself: true);
        RequireNoForeignOwnedAncestor(directory);
    }

    /// <summary>Refuses when a directory on the path to the unit is owned by another account, which can
    /// rename the unit directory away whatever the modes say. Root-owned directories pass: every path
    /// starts under them. One directory whose owner cannot be read is left to the mode check, but a system
    /// where no owner can be read at all is refused rather than passed unchecked.</summary>
    static void RequireNoForeignOwnedAncestor(string directory) {
        if (OperatingSystem.IsWindows()) return;
        if (!FileOwner.Available)
            throw new InvalidOperationException(
                "Refusing to write a service unit without checking who owns the directories above it: no working "
              + "`stat` command was found in /usr/bin, /bin or /run/current-system/sw/bin. Install coreutils and re-run the install.");

        if (DirectoryExposure.ForeignOwned(directory) is not [var (offender, owner), ..]) return;

        throw new InvalidOperationException(
            $"Refusing to write a service unit below a directory owned by another account: {offender} "
          + $"(uid {owner}). Its owner can rename the unit directory out from under it and choose what the "
          + "daemon runs. Install from a home directory whose path you or root own.");
    }

    static void RequireOwnerOnlyPath(string path) {
        if (OperatingSystem.IsWindows()) return;

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        RequireOwnerOnlyHandle(handle, path);
    }

    static void TryDelete(string path) {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { /* best-effort */ }
    }
}
