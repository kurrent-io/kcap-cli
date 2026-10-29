using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// A listing page's outcome. Rows is null on any failure, Unauthorized included, so a caller that
/// only wants data ignores the flag; Total counts the whole scope the page was cut from. RetryAfter
/// is set when the server refused the read for the caller's read budget.
public sealed record ServerSessionsFetch(RepoSessionDto[]? Rows, int Total, TimeSpan? RetryAfter = null, bool Unauthorized = false);
