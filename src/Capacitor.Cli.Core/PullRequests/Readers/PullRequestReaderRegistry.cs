namespace Capacitor.Cli.Core.PullRequests.Readers;

/// <summary>
/// Session links always come from <paramref name="sessionLinks"/>; reading routes to the first
/// ready provider serving the subject's kind and host. Nothing here names a provider.
/// </summary>
public sealed class PullRequestReaderRegistry(IPullRequestSource sessionLinks, IReadOnlyList<IPullRequestReaderProvider> providers, TimeProvider? time = null)
        : IPullRequestSource, IPullRequestReaders {
    readonly Lock _lock = new();
    readonly TimeProvider _time = time ?? TimeProvider.System;
    readonly Dictionary<string, (PullRequestRepository? Repository, string? Branch, string? SubjectKey, string? ProviderName)> _sessions = new(StringComparer.Ordinal);
    PullRequestReaderStatus[] _statuses = [.. providers.Select(_ => new PullRequestReaderStatus(PullRequestReaderStatusKind.Failed, "not_probed"))];

    public async Task<PullRequestCapability> DiscoverAsync(bool refresh, CancellationToken ct) {
        var probes = providers.Select(provider => provider.ProbeAsync(refresh, ct)).ToArray();
        var links = sessionLinks.DiscoverAsync(refresh, ct);
        var statuses = await Task.WhenAll(probes).ConfigureAwait(false);
        var capability = await links.ConfigureAwait(false);
        lock (_lock) _statuses = statuses;
        return statuses.Any(status => status.IsReady) ? new(PullRequestCapabilityKind.Supported, 1) : capability;
    }

    // The served-provider stamp survives a reset: only the link source and providers restart, so a
    // reroute discovered right after this call still fires TakeChange's one-time Restart.
    public void ResetSession(string sessionId) {
        sessionLinks.ResetSession(sessionId);
        foreach (var provider in providers) provider.ResetSession(sessionId);
    }

    public void DescribeSession(string sessionId, PullRequestRepository? repository, string? branch) {
        lock (_lock) {
            if (_sessions.Count >= 1024 && !_sessions.ContainsKey(sessionId)) _sessions.Remove(_sessions.Keys.First());
            _sessions.TryGetValue(sessionId, out var existing);
            _sessions[sessionId] = (repository, branch, existing.SubjectKey, existing.ProviderName);
        }
    }

    public async Task<PullRequestRead<PullRequestLinkListDto>> ListAsync(string sessionId, CancellationToken ct) {
        var capability = await sessionLinks.DiscoverAsync(false, ct).ConfigureAwait(false);
        var links = capability.Kind switch {
            PullRequestCapabilityKind.Supported => await sessionLinks.ListAsync(sessionId, ct).ConfigureAwait(false),
            PullRequestCapabilityKind.Legacy or PullRequestCapabilityKind.Unsupported => await sessionLinks.LegacyLinksAsync(sessionId, ct).ConfigureAwait(false),
            PullRequestCapabilityKind.SignedOut => new(PullRequestReadKind.SignedOut, AccessFailure: "invalid"),
            _ => new PullRequestRead<PullRequestLinkListDto>(PullRequestReadKind.Unavailable, Reason: capability.Reason ?? "discovery_unavailable", AccessFailure: "transient", RetryAt: capability.RetryAt)
        };
        (PullRequestRepository? Repository, string? Branch, string? SubjectKey, string? ProviderName) context;
        lock (_lock) context = _sessions.GetValueOrDefault(sessionId);

        if (links.Kind != PullRequestReadKind.Ready || links.Data is null) {
            if (context is { Repository: { } fallbackRepository, Branch: { Length: > 0 } fallbackBranch }
                    && Ready().FirstOrDefault(provider => provider.Serves(fallbackRepository.Provider, fallbackRepository.Host)) is { } fallbackProvider) {
                var discovered = await fallbackProvider.DiscoverAsync(fallbackRepository, fallbackBranch, ct).ConfigureAwait(false);
                if (discovered.Count > 0) return new(PullRequestReadKind.Ready, MergeAndOrder(discovered), FetchedAt: _time.GetUtcNow().UtcDateTime);
            }
            return links;
        }

        var items = links.Data.Items.Select(Resolve).ToList();
        if (context is { Repository: { } repository, Branch: { Length: > 0 } branch })
            foreach (var provider in Ready().Where(provider => provider.Serves(repository.Provider, repository.Host)))
                items.AddRange(await provider.DiscoverAsync(repository, branch, ct).ConfigureAwait(false));
        return links with { Data = MergeAndOrder(items) };
    }

    static PullRequestLinkListDto MergeAndOrder(IEnumerable<PullRequestLinkDto> items) {
        var merged = items.DistinctBy(item => (item.Provider, item.Host, item.Owner.ToLowerInvariant(), item.RepoName.ToLowerInvariant(), item.Number))
            .OrderBy(item => item.Owner.ToLowerInvariant(), StringComparer.Ordinal).ThenBy(item => item.RepoName.ToLowerInvariant(), StringComparer.Ordinal).ThenBy(item => item.Number).ToArray();
        return new() { Items = merged };
    }

    public Task<PullRequestRead<PullRequestLinkListDto>> LegacyLinksAsync(string sessionId, CancellationToken ct) => sessionLinks.LegacyLinksAsync(sessionId, ct);

    public async Task<PullRequestRead<PullRequestOverviewDto>> OverviewAsync(string sessionId, PullRequestSubjectDto subject, CancellationToken ct) {
        if (Route(subject) is not { } provider) {
            lock (_lock) Stamp(sessionId, SubjectKey(subject), "");
            return NoReader<PullRequestOverviewDto>(subject);
        }
        var stamp = Stamp(provider);
        if (TakeChange(sessionId, subject, stamp)) return new(PullRequestReadKind.Restart, Subject: subject, Reason: "integration_changed");
        var read = await provider.OverviewAsync(sessionId, subject, ct).ConfigureAwait(false);
        return Stale(subject, stamp, sessionId) ? new(PullRequestReadKind.Restart, Subject: subject, Reason: "integration_changed") : read;
    }

    public async Task<PullRequestRead<PullRequestPageDto<T>>> PageAsync<T>(string sessionId, PullRequestSubjectDto subject, string section,
            string? cursor, string? resolved, string? threadId, CancellationToken ct) where T : class {
        if (Route(subject) is not { } provider) {
            lock (_lock) Stamp(sessionId, SubjectKey(subject), "");
            return NoReader<PullRequestPageDto<T>>(subject);
        }
        var stamp = Stamp(provider);
        if (TakeChange(sessionId, subject, stamp)) return new(PullRequestReadKind.Restart, Subject: subject, Reason: "integration_changed");
        var read = await provider.PageAsync<T>(sessionId, subject, section, cursor, resolved, threadId, ct).ConfigureAwait(false);
        return Stale(subject, stamp, sessionId) ? new(PullRequestReadKind.Restart, Subject: subject, Reason: "integration_changed") : read;
    }

    public PullRequestReaderNote? NoteFor(string provider, string host) {
        if (Ready().Any(reader => reader.Serves(provider, host))) return null;
        PullRequestReaderStatus[] statuses;
        lock (_lock) statuses = _statuses;
        for (var i = 0; i < providers.Count; i++) {
            var reader = providers[i];
            if (reader.ProviderKind != provider || reader.Tool is not { } tool) continue;
            var status = statuses[i];
            var text = status.Kind switch {
                PullRequestReaderStatusKind.ToolMissing => $"Install {tool.Name} to read pull requests here.",
                PullRequestReaderStatusKind.SignedOut => $"{tool.Name} is not signed in. Run {tool.SignInCommand(null)} to read pull requests here.",
                PullRequestReaderStatusKind.Ready => $"{tool.Name} is not signed in for {host}. Run {tool.SignInCommand(host)} to read it here.",
                PullRequestReaderStatusKind.Failed when status.Reason == "unsupported_version" => $"Update {tool.Name} to read pull requests here.",
                _ => null
            };
            var showInstall = status.Kind == PullRequestReaderStatusKind.ToolMissing || status.Reason == "unsupported_version";
            if (text is not null) return new(text, showInstall ? tool.InstallUrl : null, tool.Name);
        }
        return null;
    }

    public string? PrLink(string? url, PullRequestSubjectDto subject) {
        var owner = providers.FirstOrDefault(provider => provider.ProviderKind == subject.Provider);
        return owner is null ? PullRequestWire.SafeLink(url) : owner.PrLink(url, subject);
    }

    IEnumerable<IPullRequestReaderProvider> Ready() {
        PullRequestReaderStatus[] statuses;
        lock (_lock) statuses = _statuses;
        return providers.Where((_, i) => statuses[i].IsReady);
    }
    IPullRequestReaderProvider? Route(PullRequestSubjectDto subject) => Ready().FirstOrDefault(provider => provider.Serves(subject.Provider, subject.Host));
    // A completed read is stale when routing this subject now, from scratch, disagrees with the stamp it was dispatched
    // under: no route at all, a different or re-identified provider, or a session stamp another call already moved on.
    // Discovering the former also fast-forwards the session's stamp, so the very next read isn't forced through its
    // own separate restart to learn what this completion just found out.
    bool Stale(PullRequestSubjectDto subject, string stamp, string sessionId) {
        if (Route(subject) is not { } current) return true;
        var currentStamp = Stamp(current);
        if (currentStamp == stamp) return Superseded(sessionId, subject, stamp);
        lock (_lock) Stamp(sessionId, SubjectKey(subject), currentStamp);
        return true;
    }
    // Captured once per read: the provider's own Identity can change while a read is in flight, and the stale value must still be rejected on completion.
    static string Stamp(IPullRequestReaderProvider provider) => provider.Name + "|" + provider.Identity;
    static string SubjectKey(PullRequestSubjectDto s) => $"{s.Provider}|{s.Host}|{s.Owner}|{s.RepoName}|{s.Number}".ToLowerInvariant();
    // A read dispatched to one provider can outlive a rediscovery that rerouted the session's stamp to another; a stale answer is a restart, never data.
    bool Superseded(string sessionId, PullRequestSubjectDto subject, string stamp) {
        lock (_lock) {
            _sessions.TryGetValue(sessionId, out var entry);
            return entry.SubjectKey == SubjectKey(subject) && entry.ProviderName != stamp;
        }
    }
    bool TakeChange(string sessionId, PullRequestSubjectDto subject, string stamp) {
        lock (_lock) {
            _sessions.TryGetValue(sessionId, out var entry);
            var subjectKey = SubjectKey(subject);
            // A prior "" stamp means no reader was ever dispatched; regaining one is a fresh start, not a reroute.
            var changed = entry.SubjectKey == subjectKey && entry.ProviderName is { Length: > 0 } previous && previous != stamp;
            Stamp(sessionId, subjectKey, stamp);
            return changed;
        }
    }
    // Callers hold `_lock`.
    void Stamp(string sessionId, string subjectKey, string stamp) {
        if (_sessions.Count >= 1024 && !_sessions.ContainsKey(sessionId)) _sessions.Remove(_sessions.Keys.First());
        _sessions.TryGetValue(sessionId, out var entry);
        _sessions[sessionId] = (entry.Repository, entry.Branch, subjectKey, stamp);
    }
    PullRequestLinkDto Resolve(PullRequestLinkDto link) {
        if (link.Provider != "unknown") return link;
        foreach (var provider in providers) {
            if (provider.ParseLink(link.Url) is not { } subject) continue;
            var hash = link.RepoHash == "legacy" ? RepoHashHelper.ComputeRepoHash(subject.Owner, subject.RepoName) : link.RepoHash;
            return link with { Provider = subject.Provider, Host = subject.Host, Owner = subject.Owner, RepoName = subject.RepoName, Number = subject.Number, RepoHash = hash };
        }
        return link;
    }
    static PullRequestRead<T> NoReader<T>(PullRequestSubjectDto subject) where T : class
        => new(PullRequestReadKind.Unavailable, Subject: subject, Reason: "no_reader", AccessFailure: "invalid");
}
