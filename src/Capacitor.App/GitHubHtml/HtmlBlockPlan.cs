namespace Capacitor.App.GitHubHtml;

sealed class HtmlBlockPlan {
    public bool Rejected { get; set; }

    public List<HtmlBlockPart> Parts { get; } = [];

    /// Every `details` tag of the block in order, true for an open tag. Complete even when the
    /// block is rejected: matching reads every block, so a rejected one cannot change who pairs.
    public List<bool> DetailsTags { get; } = [];
}
