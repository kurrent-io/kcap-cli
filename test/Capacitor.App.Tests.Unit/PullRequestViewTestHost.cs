using System.Reactive.Subjects;
using Avalonia.Controls;
using Avalonia.Threading;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

internal sealed class PullRequestViewTestHost : IAsyncDisposable {
    readonly Subject<AgentStatusDto?> _presence = new();
    public FakeTimeProvider Time { get; } = new();
    public FakePullRequestSource Source { get; }
    public PullRequestContextViewModel Model { get; }
    public PullRequestReader Reader { get; }
    public PullRequestCard Card { get; }
    public Window Window { get; }
    public int Opened { get; private set; }

    public PullRequestViewTestHost(double readerWidth = 700) {
        Source = new(Time) { TotalPages = 1 };
        Model = new(_presence, Source, Time, new RecordingOpener(), () => Opened++);
        Reader = new() { DataContext = Model };
        Card = new() { DataContext = Model, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        var grid = new Grid { ColumnDefinitions = new("*,320") };
        grid.Children.Add(Reader);
        Grid.SetColumn(Card, 1);
        grid.Children.Add(Card);
        Window = new() { Content = grid, Width = readerWidth + 320, Height = 850 };
    }

    public async Task ShowAsync() {
        Window.Show();
        Model.SetForeground(true);
        _presence.OnNext(Agent("agent", "claude", false, sessionId: "session"));
        Model.SetReaderVisible(true);
        await SettleAsync();
    }

    public async Task SettleAsync() {
        await WaitUntilAsync(() => Model.CanReveal && !Model.IsReading, what: "PR view settled");
        Dispatcher.UIThread.RunJobs();
        Window.UpdateLayout();
    }

    public async ValueTask DisposeAsync() {
        Window.Close();
        await Model.TeardownAsync();
        _presence.Dispose();
    }
}
