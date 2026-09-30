using Capacitor.Cli.Core.Harness.Titles;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Harness.Kiro;

/// <summary>Kiro's per-session <c>{id}.json</c> sidecar carries its own auto-generated <c>title</c> — the same
/// file <see cref="KiroImportSource"/> reads for import. Kiro names every session on its own; there is no
/// user-rename signal to distinguish, so a title here always reads as <see cref="HarnessTitleKind.Auto"/>.</summary>
public sealed class KiroSessionTitle(string sessionJsonPath) : IHarnessTitleStore {
    public bool RecordsChangeTime => false;

    public StoreTitle? Read() {
        var meta = KiroSessionMeta.TryRead(sessionJsonPath);

        return string.IsNullOrWhiteSpace(meta?.Title)
            ? null
            : new StoreTitle(meta.Title.Trim(), HarnessTitleKind.Auto, null);
    }
}
