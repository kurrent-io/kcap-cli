using System.Security;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Services;

/// <summary>
/// Pure text helpers for rendering service units. Each value interpolated into
/// a unit/wrapper is escaped for its target format so generators never emit
/// malformed markup, regardless of daemon name / path content.
/// </summary>
static class ServiceText {
    /// <summary>Sanitized, portable service id — reuses the daemon lock-file rule.</summary>
    public static string ServiceId(string name) => DaemonLockPaths.Sanitize(name);

    /// <summary>XML-escape for plist and Task Scheduler XML (escapes &amp; &lt; &gt; " ').</summary>
    public static string Xml(string value) => SecurityElement.Escape(value) ?? "";

    /// <summary>Escape a value for a batch <c>set "KEY=value"</c> line: percent-signs doubled.</summary>
    public static string CmdValue(string value) => value.Replace("%", "%%");

    /// <summary>Escape a systemd <c>Environment=</c>/<c>Description=</c> value: no raw newlines.</summary>
    public static string SystemdValue(string value) =>
        value.Replace("\r", " ").Replace("\n", " ");
}
