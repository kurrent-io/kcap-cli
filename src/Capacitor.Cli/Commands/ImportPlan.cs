using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.FirstRun;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Commands;

/// <summary>
/// What setup's detached child imports: the server the foreground imported to and each chosen level,
/// run in order.
/// </summary>
/// <remarks>
/// The server travels here because the browser leg runs before setup saves it to the profile, so the
/// child's own resolution would name no server, or the previous one.
/// </remarks>
internal sealed record ImportPlan(string ServerUrl, IReadOnlyList<ImportPlanLevel> Levels) {
    /// <summary>Honoured only beside <see cref="DetachedImportLog.EnvVar"/>, so a plain
    /// <c>kcap import</c> never reads it.</summary>
    public const string EnvVar = "KCAP_IMPORT_PLAN";

    public const int SchemaVersion = 1;

    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    const string DateFormat = "yyyy-MM-dd";

    public static string PathFor(ConfigRoot config, string runId) => config.Path($"import-plan-{runId}.json");

    public string ToJson() {
        var levels = new JsonArray();
        foreach (var level in Levels) {
            var repos = new JsonArray();
            foreach (var repo in level.Repos) repos.Add((JsonNode?)repo.Slug);

            JsonArray? vendors = null;
            if (level.Vendors is { } ids) {
                vendors = new JsonArray();
                foreach (var id in ids) vendors.Add((JsonNode?)id.VendorId);
            }

            levels.Add((JsonNode)new JsonObject {
                ["level"]      = Token(level.Level),
                ["repos"]      = repos,
                ["since"]      = level.Since is { } since ? (JsonNode?)since.ToString(DateFormat, CultureInfo.InvariantCulture) : null,
                ["vendors"]    = vendors,
                ["skip_title"] = level.SkipTitle,
            });
        }

        var json = new JsonObject {
            ["schema_version"] = SchemaVersion,
            ["server_url"]     = ServerUrl,
            ["levels"]         = levels,
        };

        return json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Null on anything this build would have to guess at: an unknown level or vendor, a
    /// repository that is not <c>owner/name</c>, an empty level list.</summary>
    public static ImportPlan? Parse(string json) {
        try {
            using var doc  = JsonDocument.Parse(json);
            var       root = doc.RootElement;

            if (root.Num("schema_version") != SchemaVersion) return null;
            if (root.Str("server_url") is not { Length: > 0 } server) return null;
            if (root.Arr("levels") is not { } levelsEl) return null;

            var levels = new List<ImportPlanLevel>();
            foreach (var el in levelsEl.EnumerateArray()) {
                if (ParseLevel(el) is not { } level) return null;
                levels.Add(level);
            }

            return levels.Count == 0 ? null : new ImportPlan(server, levels);
        } catch (JsonException) {
            return null;
        }
    }

    static ImportPlanLevel? ParseLevel(JsonElement el) {
        FirstRunImportLevel? level = el.Str("level") switch {
            "only_me" => FirstRunImportLevel.OnlyMe,
            "shared"  => FirstRunImportLevel.Shared,
            _         => null,
        };
        if (level is not { } lvl) return null;
        if (el.Bool("skip_title") is not { } skipTitle) return null;

        if (el.Arr("repos") is not { } reposEl) return null;
        var repos = new List<FirstRunImportChoice>();
        foreach (var repo in reposEl.EnumerateArray()) {
            var parts = repo.IsString ? repo.GetString()!.Split('/') : [];
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) return null;
            repos.Add(new FirstRunImportChoice(parts[0], parts[1], lvl));
        }
        if (repos.Count == 0) return null;

        DateOnly? since = null;
        if (el.Prop("since") is { } sinceEl && !sinceEl.IsNull) {
            if (!sinceEl.IsString
             || !DateOnly.TryParseExact(sinceEl.GetString(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return null;
            since = parsed;
        }

        List<HarnessId>? vendors = null;
        if (el.Prop("vendors") is { } vendorsEl && !vendorsEl.IsNull) {
            if (!vendorsEl.IsArray) return null;
            vendors = [];
            foreach (var v in vendorsEl.EnumerateArray()) {
                if (HarnessId.From(v.IsString ? v.GetString() : null) is not { } id) return null;
                vendors.Add(id);
            }
        }

        return new ImportPlanLevel(lvl, repos, since, vendors, skipTitle);
    }

    /// <summary>Null when the file is missing, unreadable or not a plan.</summary>
    public static ImportPlan? Read(string path) {
        try {
            return Parse(File.ReadAllText(path));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
            return null;
        }
    }

    /// <summary>Owner-only and refusing an existing path; a write that fails part-way leaves no file.</summary>
    public void Write(string path) {
        var created = false;
        try {
            using var stream = OwnerOnlyFile.CreateNew(path);
            created = true;
            using var writer = new StreamWriter(stream);
            writer.Write(ToJson());
        } catch when (created) {
            try { File.Delete(path); } catch { /* best effort */ }

            throw;
        }
    }

    /// <summary>Absolute <c>https</c>, or <c>http</c> to a loopback host — nothing a tampered plan
    /// could use to send this machine's credentials in the clear.</summary>
    public static bool IsUsableServer(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
     && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    public static void Prune(ConfigRoot config, DateTimeOffset now) {
        try {
            foreach (var path in Directory.EnumerateFiles(config.Directory, "import-plan-*.json")) {
                try {
                    if (now - File.GetLastWriteTimeUtc(path) > Retention) File.Delete(path);
                } catch { /* another process may own it; best effort */ }
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static string Token(FirstRunImportLevel level) => level switch {
        FirstRunImportLevel.OnlyMe => "only_me",
        FirstRunImportLevel.Shared => "shared",
    };
}
