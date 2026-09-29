using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// The snapshot endpoint <c>kcap skills sync</c> fetches through: it answers from a script and
/// records what it was asked, so a test can pin that a stale etag was discarded rather than resent.
/// The other two members throw — a sync reaching one is asking for something this suite does not
/// model.
/// </summary>
sealed class StubSkillsApi(Func<string, string?, string?, SkillsSnapshotResult> answer) : IRepositoriesApi {
    public List<(string RepoHash, string? Vendor, string? Etag)> Requests { get; } = [];

    /// <summary>Answers one snapshot, under <paramref name="etag"/>, to every request.</summary>
    public static StubSkillsApi Serving(string etag, params SkillSnapshotItem[] skills) =>
        new((_, _, _) => new SkillsSnapshotResult.Found(
            new SkillsSnapshotResponse { Etag = etag, Skills = skills }));

    public static StubSkillsApi Unchanged() => new((_, _, _) => new SkillsSnapshotResult.NotModified());

    public static StubSkillsApi Refusing(string message) =>
        new((_, _, _) => throw new CapacitorApiException(401, message));

    public Task<SkillsSnapshotResult> GetSkillsSnapshotAsync(
            string repoHash, string? vendor, string? etag, CancellationToken ct = default) {
        Requests.Add((repoHash, vendor, etag));

        return Task.FromResult(answer(repoHash, vendor, etag));
    }

    public Task<List<RepoRecapEntry>> GetRecapsAsync(string repoHash, int limit, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<CurationResult> GetPromotedCurationAsync(string repoHash, int limit, CancellationToken ct = default) =>
        throw new NotSupportedException();
}
