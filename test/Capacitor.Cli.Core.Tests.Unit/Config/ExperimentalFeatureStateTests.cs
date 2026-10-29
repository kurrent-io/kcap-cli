using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.Config;

public class ExperimentalFeatureStateTests {
    const string Id = "codex-app-server";

    static DaemonStatusDto Status(params ExperimentalFeatureStatusDto[]? features) => On("connected", features);

    static DaemonStatusDto On(string connection, params ExperimentalFeatureStatusDto[]? features) =>
        new(new DaemonInfoDto("d", "1", "http://x", connection, 1, 0), [], Experimental: features?.ToList());

    static ExperimentalFeatureStatusDto Feature(
            bool resolvedSaved, bool active, bool acknowledged, string? reason = null, bool? failed = null) =>
        new(Id, resolvedSaved, active, 3, acknowledged, reason, failed);

    public static IEnumerable<(bool Saved, bool Running, DaemonStatusDto? Status, string FeatureId, ExperimentalFeatureStateKind Kind, string? Reason)> Rows() {
        yield return (true, false, null, Id, ExperimentalFeatureStateKind.DaemonNotRunning, null);
        yield return (true, true, Status(null), Id, ExperimentalFeatureStateKind.Unknown, null);
        yield return (true, true, Status(Feature(true, true, true)), "other", ExperimentalFeatureStateKind.Unknown, null);
        yield return (true, true, Status(Feature(false, false, true)), Id, ExperimentalFeatureStateKind.Applying, null);
        yield return (false, true, Status(Feature(true, true, true)), Id, ExperimentalFeatureStateKind.Applying, null);
        yield return (true, true, Status(Feature(true, true, false)), Id, ExperimentalFeatureStateKind.Applying, null);
        yield return (false, true, Status(Feature(false, false, false)), Id, ExperimentalFeatureStateKind.Applying, null);
        yield return (true, true, Status(Feature(true, true, false, failed: false)), Id, ExperimentalFeatureStateKind.Applying, null);
        yield return (true, true, On("disconnected", Feature(true, true, false)), Id, ExperimentalFeatureStateKind.Applying, null);
        yield return (true, true, Status(Feature(true, true, false, failed: true)), Id, ExperimentalFeatureStateKind.AppliedLocally, null);
        yield return (false, true, On("disconnected", Feature(false, false, false, failed: true)), Id, ExperimentalFeatureStateKind.AppliedLocally, null);
        yield return (true, true, Status(Feature(true, true, true, failed: true)), Id, ExperimentalFeatureStateKind.Active, null);
        yield return (true, true, Status(Feature(true, true, true)), Id, ExperimentalFeatureStateKind.Active, null);
        yield return (true, true, Status(Feature(true, false, true, "Codex 0.1 is older")), Id, ExperimentalFeatureStateKind.OnNotActive, "Codex 0.1 is older");
        yield return (false, true, Status(Feature(false, false, true)), Id, ExperimentalFeatureStateKind.Off, null);
        yield return (true, true, On("disconnected", Feature(true, false, false, "Codex 0.1 is older")), Id, ExperimentalFeatureStateKind.OnNotActive, "Codex 0.1 is older");
    }

    [Test]
    [MethodDataSource(nameof(Rows))]
    public async Task Classify_covers_every_state(
        bool saved, bool running, DaemonStatusDto? status, string featureId, ExperimentalFeatureStateKind kind, string? reason) {
        var (actualKind, actualReason) = ExperimentalFeatureState.Classify(saved, running, status, featureId);

        await Assert.That(actualKind).IsEqualTo(kind);
        await Assert.That(actualReason).IsEqualTo(reason);
    }
}
