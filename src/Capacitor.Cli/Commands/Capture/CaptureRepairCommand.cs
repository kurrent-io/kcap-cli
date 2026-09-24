using System.Text;
using System.Text.Json;
using Capacitor.Cli.Capture;
using Capacitor.Cli.Commands.Capture.Wire;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands.Capture;

internal sealed class CaptureRepairCommand(ProfileContext profiles, ICapacitorHttpClient http, TimeProvider time) {
    public async Task<int> HandleAsync(string sessionId, bool dryRun, IReadOnlyList<IImportSource> sources, CancellationToken ct = default) {
        string? repairId = null;
        var completionRequested = false;
        try {
            sessionId = sessionId.Replace("-", "", StringComparison.Ordinal);
            if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 128 || sessionId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
                throw new IOException("Capture recovery requires one valid --session ID.");
            using var connection = await http.ForCommandAsync(ct);
            var url = (profiles.Resolution.ServerUrl ?? throw new IOException("No server configured.")).TrimEnd('/') + $"/api/sessions/{sessionId}/capture-repairs";
            var client = new CaptureRepairClient(connection, url, time);
            await client.RequireCapabilityAsync(ct);
            var candidates = new List<DiscoveredSession>();
            foreach (var source in sources.Where(s => s.IsAvailable && s.Vendor is HarnessId.Claude or HarnessId.Codex))
                candidates.AddRange(await source.DiscoverAsync(new(null, sessionId, null, 0), ct));
            candidates = candidates.Where(s => s.SessionId.Replace("-", "", StringComparison.Ordinal) == sessionId).ToList();
            if (candidates.Count != 1) throw new IOException(candidates.Count == 0
                ? "No local Claude or Codex root transcript found for this session. Recovery supports only those vendors."
                : "Multiple local transcripts match this session; select --claude or --codex to disambiguate.");
            var root = candidates[0];
            var rootPath = DiscoveredSessionFile.PathOf(root) ?? throw new IOException("The selected recovery source has no transcript file.");
            var vendor = root.Vendor == HarnessId.Claude ? "claude" : "codex";
            var files = new List<(CaptureRepairSourceRequest Source, CaptureRepairFileSnapshot File)> {
                (new(sessionId, null, vendor), CaptureRepairFileSnapshot.Take(rootPath))
            };
            if (root.Vendor == HarnessId.Claude) {
                foreach (var child in SessionImporter.DiscoverAgentTranscripts(rootPath).OrderBy(c => c.AgentId, StringComparer.Ordinal))
                    files.Add((new(sessionId, child.AgentId, vendor), CaptureRepairFileSnapshot.Take(child.Path)));
            } else {
                foreach (var child in CodexSubagentDiscovery.EnumerateDescendantRollouts(rootPath, sessionId))
                    files.Add((new(sessionId, child.ChildDashlessId, vendor), CaptureRepairFileSnapshot.Take(child.FilePath)));
            }
            if (files.Count > 128) throw new IOException("Capture recovery supports at most 128 sources per operation.");
            repairId = await client.PrepareAsync(files.Select(f => f.Source).ToArray(), dryRun, ct);
            Console.WriteLine($"Capture recovery {repairId}: scanning {files.Count} sources{(dryRun ? " (dry run)" : "")}.");
            var ends = new List<CaptureRepairSourceEndRequest>();
            var losses = new int[5];
            foreach (var (source, file) in files) {
                file.AssertUnchanged();
                var last = await CaptureRepairSourceReader.ReadAsync(file.Path, (line, token) => client.AddLineAsync(source, line, token), reason => losses[(int)reason]++, ct);
                ends.Add(await client.EndSourceAsync(source, last, ct));
            }
            foreach (var (_, file) in files) file.AssertUnchanged();
            for (var i = 0; i < losses.Length; i++)
                if (losses[i] > 0) Console.WriteLine($"Scan redaction loss: {CaptureLossMarker.ReasonName((RedactionLossReason)i)} = {losses[i]} records.");
            completionRequested = true;
            var report = await client.CompleteAsync(ends.ToArray(), ct);
            var deadline = time.GetUtcNow() + TimeSpan.FromMinutes(2);
            while (!dryRun && report.Phase is not ("complete" or "paused" or "cancelled") && time.GetUtcNow() < deadline) {
                report = await client.StatusAsync(ct);
                if (report.Phase is not ("complete" or "paused" or "cancelled"))
                    await Task.Delay(TimeSpan.FromSeconds(1), time, ct);
            }
            Console.WriteLine($"{(dryRun ? report.CandidateRecords : report.RestoredRecords)} {(dryRun ? "candidate" : "restored")} records; {report.RemainingGapCount} remaining gaps; phase {report.Phase}.");
            foreach (var gap in report.Gaps.GroupBy(g => g.Code).OrderBy(g => g.Key, StringComparer.Ordinal))
                Console.WriteLine($"  {gap.Key}: {gap.Count()} reported gaps");
            if (dryRun && report.Phase == "preview" || !dryRun && report.Phase == "complete" && report.AccountingCurrent)
                return report.RemainingGapCount == 0 ? 0 : 2;
            Console.Error.WriteLine($"Repair {repairId} is not complete. Check the session's capture status; durable validated work continues on the server.");
            return 1;
        } catch (Exception ex) when (ex is IOException or DecoderFallbackException or HttpRequestException or JsonException or OperationCanceledException or UnauthorizedAccessException) {
            Console.Error.WriteLine(ex switch {
                OperationCanceledException => "Capture recovery interrupted.",
                DecoderFallbackException => "Recovery source is not valid UTF-8; no repair was validated.",
                _ => ex.Message
            });
            if (repairId is not null) Console.Error.WriteLine(completionRequested && !dryRun
                ? $"Repair {repairId} may be durable and continuing on the server; check the session's capture status."
                : $"Repair {repairId} was not validated; temporary staging will expire.");
            return 1;
        }
    }
}
