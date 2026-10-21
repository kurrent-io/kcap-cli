using Capacitor.Cli.Core.PullRequests;

namespace Capacitor.App.ViewModels;

/// What a session's PR card last showed: its links, and the overview while its access lasts.
public sealed record PullRequestSnapshot(IReadOnlyList<PullRequestLinkDto> Links, PullRequestRead<PullRequestOverviewDto>? Overview);
