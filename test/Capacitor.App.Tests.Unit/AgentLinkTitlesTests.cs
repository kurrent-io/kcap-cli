using System.Reactive.Concurrency;
using Avalonia.Controls;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.Views;
using DynamicData;
using static Capacitor.App.Tests.Unit.AgentLinkLabelsTests;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.MarkdownViewHarness;

namespace Capacitor.App.Tests.Unit;

/// In chat, a link to an agent page whose label is its URL reads as the agent's title, still opening
/// the same URL; a stand-in fills in once when the title arrives.
public class AgentLinkTitlesTests {
    const string Server = "https://kcap.example";
    const string Agent = Server + "/agents/990c76dd";

    static (Window Window, MarkdownView View, SourceCache<AgentRow, string> Rows, AgentLinkLabels Labels) ShowWithLabels(string markdown) {
        var rows = new SourceCache<AgentRow, string>(r => r.Key);
        var labels = new AgentLinkLabels(Server, rows.AsObservableCache(), ImmediateScheduler.Instance);
        var (window, view, _) = Show(markdown);
        MarkdownView.SetAgentLinks(window, labels);
        Settle(window);
        return (window, view, rows, labels);
    }

    static void Settle(Window window) {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    static List<(string Label, string Target)> LinksIn(MarkdownView view) =>
        AllLinks(view).Select(l => (l.Link.Inlines.Text ?? "", l.Link.NavigateUri!.AbsoluteUri)).ToList();

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments("Started it: " + Agent + ". Check it.")]
    [Arguments("Started it: [" + Agent + "](" + Agent + ").")]
    [Arguments("Started it: <" + Agent + ">.")]
    public async Task A_link_labelled_with_its_url_reads_as_the_title(string markdown) {
        await RunOnUiAsync(async () => {
            var (window, view, rows, labels) = ShowWithLabels(markdown);
            using var _ = labels;
            try {
                rows.AddOrUpdate(Row(AgentOrigin.Remote, "990c76dd", "Build the deep-link endpoint"));
                view.Text = markdown + " ";
                Settle(window);

                await Assert.That(LinksIn(view)).IsEquivalentTo(new[] { ("Build the deep-link endpoint", Agent) });
            } finally {
                window.Close();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_label_the_writer_chose_stays() {
        await RunOnUiAsync(async () => {
            var (window, view, rows, labels) = ShowWithLabels($"See [the agent]({Agent}).");
            using var _ = labels;
            try {
                rows.AddOrUpdate(Row(AgentOrigin.Remote, "990c76dd", "Build the deep-link endpoint"));
                Settle(window);

                await Assert.That(LinksIn(view)).IsEquivalentTo(new[] { ("the agent", Agent) });
            } finally {
                window.Close();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unknown_agent_reads_as_its_short_id_until_its_title_arrives() {
        await RunOnUiAsync(async () => {
            var (window, view, rows, labels) = ShowWithLabels($"Started {Agent}");
            using var _ = labels;
            try {
                await Assert.That(LinksIn(view)).IsEquivalentTo(new[] { ("agent 990c76dd", Agent) });

                rows.AddOrUpdate(Row(AgentOrigin.Remote, "990c76dd", "Build the deep-link endpoint"));
                Settle(window);
                await Assert.That(LinksIn(view)).IsEquivalentTo(new[] { ("Build the deep-link endpoint", Agent) });

                rows.AddOrUpdate(Row(AgentOrigin.Remote, "990c76dd", "Renamed"));
                Settle(window);
                await Assert.That(LinksIn(view)).IsEquivalentTo(new[] { ("Build the deep-link endpoint", Agent) });
            } finally {
                window.Close();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_title_that_arrived_while_detached_shows_on_reattach() {
        await RunOnUiAsync(async () => {
            var (window, view, rows, labels) = ShowWithLabels($"Started {Agent}");
            using var _ = labels;
            try {
                window.Content = null;
                Settle(window);
                rows.AddOrUpdate(Row(AgentOrigin.Remote, "990c76dd", "Build the deep-link endpoint"));
                window.Content = view;
                Settle(window);

                await Assert.That(LinksIn(view)).IsEquivalentTo(new[] { ("Build the deep-link endpoint", Agent) });
            } finally {
                window.Close();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments("Started " + Agent)]
    [Arguments("No agent here, just https://elsewhere.example/page")]
    public async Task Reattaching_a_view_whose_links_still_read_the_same_does_not_redraw_it(string markdown) {
        await RunOnUiAsync(async () => {
            var (window, view, rows, labels) = ShowWithLabels(markdown);
            using var _ = labels;
            try {
                rows.AddOrUpdate(Row(AgentOrigin.Remote, "990c76dd", "Build the deep-link endpoint"));
                Settle(window);
                var drawn = AllLinks(view).First().Block;

                window.Content = null;
                Settle(window);
                window.Content = view;
                Settle(window);

                await Assert.That(AllLinks(view).First().Block).IsSameReferenceAs(drawn);
            } finally {
                window.Close();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_link_elsewhere_keeps_its_url() {
        await RunOnUiAsync(async () => {
            const string other = "https://elsewhere.example/agents/990c76dd";
            var (window, view, _, labels) = ShowWithLabels($"See {other}");
            using var __ = labels;
            try {
                await Assert.That(LinksIn(view)).IsEquivalentTo(new[] { (other, other) });
            } finally {
                window.Close();
            }
        });
    }
}
