using System.Runtime.CompilerServices;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// <see cref="SetupImportLane"/> imports through <c>ImportCommand</c>, which takes its base URL from
/// the <c>ProfileContext</c> it is handed and from nowhere else. The browser leg's own context is
/// resolved at process start, before setup has asked for a server, so on a first run it names none —
/// and <c>setup</c> is exempt from the entry gate that refuses an unconfigured command, so an unusable
/// URL is caught by nothing until it reaches the client factory.
///
/// <para>A guard rather than a unit test because the defect is in WHICH context the call site passes,
/// and the call site sits inside an authenticated leg that a unit test cannot reach. Pinning
/// <see cref="SetupCommand.ImportContext"/> alone would pass with nothing calling it.</para>
/// </summary>
public class SetupImportLaneConstructionGuardTests {
    const string Construction = "new SetupImportLane(";

    /// <summary>Walks up from this file to the repo-root marker, so the test runner's working
    /// directory is irrelevant.</summary>
    static string RepoRoot([CallerFilePath] string here = "") {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Capacitor.slnx")))
            dir = Path.GetDirectoryName(dir);

        return dir ?? throw new InvalidOperationException($"Could not locate repo root walking up from {here}");
    }

    /// The argument text of the call whose open paren sits at <paramref name="from"/>. Paren-matched
    /// rather than read off one line, so a call that wraps reads the same as one that does not.
    static string Arguments(string source, int from) {
        var depth  = 0;
        var quoted = false;

        for (var i = from; i < source.Length; i++) {
            var c = source[i];

            if (quoted) {
                if (c == '\\') i++;
                else if (c == '"') quoted = false;

                continue;
            }

            switch (c) {
                case '"': quoted = true; break;
                case '(': depth++; break;
                case ')':
                    if (--depth == 0) return source[(from + 1)..i];

                    break;
            }
        }

        throw new InvalidOperationException($"Unbalanced argument list at offset {from}");
    }

    [Test]
    public async Task Every_construction_of_the_lane_names_the_server_this_run_resolved() {
        var sites = new List<(string File, int No, string Args)>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)) {
            var source = File.ReadAllText(file);

            for (var at = source.IndexOf(Construction, StringComparison.Ordinal); at >= 0;
                 at = source.IndexOf(Construction, at + 1, StringComparison.Ordinal)) {
                var open = at + Construction.Length - 1;
                var line = source.Take(at).Count(c => c == '\n') + 1;

                sites.Add((Path.GetFileName(file), line, Arguments(source, open)));
            }
        }

        await Assert.That(sites).IsNotEmpty()
                    .Because("a rename that empties this scan would make the guard pass for the wrong reason");

        foreach (var site in sites) {
            await Assert.That(site.Args).Contains("ImportContext(")
                        .Because($"{site.File}:{site.No} hands the lane a context that need not name a server");
        }
    }
}
