using System.Diagnostics;
using System.Runtime.InteropServices;

namespace kapacitor.Auth;

/// <summary>
/// Best-effort clipboard writer. Used to make the GitHub device-flow fallback less
/// painful — the user pastes the user_code into the verification page instead of
/// retyping it. All failures are swallowed; the caller still prints the code.
/// </summary>
public static class Clipboard {
    public static bool TryCopy(string text) {
        try {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))     return RunWithStdin("pbcopy", "", text);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return RunWithStdin("clip", "", text);

            // Linux: prefer Wayland, fall back to X11
            if (RunWithStdin("wl-copy", "", text)) return true;
            return RunWithStdin("xclip", "-selection clipboard", text);
        } catch {
            return false;
        }
    }

    static bool RunWithStdin(string fileName, string args, string stdin) {
        try {
            var psi = new ProcessStartInfo(fileName, args) {
                RedirectStandardInput = true,
                UseShellExecute       = false,
                CreateNoWindow        = true
            };

            using var p = Process.Start(psi);
            if (p is null) return false;
            p.StandardInput.Write(stdin);
            p.StandardInput.Close();
            p.WaitForExit(2000);
            return p.ExitCode == 0;
        } catch {
            return false;
        }
    }
}
