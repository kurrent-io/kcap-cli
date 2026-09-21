namespace Capacitor.App.GitHubHtml;

sealed class HtmlBlockPlan {
    public bool Rejected { get; set; }

    public List<HtmlBlockPart> Parts { get; } = [];

    /// Every structural tag of the block in order, open and close alike. Recorded even when the
    /// block is rejected: matching reads every block, so a rejected one cannot change who pairs.
    public List<HtmlToken> StructuralTags { get; } = [];
}
