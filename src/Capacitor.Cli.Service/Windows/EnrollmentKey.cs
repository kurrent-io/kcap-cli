using System.Runtime.Versioning;
using Capacitor.Cli.Service.Sessions;
using Microsoft.Win32;

namespace Capacitor.Cli.Service.Windows;

[SupportedOSPlatform("windows")]
static class EnrollmentKey {
    /// <summary>Looked up as the user, in their own hive</summary>
    public static bool Exists(SessionUser user, string subKey) {
        using var token = UserToken.ForSession(user.SessionId) ?? throw new InvalidOperationException("the session has ended");
        return token.Impersonate(() => {
            using var key = Registry.Users.OpenSubKey($@"{user.Sid}\{subKey}");
            return key is not null;
        });
    }
}
