using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

/// <summary>
/// <c>kcap artefact</c> — publish a self-contained HTML page to the server and get a link back.
///
/// <para>The command deliberately does not compose that link itself: the server returns the URL it
/// will actually serve, and anything composed here would be a guess that goes stale the moment a
/// tenant moves.</para>
/// </summary>
class ArtefactCommand(IArtefactsApi artefacts) {
    const string Usage = """
        Usage:
          kcap artefact publish <file.html> [--title T] [--description D]
                                [--visibility none|org|scoped] [--to user:<id>|team:<slug>|project:<id>]...
                                [--session <id>]... [--update <artefactId>]
          kcap artefact list [--mine]
          kcap artefact share <artefactId> --visibility none|org|scoped [--to ...]...
          kcap artefact delete <artefactId>

        --to names one audience member and may be repeated; it is only read under `scoped`.
        --session cites a session and may be repeated; it defaults to KCAP_SESSION_ID when set.
        --update publishes a new version of an existing artefact, which keeps its URL.
        """;

    public async Task<int> HandleAsync(string[] args) {
        if (args.Length < 2) return Fail(null);

        try {
            return args[1] switch {
                "publish" => await PublishAsync(args),
                "list"    => await ListAsync(args),
                "share"   => await ShareAsync(args),
                "delete"  => await DeleteAsync(args),
                _         => Fail($"Unknown artefact subcommand: {args[1]}")
            };
        } catch (CapacitorApiException ex) {
            await Console.Error.WriteLineAsync(ex.Message);
            return 1;
        }
    }

    async Task<int> PublishAsync(string[] args) {
        if (Parse(args, "--title", "--description", "--visibility", "--to", "--session", "--update") is not { } flags) return 1;
        if (flags.Positionals.Count != 1) return Fail("Publish takes exactly one HTML file.");

        var html = ReadHtml(flags.Positionals[0]);
        if (html is null) return 1;

        // --update is a new version of something that already exists, and the server owns everything
        // else about it: a title or an audience passed here would be silently dropped, so say so
        // rather than appear to have applied them.
        if (flags.Value("--update") is { Length: > 0 } updateId) {
            if (flags.Has("--title") || flags.Has("--description") || flags.Has("--visibility") || flags.Has("--to"))
                return Fail("--update publishes content only. Change the audience with `kcap artefact share`; a title cannot be changed after publishing.");

            return Report(await artefacts.PublishVersionAsync(updateId, html), "published");
        }

        // A title the caller did not give is the file's own name: better than "Untitled", and it is
        // what they will recognize in a listing.
        var title = flags.Value("--title") ?? Path.GetFileNameWithoutExtension(flags.Positionals[0]);

        var sources = flags.Values("--session");
        if (sources.Count == 0 && ArgParsing.ResolveSessionIdFromEnv() is { Length: > 0 } ambient) sources.Add(ambient);

        var grants = ParseGrants(flags.Values("--to"));
        if (grants is null) return 1;

        var body = new PublishArtefactBody(title, html, flags.Value("--description"),
                                           flags.Value("--visibility"), Nullify(grants), Nullify(sources));

        return Report(await artefacts.PublishAsync(body), "published");
    }

    async Task<int> ListAsync(string[] args) {
        if (Parse(args, "--mine") is not { } flags) return 1;

        var listed = await artefacts.ListAsync();

        if (flags.Has("--mine")) listed = [.. listed.Where(a => a.IsOwner)];

        if (listed.Count == 0) {
            Console.WriteLine(flags.Has("--mine") ? "No artefacts of yours." : "No artefacts.");
            return 0;
        }

        foreach (var a in listed)
            Console.WriteLine($"{a.ArtefactId}  v{a.LatestVersion,-3} {a.Visibility,-7} {a.UpdatedAt:yyyy-MM-dd}  {a.Title}");

        return 0;
    }

    async Task<int> ShareAsync(string[] args) {
        if (Parse(args, "--visibility", "--to") is not { } flags) return 1;
        if (flags.Positionals.Count != 1) return Fail("Share takes exactly one artefact id.");

        if (flags.Value("--visibility") is not { Length: > 0 } visibility)
            return Fail("Share needs --visibility (none, org or scoped).");

        var grants = ParseGrants(flags.Values("--to"));
        if (grants is null) return 1;

        // A full replacement: under `scoped`, naming nobody is an audience of nobody, said outright.
        return Report(await artefacts.SetVisibilityAsync(flags.Positionals[0], visibility, grants), "shared");
    }

    async Task<int> DeleteAsync(string[] args) {
        if (Parse(args) is not { } flags) return 1;
        if (flags.Positionals.Count != 1) return Fail("Delete takes exactly one artefact id.");

        return Report(await artefacts.DeleteAsync(flags.Positionals[0]), "deleted");
    }

    /// <summary>Turns a write result into output and an exit code. A refusal the server named is
    /// printed as it named it — the CLI never restates a rule the server owns — and a limit travels
    /// with its value, so nobody has to find the ceiling by bisection.</summary>
    static int Report(ArtefactWriteResult result, string verb) {
        switch (result) {
            case ArtefactWriteResult.Written(var detail):
                Console.WriteLine($"{detail.Artefact.Title} {verb} (v{detail.Artefact.LatestVersion})");
                Console.WriteLine(detail.Artefact.Url);
                return 0;

            case ArtefactWriteResult.Gone:
                Console.WriteLine($"Artefact {verb}.");
                return 0;

            case ArtefactWriteResult.Refused(var error):
                Console.Error.WriteLine(error.Limit is { } limit
                    ? $"{error.Message} (limit: {limit})"
                    : error.Message);
                return 1;

            case ArtefactWriteResult.NotFound:
                Console.Error.WriteLine("No such artefact, or it is not visible to this profile.");
                return 1;

            case ArtefactWriteResult.NotYours:
                Console.Error.WriteLine("That artefact is not yours to change.");
                return 1;

            default:
                Console.Error.WriteLine("Unrecognized server response.");
                return 1;
        }
    }

    static string? ReadHtml(string path) {
        if (!File.Exists(path)) {
            Console.Error.WriteLine($"No such file: {path}");
            return null;
        }

        var html = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(html)) {
            Console.Error.WriteLine($"{path} is empty.");
            return null;
        }

        return html;
    }

    /// <summary>Parses <c>type:id</c>, the <c>--to</c> grammar. Returns null once it has reported a
    /// malformed entry — a partial audience is worse than none, so one bad grant stops the whole
    /// write rather than quietly narrowing who can see the artefact.</summary>
    static List<ArtefactGrantDto>? ParseGrants(List<string> raw) {
        var grants = new List<ArtefactGrantDto>();

        foreach (var entry in raw) {
            var separator = entry.IndexOf(':');
            if (separator <= 0 || separator == entry.Length - 1) {
                Console.Error.WriteLine($"Malformed --to \"{entry}\" — expected user:<id>, team:<slug> or project:<id>.");
                return null;
            }

            var type = entry[..separator];
            var id   = entry[(separator + 1)..];

            // The name is display only and the CLI has no directory to look one up in; the id is the
            // honest stand-in, and the server replaces it with the real name when it knows one.
            grants.Add(new ArtefactGrantDto(type, id, id));
        }

        return grants;
    }

    static List<T>? Nullify<T>(List<T> items) => items.Count == 0 ? null : items;

    /// <summary>A value flag given without its value stops the command: read as absent, a forgotten
    /// <c>--update</c> id publishes a second artefact and a forgotten <c>--to</c> empties an
    /// audience.</summary>
    static Flags? Parse(string[] args, params string[] known) {
        var flags = Flags.Parse(args, 2);

        // A flag the command does not read is refused too: `share --title` would otherwise report a
        // rename that never happened.
        if (flags.Unknown(known) is { } unknown) {
            Fail($"{unknown} is not a flag of this command.");

            return null;
        }

        if (flags.MissingValue() is not { } bare) return flags;

        Fail($"{bare} needs a value.");

        return null;
    }

    static int Fail(string? message) {
        if (message is not null) Console.Error.WriteLine(message);
        Console.Error.WriteLine(Usage);
        return 1;
    }

    /// <summary>The flag shape this command uses: repeatable value flags, bare switches and
    /// positionals, split in one pass so a flag's value is never mistaken for a positional.</summary>
    sealed class Flags {
        readonly Dictionary<string, List<string>> values = new(StringComparer.Ordinal);

        public List<string> Positionals { get; } = [];

        public bool Has(string flag) => values.ContainsKey(flag);

        public string? Value(string flag) => values.TryGetValue(flag, out var v) ? v[^1] : null;

        public List<string> Values(string flag) =>
            values.TryGetValue(flag, out var v) ? [.. v.Where(x => x.Length > 0)] : [];

        /// <summary>The first flag that takes a value and was given none.</summary>
        public string? MissingValue() =>
            values.FirstOrDefault(f => !Switches.Contains(f.Key) && f.Value.Contains("")).Key;

        static readonly HashSet<string> Switches = new(StringComparer.Ordinal) { "--mine" };

        public string? Unknown(IReadOnlyCollection<string> known) =>
            values.Keys.FirstOrDefault(k => !known.Contains(k, StringComparer.Ordinal));

        public static Flags Parse(string[] args, int from) {
            var flags = new Flags();

            for (var i = from; i < args.Length; i++) {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) {
                    flags.Positionals.Add(args[i]);
                    continue;
                }

                var name = args[i];

                // A flag followed by another flag, or by nothing, is recorded as present with an
                // empty value: right for a switch, and what MissingValue() reports for any other.
                var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[++i]
                    : "";

                if (!flags.values.TryGetValue(name, out var list)) flags.values[name] = list = [];
                list.Add(value);
            }

            return flags;
        }
    }
}
