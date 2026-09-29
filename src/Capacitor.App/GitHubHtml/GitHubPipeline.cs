using Markdig;
using Markdig.Extensions.Alerts;
using MarkView.Avalonia;

namespace Capacitor.App.GitHubHtml;

public static class GitHubPipeline {
    /// One pipeline for every reader view: a pipeline is immutable once built.
    public static MarkdownPipeline Instance { get; } = Configure(new MarkdownPipelineBuilder()).Use(new GitHubHtmlExtension()).Build();

    /// What GitHub-flavoured markdown parses as before the HTML pass runs.
    public static MarkdownPipelineBuilder Configure(MarkdownPipelineBuilder builder) =>
        builder.UseSupportedExtensions().Use<AlertExtension>();
}
