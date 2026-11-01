using System.Runtime.Versioning;
using Capacitor.Cli.Service.Sessions;
using static Capacitor.Cli.Service.Native.Win32;

namespace Capacitor.Cli.Service.Windows;

[SupportedOSPlatform("windows")]
static unsafe class WtsSessions {
    public static IReadOnlyList<int> Active() {
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var sessions, out var count)) throw Failure("WTSEnumerateSessions");
        try {
            var ids = new List<int>(count);
            for (var i = 0; i < count; i++)
                if (sessions[i].State is WTSActive or WTSDisconnected) ids.Add(sessions[i].SessionId);
            return ids;
        } finally {
            WTSFreeMemory(sessions);
        }
    }

    /// <summary>Null when no user is signed in to the session</summary>
    public static SessionUser? UserOf(int sessionId) {
        using var token = UserToken.ForSession(sessionId);
        return token is null ? null : new SessionUser(sessionId, token.Sid(), token.ProfileDirectory());
    }
}
