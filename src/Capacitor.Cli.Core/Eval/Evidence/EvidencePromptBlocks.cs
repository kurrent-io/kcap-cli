using System.Text;

namespace Capacitor.Cli.Core.Eval.Evidence;

public static class EvidencePromptBlocks {
    public const string TasksReadFromEvidence = "Tasks: not supplied separately. Any plan or task list the session declared is part of the evidence; read it there.";

    public const string QuestionTemplateResource      = "prompt-eval-question-evidence.txt";
    public const string OneShotPreambleResource       = "preamble-eval-oneshot.txt";
    public const string RetrospectivePreambleResource = "preamble-eval-retrospective.txt";

    /// <summary>A preamble as prepended to an already rendered prompt: its text and one blank line.</summary>
    public static string Preamble(string resource) => EmbeddedResources.Load(resource).TrimEnd() + "\n\n";

    /// <summary>Replaces each <c>{NAME}</c> key found in <paramref name="template"/> in one pass, so text inserted for one key —
    /// session content included — is never scanned for another. Unknown braces are kept.</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> values) {
        var output = new StringBuilder(template.Length);
        var i = 0;
        while (i < template.Length) {
            var open = template.IndexOf('{', i);
            var close = open < 0 ? -1 : template.IndexOf('}', open + 1);
            if (close < 0) { output.Append(template, i, template.Length - i); break; }
            if (values.TryGetValue(template.Substring(open, close - open + 1), out var value)) {
                output.Append(template, i, open - i).Append(value);
                i = close + 1;
            } else {
                output.Append(template, i, open + 1 - i);
                i = open + 1;
            }
        }
        return output.ToString();
    }
}
