using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.Views;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

/// The search page as drawn: snippet runs, and the signed-out prompt in place of the list.
public class SessionSearchViewSmokeTests {
    static void OpenWithShortcut(SearchWindowHost host) {
        host.Window.KeyPressQwerty(PhysicalKey.K, RawInputModifiers.Meta);
        host.Settle();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Matched_snippet_runs_render_bold_without_markers() => AvaloniaSession.RunOnUiAsync(async () => {
        using var host = new SearchWindowHost();
        host.Response = new SearchResponseDto {
            Sessions = [new() {
                SessionId = "S1", Slug = "fix-auth", Title = "Fix auth flow", StartedAt = "2026-10-29T09:00:00Z", EndedAt = "2026-10-29T10:00:00Z",
                MatchedField = "title", Snippet = "Fix \u0002auth\u0003 flow", PrNumber = 42,
            }],
        };
        OpenWithShortcut(host);

        host.Window.KeyTextInput("auth");
        host.SettleSearch();

        var title = host.Named<SessionSearchView>("SearchCover").GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Name == "SearchHitTitle");
        var runs = (title.Inlines ?? []).OfType<Run>().ToArray();
        var text = string.Concat(runs.Select(r => r.Text));
        var matched = runs.Single(r => r.Text == "auth");
        var textBrush = (IBrush)Application.Current!.FindResource("KcapTextBrush")!;
        var rendered = string.Join('\n', host.Named<SessionSearchView>("SearchCover").GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Inlines is { Count: > 0 } inlines ? string.Concat(inlines.OfType<Run>().Select(r => r.Text)) : t.Text ?? ""));

        var chip = host.Named<SessionSearchView>("SearchCover").GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SearchHitPrChip");
        var gap = chip.TranslatePoint(default, title)!.Value.X - title.Bounds.Width;

        await Assert.That(text).IsEqualTo("Fix auth flow");
        await Assert.That(gap).IsLessThanOrEqualTo(6.5);
        await Assert.That(rendered).DoesNotContain('\u0002');
        await Assert.That(rendered).DoesNotContain('\u0003');
        await Assert.That(rendered).Contains("#42");
        await Assert.That((int)matched.FontWeight).IsGreaterThanOrEqualTo((int)FontWeight.SemiBold);
        await Assert.That(matched.Foreground).IsSameReferenceAs(textBrush);
        await Assert.That(runs.Where(r => r != matched).All(r => r.FontWeight < FontWeight.SemiBold)).IsTrue();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_long_title_is_trimmed_and_keeps_the_pr_chip_and_time_in_the_row() => AvaloniaSession.RunOnUiAsync(async () => {
        using var host = new SearchWindowHost();
        var title = string.Join(' ', Enumerable.Repeat("authentication", 40));
        host.Response = new SearchResponseDto {
            Sessions = [new() {
                SessionId = "S1", Slug = "fix-auth", Title = title, StartedAt = "2026-10-29T09:00:00Z", EndedAt = "2026-10-29T10:00:00Z",
                MatchedField = "title", Snippet = title, PrNumber = 42,
            }],
        };
        OpenWithShortcut(host);

        host.Window.KeyTextInput("auth");
        host.SettleSearch();

        var cover = host.Named<SessionSearchView>("SearchCover");
        var titleBlock = cover.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "SearchHitTitle");
        var chip = cover.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SearchHitPrChip");
        var row = titleBlock.FindAncestorOfType<Button>()!;
        var when = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == host.Vm.Search!.Hits[0].WhenText);
        double Right(Visual v) => v.TranslatePoint(new Point(v.Bounds.Width, 0), row)!.Value.X;
        double Left(Visual v) => v.TranslatePoint(new Point(0, 0), row)!.Value.X;
        var untrimmed = new TextBlock { Text = title, FontSize = titleBlock.FontSize };
        untrimmed.Measure(Size.Infinity);

        await Assert.That(titleBlock.Bounds.Width).IsLessThan(untrimmed.DesiredSize.Width);
        await Assert.That(chip.IsEffectivelyVisible).IsTrue();
        await Assert.That(chip.Bounds.Width).IsGreaterThan(0);
        await Assert.That(Right(titleBlock)).IsLessThanOrEqualTo(Left(chip));
        await Assert.That(Right(chip)).IsLessThanOrEqualTo(Left(when));
        await Assert.That(Right(when)).IsLessThanOrEqualTo(row.Bounds.Width);
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Signed_out_page_shows_sign_in_and_sends_no_request() => AvaloniaSession.RunOnUiAsync(async () => {
        using var host = new SearchWindowHost(signedOut: true);
        OpenWithShortcut(host);
        await Assert.That(host.Vm.SignInVisible).IsTrue();
        await Assert.That(host.Vm.SearchShowsSignIn).IsTrue();
        await Assert.That(host.Named<TextBox>("SearchQueryBox").IsReadOnly).IsTrue();

        host.Window.KeyTextInput("auth flow");
        host.SettleSearch();
        var prompt = host.Named<StackPanel>("SearchSignInPrompt");
        var promptShown = prompt.IsEffectivelyVisible;
        var listShown = host.Named<DockPanel>("SearchResults").IsVisible;
        host.Named<Button>("SearchSignInButton").Command!.Execute(null);

        await Assert.That(host.Vm.IsSearchOpen).IsTrue();
        await Assert.That(promptShown).IsTrue();
        await Assert.That(listShown).IsFalse();
        await Assert.That(host.Requests).IsEmpty();
        await Assert.That(host.SignIns).IsEqualTo(1);
    });

    /// A 401 to the page shows the prompt but leaves the query editable; signing back in asks again.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_401_shows_sign_in_then_signing_in_asks_again_and_shows_results() => AvaloniaSession.RunOnUiAsync(async () => {
        using var host = new SearchWindowHost();
        host.Response = new SearchResponseDto {
            Sessions = [new() { SessionId = "S1", Slug = "fix-auth", Title = "Fix auth", StartedAt = "2026-10-29T09:00:00Z", MatchedField = "title", Snippet = "Fix auth" }],
        };
        host.Outcome = ServerReadOutcome.Unauthorized;
        OpenWithShortcut(host);
        host.Window.KeyTextInput("auth");
        host.SettleSearch();
        var promptAfter401 = host.Named<StackPanel>("SearchSignInPrompt").IsVisible;
        var editable = !host.Named<TextBox>("SearchQueryBox").IsReadOnly;

        host.Lane.OnNext(new ServerLaneStatus(ServerLaneState.SignedOut));
        host.Settle();
        var readOnlySignedOut = host.Named<TextBox>("SearchQueryBox").IsReadOnly;
        host.Outcome = ServerReadOutcome.Ok;
        host.Lane.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
        host.Settle();
        host.SettleSearch();

        await Assert.That(promptAfter401).IsTrue();
        await Assert.That(editable).IsTrue();
        await Assert.That(readOnlySignedOut).IsTrue();
        await Assert.That(host.Requests.Count).IsEqualTo(2);
        await Assert.That(host.Named<StackPanel>("SearchSignInPrompt").IsVisible).IsFalse();
        await Assert.That(host.Named<DockPanel>("SearchResults").IsVisible).IsTrue();
        await Assert.That(host.Vm.Search!.Hits.Count).IsEqualTo(1);
    });
}
