namespace Capacitor.App.ViewModels;

/// Splits a server snippet on the U+0002 / U+0003 match markers; the markers never reach a run.
public static class SearchSnippet {
    const char Start = '\u0002';
    const char End = '\u0003';

    public static IReadOnlyList<SnippetRun> Parse(string? snippet) {
        var runs = new List<SnippetRun>();
        if (string.IsNullOrEmpty(snippet)) return runs;

        var text = new System.Text.StringBuilder();
        var matched = false;
        foreach (var c in snippet) {
            if (c == Start && !matched) { Flush(); matched = true; }
            else if (c == End && matched) { Flush(); matched = false; }
            else if (c is Start or End) { }
            else text.Append(c);
        }
        Flush();
        return runs;

        void Flush() {
            if (text.Length == 0) return;
            if (runs.Count > 0 && runs[^1].Matched == matched) runs[^1] = runs[^1] with { Text = runs[^1].Text + text };
            else runs.Add(new SnippetRun(text.ToString(), matched));
            text.Clear();
        }
    }
}
