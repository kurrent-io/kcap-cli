using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

sealed class ChatPromptHarness : IAsyncDisposable {
    readonly BehaviorSubject<ChatSessionInfo> _session;
    readonly FakeTimeProvider _time = new();
    public ChatPromptFeed Feed { get; } = new();
    public AcceptingChatPromptInput Input { get; } = new();
    public SessionRuns Runs { get; }
    public ChatTabViewModel Chat { get; }

    public ChatPromptHarness(string vendor) {
        _session = new(new("Running", "Running", vendor, null, null, null, false, "", "feed"));
        Runs = new(_time);
        Chat = new("a1", AgentOrigin.Local, _session, Observable.Return<string[]?>(null), Input,
            new ChatPromptUploader(), _ => Feed, new RecordingOpener(), _time, new FakePermissionService(), Runs);
    }
    public async Task ReadyAsync() => await (Chat.PendingReadForTesting ?? Task.CompletedTask);
    public void SetIdle() => _session.OnNext(_session.Value with { AwaitingInput = true });
    public async Task SendAsync(string text) { Chat.ComposerText = text; await Chat.SendCommand.Execute(); }
    public async Task TickAsync() { _time.Advance(ChatTabViewModel.PollInterval); await ReadyAsync(); }
    public async Task DeliverAsync(ChatProjectionResult receipt) {
        Feed.Enqueue(receipt);
        Feed.Enqueue(new([new AcpEventEnvelope(Kind: AcpEventKind.AssistantText, Text: "working")], [], []));
        await TickAsync();
    }
    public async ValueTask DisposeAsync() { await Chat.TeardownAsync(); _session.Dispose(); }
}
