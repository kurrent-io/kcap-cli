using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>What <c>kcap local status</c> prints: a one-line verdict, the facts behind it, and what to do.</summary>
/// <param name="Ready">The installation is this root's, running, and answers as committed.</param>
sealed record LocalStatusReport(bool Ready, string Headline, IReadOnlyList<KeyValuePair<string, string>> Fields, IReadOnlyList<string> Notes) {
    /// <summary>The verdict as a client acts on it: whether a start could bring the server up, and if not, why.</summary>
    public LocalServerReading Reading { get; init; } = new(LocalServerCondition.Installed);
}
