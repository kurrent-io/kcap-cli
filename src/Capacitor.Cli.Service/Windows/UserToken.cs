using System.Runtime.Versioning;
using System.Security.Principal;
using static Capacitor.Cli.Service.Native.Win32;

namespace Capacitor.Cli.Service.Windows;

/// <summary>A primary token for the user signed in to a session. Owns the handle</summary>
[SupportedOSPlatform("windows")]
sealed unsafe class UserToken : IDisposable {
    public IntPtr Handle { get; private set; }

    UserToken(IntPtr handle) => Handle = handle;

    /// <summary>Null when the session has no signed-in user</summary>
    public static UserToken? ForSession(int sessionId) {
        if (!WTSQueryUserToken(sessionId, out var impersonation)) return null;
        try {
            if (!DuplicateTokenEx(impersonation, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var primary))
                throw Failure("DuplicateTokenEx");
            return new UserToken(primary);
        } finally {
            CloseHandle(impersonation);
        }
    }

    public string Sid() {
        GetTokenInformation(Handle, TokenUser, null, 0, out var needed);
        var buffer = stackalloc byte[needed];
        if (!GetTokenInformation(Handle, TokenUser, buffer, needed, out _)) throw Failure("GetTokenInformation(TokenUser)");
        // TOKEN_USER opens with SID_AND_ATTRIBUTES, whose first field is the SID pointer.
        return new SecurityIdentifier(*(IntPtr*)buffer).Value;
    }

    public string ProfileDirectory() {
        var size = 0;
        GetUserProfileDirectoryW(Handle, null, ref size);
        var buffer = stackalloc char[size];
        if (!GetUserProfileDirectoryW(Handle, buffer, ref size)) throw Failure("GetUserProfileDirectory");
        return new string(buffer, 0, size - 1);
    }

    /// <summary>The environment Windows builds for this user, as Explorer would hand it to a child</summary>
    public Dictionary<string, string> Environment() {
        if (!CreateEnvironmentBlock(out var block, Handle, false)) throw Failure("CreateEnvironmentBlock");
        try {
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var p = (char*)block;
            while (*p != '\0') {
                var entry = new string(p);
                var eq = entry.IndexOf('=', 1);
                if (eq > 0) env[entry[..eq]] = entry[(eq + 1)..];
                p += entry.Length + 1;
            }
            return env;
        } finally {
            DestroyEnvironmentBlock(block);
        }
    }

    /// <summary>Runs <paramref name="action"/> on this thread as the user, so a file or key the user could not
    /// open stays closed to it whatever SYSTEM could reach</summary>
    public T Impersonate<T>(Func<T> action) {
        if (!ImpersonateLoggedOnUser(Handle)) throw Failure("ImpersonateLoggedOnUser");
        try {
            return action();
        } finally {
            if (!RevertToSelf()) System.Environment.FailFast("RevertToSelf failed; the service thread would keep a user's identity");
        }
    }

    public void Dispose() {
        if (Handle == IntPtr.Zero) return;
        CloseHandle(Handle);
        Handle = IntPtr.Zero;
    }
}
