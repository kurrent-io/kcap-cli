using System.Text.Json;

namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>Appends ledger lines, one flushed JSON object per line, so a process killed mid-question leaves every line it
/// finished.</summary>
public sealed class JudgeLedgerWriter : IDisposable {
    readonly FileStream _stream;

    JudgeLedgerWriter(FileStream stream) => _stream = stream;

    public static JudgeLedgerWriter Create(string path, JudgeLedgerHeader header) {
        var writer = new JudgeLedgerWriter(OwnerOnlyFile.CreateNew(path));
        writer.Line(w => {
            w.WriteString("kind", "header");
            w.WriteNumber("version", JudgeLedgerHeader.CurrentVersion);
            w.WriteString("eval_run_id", header.EvalRunId);
            w.WriteString("question_id", header.QuestionId);
            w.WriteString("scope_version", header.ScopeVersion);
            WriteBudgets(w, header.Budgets);
            if (header.SoftDeadline is { } soft) w.WriteString("soft_deadline", soft); else w.WriteNull("soft_deadline");
            w.WriteString("started_at", header.StartedAt);
        });
        return writer;
    }

    public static JudgeLedgerWriter OpenAppend(string path) =>
        new(new FileStream(path, new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read }));

    public void Append(JudgeLedgerPage page) => Line(w => WritePageFields(w, page));

    public void Append(JudgeLedgerCall call) => Line(w => {
        w.WriteString("kind", "call");
        w.WriteNumber("seq", call.Seq);
        w.WriteString("tool", call.Tool);
        w.WritePropertyName("args"); w.WriteRawValue(call.ArgsJson);
        w.WriteString("outcome", call.Outcome);
        WriteNullable(w, "stop_reason", call.StopReason);
        WriteNullable(w, "error", call.Error);
        w.WriteNumber("bytes", call.Bytes);
    });

    public void Append(JudgeLedgerFooter footer) => Line(w => {
        w.WriteString("kind", "footer");
        w.WriteNumber("tool_calls", footer.ToolCalls);
        w.WriteNumber("delivered_bytes", footer.DeliveredBytes);
        WriteNullable(w, "stop_reason", footer.StopReason);
        w.WriteStartArray("sources_refused");
        foreach (var s in footer.SourcesRefused) w.WriteStringValue(s);
        w.WriteEndArray();
        w.WriteString("ended_at", footer.EndedAt);
    });

    /// <summary>A page as a complete JSON object, for the run file's seeded pages.</summary>
    public static void WritePage(Utf8JsonWriter w, JudgeLedgerPage page) {
        w.WriteStartObject();
        WritePageFields(w, page);
        w.WriteEndObject();
    }

    static void WritePageFields(Utf8JsonWriter w, JudgeLedgerPage p) {
        w.WriteString("kind", "page");
        w.WriteNumber("seq", p.Seq);
        w.WriteString("handle", p.Handle);
        w.WriteString("tool", p.Tool);
        w.WritePropertyName("args"); w.WriteRawValue(p.ArgsJson);
        WriteNullable(w, "source", p.Source);
        w.WriteNumber("bytes", p.Bytes);
        w.WriteString("text", p.Text);
        w.WriteStartArray("revisions");
        foreach (var (s, from, to) in p.Revisions) { w.WriteStartArray(); w.WriteStringValue(s); w.WriteNumberValue(from); w.WriteNumberValue(to); w.WriteEndArray(); }
        w.WriteEndArray();
        w.WriteStartArray("turns");
        foreach (var (s, index) in p.Turns) { w.WriteStartArray(); w.WriteStringValue(s); w.WriteNumberValue(index); w.WriteEndArray(); }
        w.WriteEndArray();
        w.WriteStartArray("bodies");
        foreach (var (r, field, ordinal) in p.Bodies) {
            w.WriteStartArray(); w.WriteStringValue(r); w.WriteStringValue(field);
            if (ordinal is { } o) w.WriteNumberValue(o); else w.WriteNullValue();
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WriteStartArray("detail");
        foreach (var (s, rev, offset, length) in p.Detail) { w.WriteStartArray(); w.WriteStringValue(s); w.WriteNumberValue(rev); w.WriteNumberValue(offset); w.WriteNumberValue(length); w.WriteEndArray(); }
        w.WriteEndArray();
        w.WriteStartObject("cites");
        foreach (var (handle, reference) in p.Cites) w.WriteString(handle, reference);
        w.WriteEndObject();
        w.WriteBoolean("has_next", p.HasNext);
        WriteNullable(w, "next", p.Next);
    }

    internal static void WriteBudgets(Utf8JsonWriter w, EvidenceRunBudgets b) {
        w.WriteStartObject("budgets");
        w.WriteNumber("max_tool_calls", b.MaxToolCalls);
        w.WriteNumber("judge_byte_budget_bytes", b.JudgeByteBudgetBytes);
        w.WriteNumber("page_budget_bytes", b.PageBudgetBytes);
        w.WriteEndObject();
    }

    static void WriteNullable(Utf8JsonWriter w, string name, string? value) {
        if (value is null) w.WriteNull(name); else w.WriteString(name, value);
    }

    void Line(Action<Utf8JsonWriter> body) {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer)) { w.WriteStartObject(); body(w); w.WriteEndObject(); }
        buffer.WriteByte((byte)'\n');
        _stream.Write(buffer.GetBuffer(), 0, (int)buffer.Length);
        _stream.Flush();
    }

    public void Dispose() => _stream.Dispose();
}
