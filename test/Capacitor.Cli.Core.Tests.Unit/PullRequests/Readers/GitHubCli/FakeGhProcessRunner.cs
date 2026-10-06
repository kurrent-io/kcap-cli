namespace Capacitor.Cli.Core.Tests.Unit.PullRequests.Readers.GitHubCli;

/// <summary>Answers by argument prefix; an unmatched call fails with exit 1 so a test cannot pass on an unscripted spawn.</summary>
internal sealed class FakeGhProcessRunner : IProcessRunner {
    public readonly List<(string FileName, string[] Args, RunOptions Options)> Calls = [];
    readonly List<(string[] Prefix, Func<Task<ProcessResult>> Reply, Task? OnCancel)> _replies = [];
    public Exception? StartFailure;

    // Replaces an earlier rule for the same exact prefix, so a test can re-script a call mid-run.
    public void When(string[] prefix, string stdout, int exitCode = 0, string stderr = "", bool timedOut = false) {
        _replies.RemoveAll(reply => reply.Prefix.SequenceEqual(prefix));
        _replies.Add((prefix, () => Task.FromResult(new ProcessResult(exitCode, stdout, stderr, timedOut)), null));
    }
    public void WhenPending(string[] prefix, TaskCompletionSource<ProcessResult> source) => _replies.Add((prefix, () => source.Task, null));
    /// <summary>Like a KillTree run: on cancellation the call ends only once <paramref name="onCancel"/> completes, standing in for the child's exit.</summary>
    public void WhenPending(string[] prefix, TaskCompletionSource<ProcessResult> source, Task onCancel) => _replies.Add((prefix, () => source.Task, onCancel));
    /// <summary>Matches when every needle appears somewhere in the argument list; register the more specific rule first.</summary>
    public void WhenAll(string[] needles, string stdout, int exitCode = 0, string stderr = "")
        => _replies.Add((needles, () => Task.FromResult(new ProcessResult(exitCode, stdout, stderr, false)), null));

    public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
        Calls.Add((fileName, args, options));
        if (StartFailure is not null) throw StartFailure;
        foreach (var (prefix, reply, onCancel) in _replies)
            if (args.Length >= prefix.Length && prefix.SequenceEqual(args.Take(prefix.Length)) || prefix.All(args.Contains)) return Await(reply(), onCancel, ct);
        return Task.FromResult(new ProcessResult(1, "", "unscripted: " + string.Join(' ', args), false));
    }

    static async Task<ProcessResult> Await(Task<ProcessResult> reply, Task? onCancel, CancellationToken ct) {
        try { return await reply.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (onCancel is not null) { await onCancel.ConfigureAwait(false); throw; }
    }

    public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options, Action<StreamedLine> onLine, CancellationToken ct)
        => throw new NotSupportedException();
}
