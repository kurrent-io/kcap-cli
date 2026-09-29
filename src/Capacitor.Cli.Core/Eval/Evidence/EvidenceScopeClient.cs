using System.Text.Json;

namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>Resolves and renews one run's viewer scope. The deadline is the client's clock at the request plus the
/// server's stated lifetime, so a skewed clock cannot make it later than the server's own expiry.</summary>
public sealed class EvidenceScopeClient(HttpClient http, string baseUrl, string sessionId, TimeProvider time) {
    public const int    MaxManifestPages = 10;
    public const string SingleQuery      = "continuations=false&delegates=true&adopted_children=false";

    public static readonly TimeSpan ArtifactLifetime           = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan PhaseSetupMargin           = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PostQuestionBudget         = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RpcMargin                  = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RetrospectiveExcerptBudget = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DrainPersistBudget         = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PreDrainHeadroom           = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan CertificationHeadroom      = EvidenceCitationClient.CertificationBudget + RpcMargin;
    public static readonly TimeSpan RetrospectiveHeadroom      = RetrospectiveExcerptBudget + EvalService.RetrospectiveTimeout + DrainPersistBudget;

    /// <summary>Everything a question runs before the next renewal: setup, the harness, parsing and coverage, and certification.</summary>
    public static TimeSpan QuestionHeadroom(EvidenceRoute route) =>
        PhaseSetupMargin + (route == EvidenceRoute.OneShot ? EvalService.OneShotQuestionTimeout : EvalService.ToolsPerQuestionTimeout)
      + PostQuestionBudget + EvidenceCitationClient.CertificationBudget;

    public EvidenceScopeState? State     { get; private set; }
    public int                 Refreshes { get; private set; }
    public int                 Reopens   { get; private set; }
    public string?             LastError { get; private set; }

    public async Task<EvidenceScopeStatus> ResolveAsync(CancellationToken ct) {
        var (status, fresh) = await FetchAllPagesAsync(ct);
        if (status == EvidenceScopeStatus.Ok) State = fresh;
        return status;
    }

    public async Task<EvidenceScopeStatus> ReopenAsync(CancellationToken ct) {
        var state = State ?? throw new InvalidOperationException("the scope has not been resolved");
        Reopens++;
        var (status, page) = await GetPageAsync(Url($"cursor={Uri.EscapeDataString(state.Token)}"), ct);
        return status switch {
            EvidenceScopeStatus.Ok when page!.ScopeVersion == state.ScopeVersion => EvidenceScopeStatus.Ok,
            EvidenceScopeStatus.Ok or EvidenceScopeStatus.NotVisible or EvidenceScopeStatus.Moved => EvidenceScopeStatus.Moved,
            _ => status
        };
    }

    /// <summary>Re-checks admission, then renews the artifact when it would not outlive <paramref name="headroom"/>. A
    /// renewal onto another scope version is <see cref="EvidenceScopeStatus.Moved"/>: the run ends rather than mixing versions.</summary>
    public async Task<EvidenceScopeStatus> EnsureScopeAsync(TimeSpan headroom, CancellationToken ct) {
        var reopened = await ReopenAsync(ct);
        if (reopened != EvidenceScopeStatus.Ok) return reopened;

        var state = State!;
        if (state.Deadline - time.GetUtcNow() > headroom) return EvidenceScopeStatus.Ok;

        Refreshes++;
        var (status, fresh) = await FetchAllPagesAsync(ct);
        if (status is EvidenceScopeStatus.NotVisible or EvidenceScopeStatus.Moved) return EvidenceScopeStatus.Moved;
        if (status != EvidenceScopeStatus.Ok) return status;
        if (fresh!.ScopeVersion != state.ScopeVersion) return EvidenceScopeStatus.Moved;
        State = state with { Token = fresh.Token, Deadline = fresh.Deadline, ServerExpiresAt = fresh.ServerExpiresAt };
        return EvidenceScopeStatus.Ok;
    }

    string Url(string query) => $"{baseUrl.TrimEnd('/')}/api/sessions/{Uri.EscapeDataString(sessionId)}/evidence-scope?{query}";

    async Task<(EvidenceScopeStatus, EvidenceScopeState?)> FetchAllPagesAsync(CancellationToken ct) {
        var sentAt = time.GetUtcNow();
        var (status, first) = await GetPageAsync(Url(SingleQuery), ct);
        if (status != EvidenceScopeStatus.Ok) return (status, null);

        var sources = new List<EvidenceSourceDto>(first!.Sources);
        var next    = first.NextCursor;
        for (var pages = 1; next is not null; pages++) {
            if (pages >= MaxManifestPages) { LastError = $"the evidence scope manifest exceeded {MaxManifestPages} pages"; return (EvidenceScopeStatus.Failed, null); }
            var (pageStatus, page) = await GetPageAsync(Url($"cursor={Uri.EscapeDataString(next)}"), ct);
            if (pageStatus != EvidenceScopeStatus.Ok) return (pageStatus, null);
            if (page!.ScopeVersion != first.ScopeVersion) return (EvidenceScopeStatus.Moved, null);
            sources.AddRange(page.Sources);
            next = page.NextCursor;
        }

        var lifetime = first.IssuedAt is { } issued && first.ExpiresAt is { } expires ? expires - issued : ArtifactLifetime;
        return (EvidenceScopeStatus.Ok, new(first.ScopeVersion, first.RootSessionId, first.Complete, first.IncompleteReasons, sources, first.Token, sentAt + lifetime, first.ExpiresAt));
    }

    async Task<(EvidenceScopeStatus, EvidenceScopeManifestDto?)> GetPageAsync(string url, CancellationToken ct) {
        try {
            using var resp = await http.GetAsync(url, ct);
            switch ((int)resp.StatusCode) {
                case 401: LastError = "authentication failed — run 'kcap login' to re-authenticate"; return (EvidenceScopeStatus.Unauthorized, null);
                case 404: return (EvidenceScopeStatus.NotVisible, null);
                case 409: return (EvidenceScopeStatus.Moved, null);
            }
            if (!resp.IsSuccessStatusCode) { LastError = $"failed to resolve the evidence scope: HTTP {(int)resp.StatusCode}"; return (EvidenceScopeStatus.Failed, null); }
            var page = JsonSerializer.Deserialize(await resp.Content.ReadAsStringAsync(ct), CapacitorJsonContext.Default.EvidenceScopeManifestDto);
            if (page is null) { LastError = "the evidence scope response was empty"; return (EvidenceScopeStatus.Failed, null); }
            return (EvidenceScopeStatus.Ok, page);
        } catch (HttpRequestException e) {
            LastError = $"server unreachable: {e.Message}";
            return (EvidenceScopeStatus.Failed, null);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            LastError = "the evidence scope request timed out";
            return (EvidenceScopeStatus.Failed, null);
        } catch (JsonException e) {
            LastError = $"the evidence scope response was not valid JSON: {e.Message}";
            return (EvidenceScopeStatus.Failed, null);
        }
    }
}
