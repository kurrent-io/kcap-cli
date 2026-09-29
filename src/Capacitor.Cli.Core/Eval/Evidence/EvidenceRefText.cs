using System.Globalization;
using System.Text;

namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>The server's evidence ref grammar: <c>{source}@{rev}</c>, <c>{source}@{from}-{to}</c>, <c>{source}#g{gen}t{index}</c>,
/// where the source id is a percent-encoded session or subagent stream name. A malformed string is a false, never an exception.
/// For a turn, <see cref="A"/> is the generation and <see cref="B"/> the index; for an event, <see cref="B"/> equals <see cref="A"/>.</summary>
public readonly record struct EvidenceRefText(string SourceId, EvidenceRefForm Form, long A, long B) {
    const string RootPrefix     = "AgentSession-";
    const string SubagentPrefix = "AgentSubsession-";

    public override string ToString() => Form switch {
        EvidenceRefForm.Event => $"{SourceId}@{A}",
        EvidenceRefForm.Range => $"{SourceId}@{A}-{B}",
        _                     => $"{SourceId}#g{A}t{B}"
    };

    public static bool TryParse(string? text, out EvidenceRefText reference) {
        reference = default;
        if (string.IsNullOrEmpty(text)) return false;
        var at   = text.IndexOf('@');
        var hash = text.IndexOf('#');
        var cut  = (at, hash) switch { (-1, -1) => -1, (-1, _) => hash, (_, -1) => at, _ => Math.Min(at, hash) };
        if (cut <= 0) return false;
        var source = text[..cut];
        if (!TryDecodeSource(source, out _)) return false;
        var rest = text[(cut + 1)..];

        if (text[cut] == '@') {
            var dash = rest.IndexOf('-');
            if (dash < 0) {
                if (!TryNumber(rest, out var rev)) return false;
                reference = new(source, EvidenceRefForm.Event, rev, rev);
                return true;
            }
            if (!TryNumber(rest[..dash], out var from) || !TryNumber(rest[(dash + 1)..], out var to) || from > to) return false;
            reference = new(source, EvidenceRefForm.Range, from, to);
            return true;
        }

        if (rest.Length < 4 || rest[0] != 'g') return false;
        var t = rest.IndexOf('t');
        if (t < 2) return false;
        if (!TryNumber(rest[1..t], out var generation) || !TryNumber(rest[(t + 1)..], out var index) || index > int.MaxValue) return false;
        reference = new(source, EvidenceRefForm.Turn, generation, index);
        return true;
    }

    /// <summary>Percent-decodes a source id and accepts it only when it names a session or subagent stream.</summary>
    public static bool TryDecodeSource(string sourceId, out string streamName) {
        streamName = "";
        var bytes = new List<byte>(sourceId.Length);
        for (var i = 0; i < sourceId.Length; i++) {
            var c = sourceId[i];
            if (c != '%') { if (c > 0x7F) return false; bytes.Add((byte)c); continue; }
            if (i + 2 >= sourceId.Length || !byte.TryParse(sourceId.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)) return false;
            bytes.Add(b);
            i += 2;
        }
        string decoded;
        try { decoded = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes.ToArray()); }
        catch (DecoderFallbackException) { return false; }
        if (!decoded.StartsWith(RootPrefix, StringComparison.Ordinal) && !decoded.StartsWith(SubagentPrefix, StringComparison.Ordinal)) return false;
        streamName = decoded;
        return true;
    }

    static bool TryNumber(string s, out long value) {
        value = 0;
        if (s.Length == 0 || (s.Length > 1 && s[0] == '0') || !s.All(char.IsAsciiDigit)) return false;
        return long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
