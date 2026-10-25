using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// A listing page's outcome. Rows is null on any failure, Unauthorized included, so a caller that
/// only wants data ignores the flag; Total counts the whole scope the page was cut from.
public sealed record ServerSessionsFetch(RepoSessionDto[]? Rows, int Total, bool Unauthorized = false);
