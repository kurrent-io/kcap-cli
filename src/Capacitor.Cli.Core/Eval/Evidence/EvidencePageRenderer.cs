using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>Turns an evidence route's response body into the page a judge receives — the body unchanged but for page and
/// has_next in front, a cite on every row and next_cursor removed — and records what the page delivered for the ledger.</summary>
public static class EvidencePageRenderer {
    public const int MaxSourceRows = 50;

    /// <summary>Per tool, the row array and the field inside each row that carries its ref; read_body cites its root.</summary>
    public static readonly IReadOnlyDictionary<string, string?> RowRefFields = new Dictionary<string, string?>(StringComparer.Ordinal) {
        ["list_turns"]          = "turns[].turn_ref",
        ["read_events"]         = "entries[].ref",
        ["read_body"]           = "reference",
        ["list_calls"]          = "calls[].locator.ref",
        ["summarize_calls"]     = null,
        ["list_authorizations"] = "authorizations[].authorization_ref"
    };

    /// <summary>Selects the rendering of a first view's plan_ledger section. No judge tool reads or continues a plan ledger, so
    /// the page it renders names no tool. Each plan's documents and tasks are cited inside it, a task once for its title_ref and
    /// again for a stated status_ref.</summary>
    public const string PlanLedgerTool = "read_plan_ledger";

    public static JudgeLedgerPage Render(int seq, string handle, string tool, string argsJson, string body) {
        var planLedger = tool == PlanLedgerTool;
        string? path = null;
        if (!planLedger && !RowRefFields.TryGetValue(tool, out path)) throw new ArgumentException($"no page rendering for {tool}", nameof(tool));
        var (array, refPath) = Split(path);

        using var doc = JsonDocument.Parse(body);
        var root   = doc.RootElement;
        var isBody = tool == "read_body";
        var next   = isBody ? root.Num("next_offset")?.ToString(CultureInfo.InvariantCulture) : root.Str("next_cursor");
        var source = root.Str("source_id");

        var cites     = new Dictionary<string, string>(StringComparer.Ordinal);
        var revisions = new List<long>();
        var turns     = new List<(string Source, int Index)>();
        var bodies    = new List<(string Ref, string Field, int? Ordinal)>();
        var detail    = new List<(string Source, long Revision, int Offset, int Length)>();
        var cited     = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        var ledgered  = new List<string>();
        (int Offset, int Length)? contentSpan = null;

        string Cite(string reference) {
            var cite = JudgeCiteHandles.Row(handle, cites.Count + 1);
            cites[cite] = reference;
            return cite;
        }

        // a ledger row shows part of an entry (one task of a snapshot, a document without its text), so its ref may be
        // cited but the entry is not delivered until an events page serves it
        string Citable(string reference) {
            if (EvidenceRefText.TryParse(reference, out var r) && r.Form == EvidenceRefForm.Event) {
                if (!cited.TryGetValue(r.SourceId, out var list)) cited[r.SourceId] = list = [];
                list.Add(r.A);
            }
            return Cite(reference);
        }

        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer)) {
            w.WriteStartObject();
            w.WriteString("page", handle);
            w.WriteBoolean("has_next", next is not null);
            if (array is null && refPath is not null && ResolveRef(root, refPath) is { } rootRef) w.WriteString("cite", Cite(rootRef));

            foreach (var property in root.EnumerateObject()) {
                if (property.NameEquals("next_cursor")) continue;

                if (planLedger && property.NameEquals("plans") && property.Value.IsArray) {
                    WritePlans(w, property, Citable);
                    continue;
                }

                if (array is not null && property.NameEquals(array) && property.Value.IsArray) {
                    w.WriteStartArray(property.Name);
                    var index = 0;
                    foreach (var row in property.Value.EnumerateArray()) {
                        // Every element after the first is preceded by one comma.
                        var start = (int)Position(w) + (index++ > 0 ? 1 : 0);
                        if (!row.IsObject) { row.WriteTo(w); continue; }
                        w.WriteStartObject();
                        if (ResolveRef(row, refPath!) is { } rowRef) w.WriteString("cite", Cite(rowRef));
                        foreach (var field in row.EnumerateObject()) field.WriteTo(w);
                        w.WriteEndObject();

                        if (tool == "read_events" && source is not null && row.Num("revision") is { } revision) {
                            revisions.Add(revision);
                            detail.Add((source, revision, start, (int)Position(w) - start));
                            bodies.AddRange(EvidenceCanonicalContent.Deferred(row));
                        } else if (tool == "list_turns" && source is not null && row.Num("index") is { } turn) {
                            turns.Add((source, (int)turn));
                        }
                    }
                    w.WriteEndArray();
                    continue;
                }

                var propertyStart = (int)Position(w) + 1;
                property.WriteTo(w);
                if (isBody && property.NameEquals("content")) contentSpan = (propertyStart, (int)Position(w) - propertyStart);
            }
            w.WriteEndObject();
        }

        if (isBody && root.Str("reference") is { } reference && root.Str("field") is { } bodyField) {
            bodies.Add((reference, bodyField, root.Num("ordinal") is { } o ? (int)o : null));
            if (contentSpan is { } span && EvidenceRefText.TryParse(reference, out var parsed) && parsed.Form == EvidenceRefForm.Event)
                detail.Add((parsed.SourceId, parsed.A, span.Offset, span.Length));
        }

        var text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        if (planLedger && root.Arr("plans") is { } plans) {
            foreach (var plan in plans.EnumerateArray())
                if (plan.Arr("sources") is { } lanes) ledgered.AddRange(lanes.EnumerateArray().Select(l => l.GetString()).OfType<string>());
        }
        var runs = planLedger || source is null ? [] : Runs(source, revisions);
        return new JudgeLedgerPage(seq, handle, planLedger ? "" : tool, argsJson, isBody ? null : source, text, runs, turns, bodies, detail, cites, next is not null, next) {
            LedgerSources = ledgered,
            Citable       = [.. cited.SelectMany(c => Runs(c.Key, c.Value))]
        };
    }

    static void WritePlans(Utf8JsonWriter w, JsonProperty plans, Func<string, string> citable) {
        w.WriteStartArray(plans.Name);
        foreach (var plan in plans.Value.EnumerateArray()) {
            if (!plan.IsObject) { plan.WriteTo(w); continue; }
            w.WriteStartObject();
            foreach (var field in plan.EnumerateObject()) {
                if (field.NameEquals("documents") && field.Value.IsArray) WriteCitedRows(w, field, "ref", citable);
                else if (field.NameEquals("tasks") && field.Value.IsArray) WriteCitedRows(w, field, "title_ref", citable);
                else field.WriteTo(w);
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    static void WriteCitedRows(Utf8JsonWriter w, JsonProperty rows, string refField, Func<string, string> delivered) {
        w.WriteStartArray(rows.Name);
        foreach (var row in rows.Value.EnumerateArray()) {
            if (!row.IsObject) { row.WriteTo(w); continue; }
            w.WriteStartObject();
            if (row.Str(refField) is { } reference) w.WriteString("cite", delivered(reference));
            foreach (var field in row.EnumerateObject()) {
                if (field.NameEquals("status_ref") && field.Value.IsString) w.WriteString("status_cite", delivered(field.Value.GetString()!));
                field.WriteTo(w);
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    public static JudgeLedgerPage RenderSources(int seq, string handle, string argsJson, IReadOnlyList<EvidenceRunSource> sources, int from) {
        from = Math.Clamp(from, 0, sources.Count);
        var rows = sources.Skip(from).Take(MaxSourceRows).ToList();
        var more = from + rows.Count < sources.Count;

        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer)) {
            w.WriteStartObject();
            w.WriteString("page", handle);
            w.WriteBoolean("has_next", more);
            if (more) w.WriteNumber("next_from", from + rows.Count);
            w.WriteNumber("total", sources.Count);
            w.WriteStartArray("sources");
            foreach (var s in rows) {
                w.WriteStartObject();
                w.WriteString("source", s.SourceId);
                w.WriteString("kind", s.Kind);
                w.WriteBoolean("available", s.Available);
                w.WriteNumber("first_revision", s.FirstRevision);
                w.WriteNumber("revision_cutoff", s.RevisionCutoff);
                if (s.TurnCount is { } n) w.WriteNumber("turn_count", n); else w.WriteNull("turn_count");
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        // Continued through list_sources(from), never open_page, so the ledger never counts it as an unfollowed page.
        var text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        return new JudgeLedgerPage(seq, handle, "list_sources", argsJson, null, text, [], [], [], [], FrozenDictionary<string, string>.Empty, false, null);
    }

    static long Position(Utf8JsonWriter w) => w.BytesCommitted + w.BytesPending;

    static (string? Array, string[]? RefPath) Split(string? path) {
        if (path is null) return (null, null);
        var marker = path.IndexOf("[].", StringComparison.Ordinal);
        return marker < 0 ? (null, path.Split('.')) : (path[..marker], path[(marker + 3)..].Split('.'));
    }

    static string? ResolveRef(JsonElement element, string[] path) {
        foreach (var part in path[..^1]) {
            if (element.Obj(part) is not { } inner) return null;
            element = inner;
        }
        return element.Str(path[^1]);
    }

    static List<(string Source, long From, long To)> Runs(string source, List<long> revisions) {
        var runs = new List<(string Source, long From, long To)>();
        foreach (var r in revisions.Order()) {
            if (runs.Count > 0 && runs[^1].To + 1 == r) runs[^1] = (source, runs[^1].From, r);
            else if (runs.Count == 0 || runs[^1].To < r) runs.Add((source, r, r));
        }
        return runs;
    }
}
