using System.Text;
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// The daemon-written envelope record of one hosted session: a bounded queue fed from the runtime's
/// emit site and one writer task that appends to disk. Record never does IO, so a hung filesystem
/// costs journal lines rather than protocol liveness; what it costs is counted and rendered as a
/// note in the file itself.
internal sealed class TranscriptJournal : IDisposable {
    public const int Capacity = 4096;
    public static readonly TimeSpan CompleteGrace = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan LockBound     = TimeSpan.FromSeconds(2);

    readonly ILogger                 _logger;
    readonly TimeProvider            _time;
    readonly Action<string, byte[]>  _append;
    readonly JournalPathLocks        _locks;
    readonly TimeSpan                _grace;
    readonly TimeSpan                _lockBound;
    readonly Channel<JournalItem>    _queue;
    readonly CancellationTokenSource _writerCts = new();

    /// Test-only: the writer waits on it before its first read, pinning what is queued when it starts.
    readonly Task? _writerStartGate;

    Task?            _writer;
    int              _pendingGap;
    int              _completed;
    volatile bool    _latched;
    volatile string? _inFlightKind;

    public TranscriptJournal(
            string                   path,
            ILogger                  logger,
            TimeProvider?            time            = null,
            Action<string, byte[]>?  append          = null,
            JournalPathLocks?        locks           = null,
            int                      capacity        = Capacity,
            TimeSpan?                completeGrace   = null,
            TimeSpan?                lockBound       = null,
            Task?                    writerStartGate = null) {
        Path             = path;
        _logger          = logger;
        _time            = time ?? TimeProvider.System;
        _append          = append ?? AppendToFile;
        _locks           = locks ?? JournalPathLocks.Shared;
        _grace           = completeGrace ?? CompleteGrace;
        _lockBound       = lockBound ?? LockBound;
        _writerStartGate = writerStartGate;
        _queue           = Channel.CreateBounded<JournalItem>(new BoundedChannelOptions(capacity) {
            FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
    }

    public static TranscriptJournal ForAgent(string stateDir, string agentId, ILogger logger) =>
        new(System.IO.Path.Combine(stateDir, "transcripts", AgentFileNames.For(agentId) + ".jsonl"), logger);

    public string Path { get; }

    /// True only once the header is on disk: until then Record has nothing to append to.
    public bool IsOpen { get; private set; }

    public bool CreatedFile { get; private set; }

    /// Set by <see cref="CompleteAsync"/>: the writer exited within the grace.
    public bool Drained { get; private set; }

    public int PendingGap => Volatile.Read(ref _pendingGap);

    public bool Open(string? cwd, string? model) {
        try {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            CreatedFile = !File.Exists(Path);
            // The launch path is synchronous by design, and the bound is what keeps a journal held
            // by an abandoned writer from stalling it.
            using var lease = _locks.AcquireAsync(Path, _lockBound, CancellationToken.None).GetAwaiter().GetResult();
            if (lease is null) {
                _logger.LogWarning("Transcript journal {Path}: could not take the path lock to open", Path);
                return false;
            }

            var header = Encode(AcpEventTranslator.BuildSessionStarted(0, NowIso(), cwd, model));
            using (var fs = new FileStream(Path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) {
                fs.Seek(0, SeekOrigin.End);
                fs.Write(header);
                fs.Flush(true);
            }

            IsOpen  = true;
            _writer = Task.Run(() => RunWriterAsync(_writerCts.Token));
            return true;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Transcript journal {Path}: open failed; the session will not be journaled", Path);
            return false;
        }
    }

    public void Record(AcpEventEnvelope envelope) {
        if (!IsOpen || _latched || envelope.Ephemeral) return;
        var gap = Interlocked.Exchange(ref _pendingGap, 0);
        if (!_queue.Writer.TryWrite(new JournalItem(envelope, gap))) Interlocked.Add(ref _pendingGap, gap + 1);
    }

    public async Task<bool> CompleteAsync() {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return Drained;
        try {
            _queue.Writer.TryComplete();
            if (_writer is null) { Drained = true; return true; }

            // Wall-clock, not the injected TimeProvider: the grace bounds a shutdown against a hung
            // disk, and a caller's test clock advancing is not evidence the disk came back.
            if (await Task.WhenAny(_writer, Task.Delay(_grace)).ConfigureAwait(false) == _writer) {
                Drained = true;
                return true;
            }

            // Not awaited: the writer is by definition stuck inside an append the cancellation cannot
            // reach, so waiting on it here would spend the grace twice.
            _writerCts.Cancel();
            _latched = true;
            _logger.LogWarning(
                "Transcript journal {Path}: abandoning the writer — item {Kind} in flight, {Queued} queued, {Gap} unrecorded",
                Path, _inFlightKind ?? "(none)", _queue.Reader.Count, PendingGap);
            return false;
        } finally {
            // Safe under an abandoned writer: it holds its token, and a cancelled token needs no source.
            _writerCts.Dispose();
        }
    }

    /// CompleteAsync is the lifecycle call and disposes the source itself; this covers a journal
    /// dropped without one.
    public void Dispose() => _writerCts.Dispose();

    async Task RunWriterAsync(CancellationToken ct) {
        try {
            if (_writerStartGate is not null) await _writerStartGate.WaitAsync(ct).ConfigureAwait(false);
            await foreach (var item in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false)) {
                // ReadAllAsync drains what is already buffered without consulting the token again,
                // so an abandoned writer would keep appending past the grace it was given.
                ct.ThrowIfCancellationRequested();
                _inFlightKind = item.Envelope.Kind;
                await WriteItemAsync(item).ConfigureAwait(false);
                _inFlightKind = null;
            }

            var gap = Interlocked.Exchange(ref _pendingGap, 0);
            if (gap > 0) await WriteBytesAsync(Encode(GapNote(gap))).ConfigureAwait(false);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
        } catch (Exception ex) {
            // One warning for the whole session: a failing disk fails every subsequent append too.
            _latched = true;
            _queue.Writer.TryComplete();
            var abandoned = 0;
            while (_queue.Reader.TryRead(out _)) abandoned++;
            _logger.LogWarning(ex, "Transcript journal {Path}: write failed; {Abandoned} queued and {Gap} unrecorded envelopes are lost",
                Path, abandoned, PendingGap);
        }
    }

    /// One buffer per item, so a note and the envelope it precedes reach the file in one append.
    Task WriteItemAsync(JournalItem item) {
        byte[] bytes = item.GapBefore > 0
            ? [.. Encode(GapNote(item.GapBefore)), .. Encode(item.Envelope)]
            : Encode(item.Envelope);
        return WriteBytesAsync(bytes);
    }

    async Task WriteBytesAsync(byte[] bytes) {
        using var lease = await _locks.AcquireAsync(Path, _lockBound, CancellationToken.None).ConfigureAwait(false)
                       ?? throw new IOException("could not take the journal path lock");
        _append(Path, bytes);
    }

    AcpEventEnvelope GapNote(int count) =>
        new(Kind: AcpEventKind.SystemNote, Text: $"{count} envelopes were not recorded to this journal", TimestampIso: NowIso());

    static byte[] Encode(AcpEventEnvelope e) => Encoding.UTF8.GetBytes(EnvelopeJournalFormat.Write(e) + "\n");

    string NowIso() => _time.GetUtcNow().ToString("o");

    static void AppendToFile(string path, byte[] bytes) {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        fs.Seek(0, SeekOrigin.End);
        fs.Write(bytes);
        fs.Flush(true);
    }
}
