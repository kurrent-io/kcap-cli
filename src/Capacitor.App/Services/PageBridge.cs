using System.Buffers;
using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Pages;

namespace Capacitor.App.Services;

/// The app's end of a page's bridge, answering what the web viewer's bridge answers: the page's
/// ready, submit and refresh, under the viewer's own credential, and its results while the viewer
/// may see them.
///
/// The WebView's native channel is reachable from the page frame and any frame inside it, and does
/// not say which frame sent a message, so the nonce the host script adds is the only proof that a
/// message passed the host script's checks. Anything else is dropped without a word.
public sealed class PageBridge : IAsyncDisposable {
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(5000);
    public static readonly FrozenSet<string> InboundTypes = new[] { "ready", "submit", "refresh" }.ToFrozenSet(StringComparer.Ordinal);
    static readonly string[] ResultsFields = ["mode", "version", "closed", "revealed"];
    static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    // The host script's own cap on the page's message, plus room for the nonce wrapper.
    const int MaxRawLength = 256 * 1024 + 256;

    readonly IPageContentSource _source;
    readonly ArtefactDto _page;
    readonly int _version;
    readonly string _prefix;
    readonly Func<string, Task> _postToPage;
    readonly TimeProvider _time;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly CancellationTokenSource _disposed = new();
    readonly Lock _timerLock = new();
    PageResultsAccess _access;
    ITimer? _timer;
    bool _stopped;
    bool _sentResults;
    string? _lastEtag;
    int _ticking;

    public PageBridge(IPageContentSource source, ArtefactDto page, int version, PageResultsAccess access, string nonce,
            Func<string, Task> postToPage, TimeProvider time) {
        _source = source;
        _page = page;
        _version = version;
        _access = access;
        _prefix = $"{{\"nonce\":\"{nonce}\"";
        _postToPage = postToPage;
        _time = time;
    }

    /// The last tick's work, for tests that advance time and must wait for its reads.
    internal Task TickCompletion { get; private set; } = Task.CompletedTask;

    /// Arms the results poll, or the access re-check while access is undecided.
    public void Start() {
        if (_access is not (PageResultsAccess.Visible or PageResultsAccess.Unknown)) return;
        lock (_timerLock) {
            if (_stopped || _timer is not null) return;
            _timer = _time.CreateTimer(_ => TickCompletion = TickAsync(), null, PollInterval, PollInterval);
        }
    }

    public async Task OnNativeMessageAsync(string raw) {
        try {
            if (Unwrap(raw) is not { } data) return;
            await RunAsync(ct => HandleAsync(data, ct)).ConfigureAwait(false);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: page bridge message failed: {ex.Message}");
        }
    }

    /// The host script writes the nonce as the first member, so a message without this load's exact
    /// prefix is dropped unparsed. The prefix comparison is the nonce check: it runs in fixed time
    /// over the prefix's length, which leaks only the nonce's length, a public constant.
    JsonElement? Unwrap(string raw) {
        if (raw.Length > MaxRawLength || raw.Length < _prefix.Length
            || !CryptographicOperations.FixedTimeEquals(MemoryMarshal.AsBytes(raw.AsSpan(0, _prefix.Length)), MemoryMarshal.AsBytes(_prefix.AsSpan())))
            return null;
        try {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.Obj("data") is not { } data || data.Str("kcap") != "v1" || data.Str("type") is null) return null;
            return data.Clone();
        } catch (Exception e) when (e is JsonException or InvalidOperationException) {
            // InvalidOperationException: a string member holding a lone surrogate, which JSON.stringify writes as an escape.
            return null;
        }
    }

    async Task RunAsync(Func<CancellationToken, Task> work) {
        var ct = _disposed.Token;
        try {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            return;
        }
        try {
            await work(ct).ConfigureAwait(false);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
        } finally {
            _gate.Release();
        }
    }

    Task HandleAsync(JsonElement data, CancellationToken ct) => data.Str("type") switch {
        "ready"   => ReadyAsync(ct),
        "submit"  => SubmitAsync(data.Prop("payload") is { } payload && !payload.IsNull ? payload : EmptyObject, ct),
        "refresh" => CanReadResults ? PushResultsAsync(ct) : Task.CompletedTask,
        _         => Task.CompletedTask,
    };

    bool CanReadResults => _access == PageResultsAccess.Visible;

    async Task ReadyAsync(CancellationToken ct) {
        var name = await _source.ViewerNameAsync(ct).ConfigureAwait(false);
        var mine = await _source.MineAsync(_page.ArtefactId, _version, ct).ConfigureAwait(false);
        var prior = mine.Status is >= 200 and < 300 && mine.Body is { } body && body.Arr("responses") is { } responses
            && responses.GetArrayLength() > 0 && responses[0].Prop("payload") is { } payload
                ? payload
                : (JsonElement?)null;

        await PostAsync(w => {
            w.WriteString("type", "init");
            w.WriteNumber("version", _version);
            w.WriteString("theme", "dark");
            w.WriteStartObject("viewer");
            w.WriteString("name", name);
            w.WriteEndObject();
            w.WritePropertyName("prior");
            if (prior is { } p) p.WriteTo(w); else w.WriteNullValue();
        }).ConfigureAwait(false);

        // A page that announces itself has seen no results yet, whatever an earlier tick sent.
        _sentResults = false;
        if (CanReadResults) await PushResultsAsync(ct).ConfigureAwait(false);
    }

    async Task SubmitAsync(JsonElement payload, CancellationToken ct) {
        var reply = await _source.SubmitAsync(_page.ArtefactId, _version, payload, ct).ConfigureAwait(false);
        if (reply.Status is >= 200 and < 300) {
            await PostAsync(w => {
                w.WriteString("type", "ack");
                w.WriteBoolean("accepted", true);
                w.WritePropertyName("response_id");
                if (reply.Body?.Prop("response_id") is { } id) id.WriteTo(w); else w.WriteNullValue();
                w.WriteNumber("version", _version);
            }).ConfigureAwait(false);
            _sentResults = false;
            if (CanReadResults) await PushResultsAsync(ct).ConfigureAwait(false);
            return;
        }

        var error = reply.Body?.Str("message") is { Length: > 0 } message ? message : reply.Status switch {
            0   => "Couldn't reach the server — try again.",
            409 => "Responses are closed for this page.",
            429 => "Too many answers — try again in a moment.",
            _   => $"The server refused this answer (HTTP {reply.Status}).",
        };
        await PostAsync(w => {
            w.WriteString("type", "ack");
            w.WriteBoolean("accepted", false);
            w.WriteString("error", error);
            w.WriteNumber("code", reply.Status);
        }).ConfigureAwait(false);
    }

    async Task PushResultsAsync(CancellationToken ct) {
        var reply = await _source.ResultsAsync(_page.ArtefactId, _version, ct).ConfigureAwait(false);
        if (reply.Status is 401 or 403 or 404) {
            // Refused, gone, or signed out: asking again will not change the answer for this load.
            _access = reply.Status == 401 ? PageResultsAccess.SignedOut : PageResultsAccess.Hidden;
            StopPolling();
            return;
        }
        if (reply.Status is < 200 or >= 300 || reply.Body is not { } body || !body.IsObject) return;

        var etag = body.Prop("etag")?.GetRawText();
        if (!_sentResults || etag != _lastEtag) {
            _sentResults = true;
            _lastEtag = etag;
            await PostAsync(w => {
                w.WriteString("type", "results");
                foreach (var field in ResultsFields) {
                    w.WritePropertyName(field);
                    if (body.Prop(field) is { } value) value.WriteTo(w); else w.WriteNullValue();
                }
                w.WritePropertyName("results");
                body.WriteTo(w);
            }).ConfigureAwait(false);
        }
        if (body.Bool("closed") == true) StopPolling();
    }

    async Task TickAsync() {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try {
            await RunAsync(async ct => {
                if (_access == PageResultsAccess.Unknown) {
                    _access = await _source.ResultsAccessAsync(_page.ArtefactId, _page, ct).ConfigureAwait(false);
                    if (_access == PageResultsAccess.Unknown) return;
                    if (!CanReadResults) {
                        StopPolling();
                        return;
                    }
                    _sentResults = false;
                }
                if (CanReadResults) await PushResultsAsync(ct).ConfigureAwait(false);
                else StopPolling();
            }).ConfigureAwait(false);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: page results poll failed: {ex.Message}");
        } finally {
            Volatile.Write(ref _ticking, 0);
        }
    }

    void StopPolling() {
        ITimer? timer;
        lock (_timerLock) {
            _stopped = true;
            timer = _timer;
            _timer = null;
        }
        timer?.Dispose();
    }

    async Task PostAsync(Action<Utf8JsonWriter> write) {
        if (_disposed.IsCancellationRequested) return;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        try {
            await _postToPage(Encoding.UTF8.GetString(buffer.WrittenSpan)).ConfigureAwait(false);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: could not deliver to the page: {ex.Message}");
        }
    }

    public ValueTask DisposeAsync() {
        StopPolling();
        _disposed.Cancel();
        return ValueTask.CompletedTask;
    }
}
