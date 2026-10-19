using System.Text;
using System.Xml;
using System.Xml.Linq;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.LocalEdition;

/// <summary>Parses the root a local-server unit names, from launchd's and systemd's output and from the unit
/// files.</summary>
static class LocalServerOwnerReader {
    public static OwnerReading FromPrint(int exitCode, string stdout, string stderr) {
        switch (LaunchdUnit.ClassifyPrint(exitCode, stdout, stderr)) {
            case LabelProbe.Absent:  return OwnerReading.Absent.Instance;
            case LabelProbe.Unknown: return new OwnerReading.Unreadable($"launchctl print failed (exit {exitCode}): {stderr.Trim()}");
        }

        // Only the job's own block: launchd also prints "inherited environment", which is not this job's.
        var inBlock = false;
        foreach (var raw in stdout.Split('\n')) {
            var line = raw.Trim();

            if (!inBlock) {
                inBlock = line == "environment = {";
                continue;
            }

            if (line == "}") break;

            var prefix = LocalRoot.EnvVar + " => ";
            if (line.StartsWith(prefix, StringComparison.Ordinal)) return new OwnerReading.Root(line[prefix.Length..]);
        }

        return new OwnerReading.Unreadable($"the loaded job names no {LocalRoot.EnvVar}");
    }

    public static OwnerReading FromPlist(string path) {
        switch (LaunchdUnit.TryReadPlist(path, out var content)) {
            case LaunchdUnit.PlistRead.Absent:     return OwnerReading.Absent.Instance;
            case LaunchdUnit.PlistRead.Unreadable: return new OwnerReading.Unreadable($"{path} could not be read");
        }

        try {
            using var reader = XmlReader.Create(new StringReader(content!), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            var doc = XDocument.Load(reader);

            var env = doc.Root?.Element("dict")?.Elements("key").FirstOrDefault(k => k.Value == "EnvironmentVariables")?.ElementsAfterSelf().FirstOrDefault();
            var key = env?.Name == "dict" ? env.Elements("key").FirstOrDefault(k => k.Value == LocalRoot.EnvVar) : null;

            return key?.ElementsAfterSelf().FirstOrDefault() is { Name.LocalName: "string" } value
                ? new OwnerReading.Root(value.Value)
                : new OwnerReading.Unreadable($"{path} names no {LocalRoot.EnvVar}");
        } catch (XmlException) {
            return new OwnerReading.Unreadable($"{path} is not a readable property list");
        }
    }

    public static OwnerReading FromShow(int exitCode, string stdout, string stderr) {
        if (exitCode != 0) return new OwnerReading.Unreadable($"systemctl show failed (exit {exitCode}): {stderr.Trim()}");

        var lines = stdout.Split('\n', StringSplitOptions.TrimEntries);
        if (lines.Contains("LoadState=not-found")) {
            // A deleted unit file can still be running; only a unit that is not running is gone.
            return lines.Contains("ActiveState=inactive") || lines.Contains("ActiveState=failed")
                ? OwnerReading.Absent.Instance
                : new OwnerReading.Unreadable("the unit is not found but is still active");
        }

        var env = lines.FirstOrDefault(l => l.StartsWith("Environment=", StringComparison.Ordinal));

        return RootIn(env is null ? [] : Assignments(env["Environment=".Length..]), "the loaded unit");
    }

    public static OwnerReading FromUnitFile(string path) {
        switch (LaunchdUnit.TryReadPlist(path, out var content)) {
            case LaunchdUnit.PlistRead.Absent:     return OwnerReading.Absent.Instance;
            case LaunchdUnit.PlistRead.Unreadable: return new OwnerReading.Unreadable($"{path} could not be read");
        }

        // systemd applies the lines in order: an empty one resets everything before it.
        var assignments = new List<string>();
        foreach (var line in content!.Split('\n', StringSplitOptions.TrimEntries)) {
            if (!line.StartsWith("Environment=", StringComparison.Ordinal)) continue;

            var value = line["Environment=".Length..].Trim();
            if (value.Length == 0) assignments.Clear();
            else assignments.AddRange(Assignments(value).Select(a => a.Replace("%%", "%")));
        }

        return RootIn(assignments, path);
    }

    static OwnerReading RootIn(IEnumerable<string> assignments, string what) {
        var prefix = LocalRoot.EnvVar + "=";
        var match  = assignments.LastOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal));

        return match is null
            ? new OwnerReading.Unreadable($"{what} names no {LocalRoot.EnvVar}")
            : new OwnerReading.Root(match[prefix.Length..]);
    }

    /// <summary>Splits systemd's space-separated <c>KEY=VALUE</c> words, where a word in double quotes may
    /// contain spaces and backslash escapes.</summary>
    static IEnumerable<string> Assignments(string text) {
        var word    = new StringBuilder();
        var quoted  = false;
        var started = false;

        for (var i = 0; i < text.Length; i++) {
            var c = text[i];

            if (quoted && c == '\\' && i + 1 < text.Length) {
                word.Append(text[++i]);
            } else if (c == '"') {
                quoted  = !quoted;
                started = true;
            } else if (!quoted && char.IsWhiteSpace(c)) {
                if (started) yield return word.ToString();
                word.Clear();
                started = false;
            } else {
                word.Append(c);
                started = true;
            }
        }

        if (started) yield return word.ToString();
    }
}
