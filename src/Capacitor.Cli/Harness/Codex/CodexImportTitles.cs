using System.Collections.Frozen;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.Titles;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Harness.Titles;

namespace Capacitor.Cli.Harness.Codex;

/// <summary>Posts the names Codex keeps in its shared session index, for one import. The index only grows, so it is
/// read once for the whole run rather than rescanned per session.</summary>
internal sealed class CodexImportTitles(string codexHome, TimeProvider time) {
    readonly Lazy<FrozenDictionary<string, StoreTitle>> _index = new(() => CodexSessionIndexTitle.ReadAll(codexHome));

    /// <summary>True only when the server took the name through the harness-title route, where it outranks a
    /// generated title; an older server's fallback route only fills an untitled session, so a generated title is
    /// still owed there. False, sending nothing, when the index does not name the session.</summary>
    public async Task<bool> PostAsync(
            HttpClient                  client,
            string                      baseUrl,
            string                      sessionId,
            IProgress<ImportProgress>?  progress,
            CancellationToken           ct
        ) {
        if (!_index.Value.TryGetValue(ImportCommand.NormalizeGuid(sessionId), out var title)) return false;

        var outcome = await ImportHarnessTitle.PostAsync(
            client, time, baseUrl, sessionId, new HarnessTitlePost(title.Title, title.Kind, title.RecordedChangeAt), progress, ct);

        return outcome is HarnessTitleOutcome.Posted;
    }
}
