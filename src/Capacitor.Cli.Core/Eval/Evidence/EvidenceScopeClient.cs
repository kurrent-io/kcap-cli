using System.Text;
using System.Text.Json;

namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>Resolves and renews one run's viewer scope. The deadline is the client's clock at the request plus the
/// server's stated lifetime, so a skewed clock cannot make it later than the server's own expiry. With
/// <paramref name="holds"/> the server holds the scope for the run: renewal extends that hold instead of resolving
/// again, so the session's growth cannot move the version, and disposing the client releases it.</summary>
public sealed class EvidenceScopeClient(HttpClient http, string baseUrl, string sessionId, TimeProvider time, bool holds = false) : IAsyncDisposable {
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
    public static readonly TimeSpan ReleaseTimeout             = TimeSpan.FromSeconds(10);

    /// <summary>Everything a question runs before the next renewal: setup, the harness, parsing and coverage, and certification.</summary>
    public static TimeSpan QuestionHeadroom(EvidenceRoute route) =>
        PhaseSetupMargin + (route == EvidenceRoute.OneShot ? EvalService.OneShotQuestionTimeout : EvalService.ToolsPerQuestionTimeout)
      + PostQuestionBudget + EvidenceCitationClient.CertificationBudget;

    public EvidenceScopeState? State     { get; private set; }
    public int                 Refreshes { get; private set; }
    public int                 Reopens   { get; private set; }
    public string?             LastError { get; private set; }

    /// <summary>True when the server was asked to hold the scope and refused, as it does for a scope too large to hold.</summary>
    public bool HoldRefused { get; private set; }

    // The latest token of a hold this client took, owned from the first page that reported it, so a hold is released
    // even when a later page of its manifest failed.
    string? _heldToken;

    public async Task<EvidenceScopeStatus> ResolveAsync(CancellationToken ct) {
        var (status, fresh) = holds ? await FetchAllPagesAsync(TakeHoldAsync, ct) : await FetchAllPagesAsync(ct);
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
        if (state.Held) return await RenewAsync(state, ct);
        var (status, fresh) = await FetchAllPagesAsync(ct);
        if (status is EvidenceScopeStatus.NotVisible or EvidenceScopeStatus.Moved) return EvidenceScopeStatus.Moved;
        if (status != EvidenceScopeStatus.Ok) return status;
        if (fresh!.ScopeVersion != state.ScopeVersion) return EvidenceScopeStatus.Moved;
        State = state with { Token = fresh.Token, Deadline = fresh.Deadline, ServerExpiresAt = fresh.ServerExpiresAt };
        return EvidenceScopeStatus.Ok;
    }

    /// <summary>Extends the hold without resolving again; a renewal answering another version is a moved scope.</summary>
    async Task<EvidenceScopeStatus> RenewAsync(EvidenceScopeState state, CancellationToken ct) {
        var sentAt = time.GetUtcNow();
        var (status, page) = await SendPageAsync(HttpMethod.Post, Route("renewal"), HoldBody(state.Token), ct);
        if (status is EvidenceScopeStatus.NotVisible or EvidenceScopeStatus.Moved) return EvidenceScopeStatus.Moved;
        if (status != EvidenceScopeStatus.Ok) return status;
        if (page!.ScopeVersion != state.ScopeVersion) return EvidenceScopeStatus.Moved;
        _heldToken = page.Token;
        State = state with { Token = page.Token, Deadline = sentAt + Lifetime(page), ServerExpiresAt = page.ExpiresAt };
        return EvidenceScopeStatus.Ok;
    }

    /// <summary>Releases the hold this client took, if any, within <see cref="ReleaseTimeout"/>. Never throws: a release
    /// that fails for any reason leaves the hold to expire on the server, and the run's own cleanup still runs.</summary>
    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _heldToken, null) is not { } token) return;
        using var timeout = new CancellationTokenSource(ReleaseTimeout, time);
        try {
            using var request = new HttpRequestMessage(HttpMethod.Delete, Route("hold")) { Content = HoldBody(token) };
            using var resp    = await http.SendAsync(request, timeout.Token);
        } catch (Exception e) {
            LastError = $"could not release the evidence scope hold: {e.Message}";
        }
    }

    string Url(string query) => $"{baseUrl.TrimEnd('/')}/api/sessions/{Uri.EscapeDataString(sessionId)}/evidence-scope?{query}";

    string Route(string leaf) => $"{baseUrl.TrimEnd('/')}/api/sessions/{Uri.EscapeDataString(sessionId)}/evidence-scope/{leaf}";

    static StringContent HoldBody(string token) =>
        new(JsonSerializer.Serialize(new EvidenceScopeHoldRequestDto { Token = token }, CapacitorJsonContext.Default.EvidenceScopeHoldRequestDto), Encoding.UTF8, "application/json");

    // One request key per resolution, so a redelivery of this request can never take a second hold.
    Task<(EvidenceScopeStatus, EvidenceScopeManifestDto?)> TakeHoldAsync(CancellationToken ct) {
        var body = JsonSerializer.Serialize(new EvidenceScopeHoldCreateRequestDto {
            RequestId = Guid.NewGuid().ToString("N"), Continuations = false, Delegates = true, AdoptedChildren = false
        }, CapacitorJsonContext.Default.EvidenceScopeHoldCreateRequestDto);
        return SendPageAsync(HttpMethod.Post, Route("holds"), new StringContent(body, Encoding.UTF8, "application/json"), ct);
    }

    static TimeSpan Lifetime(EvidenceScopeManifestDto page) => page.IssuedAt is { } issued && page.ExpiresAt is { } expires ? expires - issued : ArtifactLifetime;

    Task<(EvidenceScopeStatus, EvidenceScopeState?)> FetchAllPagesAsync(CancellationToken ct) => FetchAllPagesAsync(t => GetPageAsync(Url(SingleQuery), t), ct);

    async Task<(EvidenceScopeStatus, EvidenceScopeState?)> FetchAllPagesAsync(Func<CancellationToken, Task<(EvidenceScopeStatus, EvidenceScopeManifestDto?)>> firstPage, CancellationToken ct) {
        var sentAt = time.GetUtcNow();
        var (status, first) = await firstPage(ct);
        if (status != EvidenceScopeStatus.Ok) return (status, null);
        if (first!.Held == true) _heldToken = first.Token;
        else if (first.Held == false) HoldRefused = true;

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

        return (EvidenceScopeStatus.Ok, new(first.ScopeVersion, first.RootSessionId, first.Complete, first.IncompleteReasons, sources, first.Token, sentAt + Lifetime(first),
            first.ExpiresAt, first.Held == true));
    }

    Task<(EvidenceScopeStatus, EvidenceScopeManifestDto?)> GetPageAsync(string url, CancellationToken ct) => SendPageAsync(HttpMethod.Get, url, null, ct);

    async Task<(EvidenceScopeStatus, EvidenceScopeManifestDto?)> SendPageAsync(HttpMethod method, string url, HttpContent? content, CancellationToken ct) {
        try {
            using var request = new HttpRequestMessage(method, url) { Content = content };
            using var resp    = await http.SendAsync(request, ct);
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
