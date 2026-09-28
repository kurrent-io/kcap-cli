using System.Text.Json;

namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>Everything the MCP process needs for one question, handed over in an owner-only file rather than argv or the
/// environment: the scope token, the client deadline, the whole manifest, the budgets and the seeded pages.</summary>
public sealed record EvidenceRunFile(
        string EvalRunId, string QuestionId, string RootSessionId, string ScopeVersion, string Token, DateTimeOffset Deadline,
        IReadOnlyList<EvidenceRunSource> Sources, EvidenceRunBudgets Budgets, DateTimeOffset SoftDeadline, string LedgerPath,
        IReadOnlyList<JudgeLedgerPage> SeededPages) {
    public const int CurrentVersion = 1;

    public void WriteTo(Stream stream) {
        using var w = new Utf8JsonWriter(stream);
        w.WriteStartObject();
        w.WriteNumber("version", CurrentVersion);
        w.WriteString("eval_run_id", EvalRunId);
        w.WriteString("question_id", QuestionId);
        w.WriteString("root_session_id", RootSessionId);
        w.WriteString("scope_version", ScopeVersion);
        w.WriteString("token", Token);
        w.WriteString("deadline", Deadline);
        w.WriteStartArray("sources");
        foreach (var s in Sources) {
            w.WriteStartObject();
            w.WriteString("source_id", s.SourceId);
            w.WriteString("kind", s.Kind);
            w.WriteBoolean("available", s.Available);
            w.WriteNumber("first_revision", s.FirstRevision);
            w.WriteNumber("revision_cutoff", s.RevisionCutoff);
            if (s.TurnCount is { } n) w.WriteNumber("turn_count", n); else w.WriteNull("turn_count");
            w.WriteEndObject();
        }
        w.WriteEndArray();
        JudgeLedgerWriter.WriteBudgets(w, Budgets);
        w.WriteString("soft_deadline", SoftDeadline);
        w.WriteString("ledger_path", LedgerPath);
        w.WriteStartArray("seeded_pages");
        foreach (var p in SeededPages) JudgeLedgerWriter.WritePage(w, p);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    public static EvidenceRunFile Read(string path) {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var e = doc.RootElement;
        if (e.GetProperty("version").GetInt32() != CurrentVersion) throw new InvalidDataException($"run file version {e.GetProperty("version").GetInt32()} is not {CurrentVersion}");
        return new(
            e.GetProperty("eval_run_id").GetString()!, e.GetProperty("question_id").GetString()!, e.GetProperty("root_session_id").GetString()!,
            e.GetProperty("scope_version").GetString()!, e.GetProperty("token").GetString()!, e.GetProperty("deadline").GetDateTimeOffset(),
            [.. e.GetProperty("sources").EnumerateArray().Select(s => new EvidenceRunSource(
                s.GetProperty("source_id").GetString()!, s.GetProperty("kind").GetString()!, s.GetProperty("available").GetBoolean(),
                s.GetProperty("first_revision").GetInt64(), s.GetProperty("revision_cutoff").GetInt64(),
                s.GetProperty("turn_count").IsNumber ? s.GetProperty("turn_count").GetInt32() : null))],
            JudgeLedgerReader.ReadBudgets(e.GetProperty("budgets")), e.GetProperty("soft_deadline").GetDateTimeOffset(),
            e.GetProperty("ledger_path").GetString()!,
            [.. e.GetProperty("seeded_pages").EnumerateArray().Select(JudgeLedgerReader.ReadPage)]);
    }
}
