using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Capacitor.Cli.Daemon.Harness.Muse;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Muse;

/// <summary>
/// Interactive <see cref="IMuseMspProcess"/> fake: <see cref="Push"/> puts a line on the child's
/// stdout at any moment, <see cref="Writes"/> captures what the runtime wrote to stdin, and
/// <see cref="EndOfStream"/> is the child exiting.
/// </summary>
internal sealed class FakeMuseMspProcess : IMuseMspProcess {
    readonly Channel<string> _lines  = Channel.CreateUnbounded<string>();
    readonly Lock            _gate   = new();
    readonly List<string>    _writes = [];

    /// <summary>The <c>result</c> object an <c>initialize</c> request is answered with, echoing its
    /// id. Null leaves the handshake unanswered.</summary>
    public string? AutoInitializeResult { get; set; } = MuseMspWire.InitializeResult;

    /// <summary>Invoked inside <see cref="WriteLineAsync"/> for every written line, so a test can
    /// answer a request at the moment it goes on the wire.</summary>
    public Action<string>? OnWrite { get; set; }

    /// <summary>Every <see cref="WriteLineAsync"/> fails, as when the child's stdin is gone.</summary>
    public bool FailWrites { get; set; }

    /// <summary>Completes a recorded write via this delegate instead of immediately, to park or
    /// fault one specific line.</summary>
    public Func<string, Task>? WriteOverride { get; set; }

    /// <summary>Replaces <see cref="TerminateAsync"/>'s completion; the call is still counted.</summary>
    public Func<Task>? TerminateOverride { get; set; }

    public int  Pid            { get; } = 4242;
    public bool HasExited      { get; private set; }
    public int? ExitCode       { get; private set; }
    public int  TerminateCalls { get; private set; }
    public int  DisposeCalls   { get; private set; }

    public string? Diagnostics { get; set; }

    public IReadOnlyList<string> Writes {
        get { lock (_gate) return _writes.ToArray(); }
    }

    public void Push(string line) => _lines.Writer.TryWrite(line);

    public void EndOfStream(int exitCode = 0) {
        HasExited = true;
        ExitCode  = exitCode;
        _lines.Writer.TryComplete();
    }

    public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken ct) {
        while (await _lines.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            while (_lines.Reader.TryRead(out var line))
                yield return line;
    }

    public Task WriteLineAsync(string json, CancellationToken ct) {
        if (FailWrites) return Task.FromException(new IOException("fake muse stdin is gone"));

        lock (_gate) _writes.Add(json);

        if (AutoInitializeResult is { } result) AnswerInitialize(json, result);

        OnWrite?.Invoke(json);

        return WriteOverride?.Invoke(json) ?? Task.CompletedTask;
    }

    void AnswerInitialize(string json, string result) {
        using var frame = MuseMspFrame.TryParse(json);

        if (frame is { Method: "initialize", Id: { } id })
            Push($$"""{"jsonrpc":"2.0","id":{{id}},"result":{{result}}}""");
    }

    public Task WaitForExitAsync(TimeSpan? timeout = null) => Task.CompletedTask;

    /// <summary>When true, closing stdin ends the child with exit code 0.</summary>
    public bool ExitsOnInputClose { get; set; }
    public int  InputCloseCalls   { get; private set; }

    /// <summary>Lines the child writes to stdout as it exits on input close.</summary>
    public IReadOnlyList<string> LinesOnInputClose { get; set; } = [];

    /// <summary>Invoked at the start of every <see cref="CloseInputAsync"/>, before it acts.</summary>
    public Action? OnInputClose { get; set; }

    public Task CloseInputAsync(TimeSpan timeout) {
        InputCloseCalls++;
        OnInputClose?.Invoke();
        if (!ExitsOnInputClose) return Task.CompletedTask;

        foreach (var line in LinesOnInputClose) Push(line);
        EndOfStream(0);
        return Task.CompletedTask;
    }

    public Task TerminateAsync(TimeSpan? timeout = null) {
        TerminateCalls++;
        if (TerminateOverride is { } o) return o();
        HasExited = true;
        ExitCode ??= -1;
        _lines.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() {
        DisposeCalls++;
        _lines.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
