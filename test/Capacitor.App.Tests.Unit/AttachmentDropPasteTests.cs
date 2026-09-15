using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit;

/// The composer's intake behaviour over a real TextBox and card under a headless window, with the
/// clipboard and picker reached through their seams. The headless drawing backend encodes a bitmap
/// to zero bytes, so the bitmap test pins the chip the paste produced, never its bytes.
public class AttachmentDropPasteTests {
    const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";
    static readonly DateTimeOffset Staged = new(2026, 9, 14, 10, 30, 5, TimeSpan.Zero);

    static IDataTransfer FileTransfer(params IStorageItem[] files) {
        var transfer = new DataTransfer();
        foreach (var file in files) {
            var item = new DataTransferItem();
            item.SetFile(file);
            transfer.Add(item);
        }
        return transfer;
    }

    static IDataTransfer TextTransfer(string text) {
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(text));
        return transfer;
    }

    static Task SetClipboardTextAsync(Visual near, string text) =>
        TopLevel.GetTopLevel(near)!.Clipboard!.SetDataAsync(new FakeAsyncDataTransfer(text: text));

    sealed class Harness : IDisposable {
        public RecordingSink Sink { get; } = new();
        public TextBox Box { get; } = new();
        public Button Pick { get; } = new() { Content = "+" };
        public Border Card { get; }
        public Window Window { get; }
        public AttachmentDropPaste Behaviour { get; }
        public FakeTimeProvider Time { get; } = new(Staged);
        public int ClipboardReads { get; private set; }

        public Func<Task<IAsyncDataTransfer?>> Clipboard { get; set; } = () => Task.FromResult<IAsyncDataTransfer?>(null);
        public Func<Task<IReadOnlyList<IStorageFile>>> Picker { get; set; } = () => Task.FromResult<IReadOnlyList<IStorageFile>>([]);

        public Harness() {
            Card = new Border { Child = new StackPanel { Children = { Box, Pick } } };
            Window = new Window { Content = Card, Width = 420, Height = 260 };
            Window.Show();
            Dispatcher.UIThread.RunJobs();
            Behaviour = AttachmentDropPaste.Attach(
                Card, Box, Pick, () => Sink, Time,
                () => { ClipboardReads++; return Clipboard(); },
                () => Picker());
        }

        public Task Intake => Behaviour.PendingIntakeForTesting ?? Task.CompletedTask;

        public void ClipboardHolds(IAsyncDataTransfer transfer) => Clipboard = () => Task.FromResult<IAsyncDataTransfer?>(transfer);

        public void RaisePaste() {
            Box.Focus();
            Dispatcher.UIThread.RunJobs();
            Box.RaiseEvent(new RoutedEventArgs(TextBox.PastingFromClipboardEvent));
        }

        public void ClickPick() => Pick.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        public DragDropEffects DragOver(IDataTransfer transfer) {
            var args = new DragEventArgs(DragDrop.DragOverEvent, transfer, Card, new Point(5, 5), KeyModifiers.None);
            Card.RaiseEvent(args);
            return args.DragEffects;
        }

        public void Drop(IDataTransfer transfer) =>
            Card.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, transfer, Card, new Point(5, 5), KeyModifiers.None));

        /// Awaits the intake in flight and drains the queue behind it, including the background job
        /// that releases the behaviour's own re-entrant-paste latch.
        public async Task SettleAsync() {
            await Intake;
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose() {
            Behaviour.Dispose();
            Window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Files_on_the_clipboard_are_staged_and_the_transfer_is_disposed_once() {
        await RunOnUiAsync(async () => {
            using var harness = new Harness();
            var transfer = new FakeAsyncDataTransfer(files: [FakeStorageFile.Of("a.png", new byte[] { 1, 2, 3 })]);
            harness.ClipboardHolds(transfer);

            harness.RaisePaste();
            await harness.SettleAsync();

            var intake = harness.Sink.Accepted.Single();
            await Assert.That(intake.Accepted.Select(f => f.FileName)).IsEquivalentTo(["a.png"]);
            await Assert.That(intake.Refused).IsEmpty();
            await Assert.That(transfer.Disposed).IsEqualTo(1);
            await Assert.That(harness.Box.Text ?? "").IsEqualTo("");
        });
    }

    /// The behaviour handles the event, so the TextBox's own paste never runs: the text case has to
    /// put it back, once, without the re-entrant event it raises starting a second intake.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Text_on_the_clipboard_pastes_exactly_once() {
        await RunOnUiAsync(async () => {
            using var harness = new Harness();
            var transfer = new FakeAsyncDataTransfer(text: "hello");
            harness.ClipboardHolds(transfer);
            await SetClipboardTextAsync(harness.Box, "hello");

            harness.RaisePaste();
            await harness.SettleAsync();

            await Assert.That(harness.Box.Text).IsEqualTo("hello");
            await Assert.That(harness.ClipboardReads).IsEqualTo(1);
            await Assert.That(harness.Sink.Accepted).IsEmpty();
            await Assert.That(transfer.Disposed).IsEqualTo(1);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_bitmap_is_staged_under_a_clock_named_png_and_disposed() {
        await RunOnUiAsync(async () => {
            using var harness = new Harness();
            var bitmap = new Bitmap(new MemoryStream(Convert.FromBase64String(OnePixelPng)));
            var transfer = new FakeAsyncDataTransfer(bitmap: bitmap);
            harness.ClipboardHolds(transfer);

            harness.RaisePaste();
            await harness.SettleAsync();

            var staged = harness.Sink.Accepted.Single().Accepted.Single();
            await Assert.That(staged.FileName).IsEqualTo("pasted-image-20260914-103005.png");
            await Assert.That(staged.ContentType).IsEqualTo("image/png");
            await Assert.That(transfer.Disposed).IsEqualTo(1);
        });
    }

    /// Nothing to take, a read that throws and a behaviour disposed mid-read: each releases the
    /// transfer exactly once, and none of them leaves an exception for the finalizer to raise.
    [Test]
    [NotInParallel]
    public async Task Nothing_a_thrown_read_and_a_cancelled_one_dispose_once_and_never_throw() {
        await RunOnUiAsync(async () => {
            var unobserved = new List<Exception>();
            void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) {
                unobserved.Add(e.Exception);
                e.SetObserved();
            }
            TaskScheduler.UnobservedTaskException += OnUnobserved;
            try {
                using (var harness = new Harness()) {
                    var empty = new FakeAsyncDataTransfer();
                    harness.ClipboardHolds(empty);
                    harness.RaisePaste();
                    await harness.SettleAsync();
                    await Assert.That(harness.Sink.Accepted).IsEmpty();
                    await Assert.That(empty.Disposed).IsEqualTo(1);

                    var thrower = new FakeAsyncDataTransfer(text: "boom", throwOnRead: true);
                    harness.ClipboardHolds(thrower);
                    harness.RaisePaste();
                    await harness.SettleAsync();
                    await Assert.That(harness.Sink.Accepted.Single().Refused)
                        .IsEquivalentTo([new IntakeRefusal("clipboard", "the clipboard could not be read")]);
                    await Assert.That(thrower.Disposed).IsEqualTo(1);
                }

                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var midRead = new Harness();
                var gated = new FakeAsyncDataTransfer(files: [FakeStorageFile.Of("late.png", new byte[] { 1 })], gate: gate.Task);
                midRead.ClipboardHolds(gated);
                midRead.RaisePaste();
                var intake = midRead.Intake;
                midRead.Behaviour.Dispose();
                gate.SetResult();
                await intake;
                Dispatcher.UIThread.RunJobs();

                await Assert.That(midRead.Sink.Accepted).IsEmpty();
                await Assert.That(gated.Disposed).IsEqualTo(1);

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Assert.That(unobserved).IsEmpty();
            } finally {
                TaskScheduler.UnobservedTaskException -= OnUnobserved;
            }
        });
    }

    /// One intake at a time, and a shut gate still never blocks typing: text goes in, files come
    /// back as the sink's own refusal.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_second_paste_during_an_intake_is_dropped_and_a_closed_gate_still_pastes_text() {
        await RunOnUiAsync(async () => {
            using var harness = new Harness();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.ClipboardHolds(new FakeAsyncDataTransfer(files: [FakeStorageFile.Of("a.png", new byte[] { 1 })], gate: gate.Task));

            harness.RaisePaste();
            var first = harness.Intake;
            harness.RaisePaste();
            await Assert.That(harness.ClipboardReads).IsEqualTo(1);
            gate.SetResult();
            await first;
            Dispatcher.UIThread.RunJobs();
            await Assert.That(harness.Sink.Accepted.Single().Accepted.Select(f => f.FileName)).IsEquivalentTo(["a.png"]);

            harness.Sink.Accepted.Clear();
            harness.Sink.CanAttachValue = false;
            harness.ClipboardHolds(new FakeAsyncDataTransfer(text: "typed"));
            await SetClipboardTextAsync(harness.Box, "typed");
            harness.RaisePaste();
            await harness.SettleAsync();
            await Assert.That(harness.Box.Text).IsEqualTo("typed");
            await Assert.That(harness.Sink.Accepted).IsEmpty();

            var files = new FakeAsyncDataTransfer(files: [FakeStorageFile.Of("b.png", new byte[] { 1 })]);
            harness.ClipboardHolds(files);
            harness.RaisePaste();
            await harness.SettleAsync();
            await Assert.That(harness.Sink.Accepted.Single().Refused)
                .IsEquivalentTo([new IntakeRefusal("attachments", "attachments need the daemon updated")]);
            await Assert.That(files.Disposed).IsEqualTo(1);
        });
    }

    /// A drop the sink cannot take stages nothing and says nothing: the disabled pick button's
    /// tooltip already carries the reason, and the drag never offered to copy.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_drop_stages_files_and_drag_over_copies_only_for_files_the_sink_can_take() {
        await RunOnUiAsync(async () => {
            using var harness = new Harness();

            await Assert.That(harness.DragOver(FileTransfer(FakeStorageFile.Of("a.png", new byte[] { 1 })))).IsEqualTo(DragDropEffects.Copy);
            await Assert.That(harness.Card.Classes.Contains("dragOver")).IsTrue();
            await Assert.That(harness.DragOver(TextTransfer("plain"))).IsEqualTo(DragDropEffects.None);

            harness.Sink.CanAttachValue = false;
            await Assert.That(harness.DragOver(FileTransfer(FakeStorageFile.Of("b.png", new byte[] { 1 })))).IsEqualTo(DragDropEffects.None);
            harness.Drop(FileTransfer(FakeStorageFile.Of("c.png", new byte[] { 1 })));
            await harness.SettleAsync();
            await Assert.That(harness.Sink.Accepted).IsEmpty();

            harness.Sink.CanAttachValue = true;
            harness.Drop(FileTransfer(FakeStorageFile.Of("d.png", new byte[] { 1, 2 })));
            await harness.SettleAsync();

            await Assert.That(harness.Sink.Accepted.Single().Accepted.Select(f => f.FileName)).IsEquivalentTo(["d.png"]);
            await Assert.That(harness.Card.Classes.Contains("dragOver")).IsFalse();
        });
    }

    /// A picker outlives the card it was opened from — the user takes their time, the tab closes.
    /// Whatever it answers, files or a failure, reaches nothing and raises nothing.
    [Test]
    [NotInParallel]
    public async Task The_pick_button_stages_files_and_a_picker_outliving_the_card_stages_nothing() {
        await RunOnUiAsync(async () => {
            var unobserved = new List<Exception>();
            void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) {
                unobserved.Add(e.Exception);
                e.SetObserved();
            }
            TaskScheduler.UnobservedTaskException += OnUnobserved;
            try {
                using (var harness = new Harness()) {
                    harness.Picker = () => Task.FromResult<IReadOnlyList<IStorageFile>>([FakeStorageFile.Of("picked.png", new byte[] { 1, 2 })]);
                    harness.ClickPick();
                    await harness.SettleAsync();
                    await Assert.That(harness.Sink.Accepted.Single().Accepted.Select(f => f.FileName)).IsEquivalentTo(["picked.png"]);
                }

                var late = new TaskCompletionSource<IReadOnlyList<IStorageFile>>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (var closed = new Harness()) {
                    closed.Picker = () => late.Task;
                    closed.ClickPick();
                    var intake = closed.Intake;
                    closed.Behaviour.Dispose();
                    late.SetResult([FakeStorageFile.Of("late.png", new byte[] { 1 })]);
                    await intake;
                    Dispatcher.UIThread.RunJobs();
                    await Assert.That(closed.Sink.Accepted).IsEmpty();
                }

                var failed = new TaskCompletionSource<IReadOnlyList<IStorageFile>>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (var broken = new Harness()) {
                    broken.Picker = () => failed.Task;
                    broken.ClickPick();
                    var intake = broken.Intake;
                    broken.Behaviour.Dispose();
                    failed.SetException(new InvalidOperationException("the picker died"));
                    await intake;
                    Dispatcher.UIThread.RunJobs();
                    await Assert.That(broken.Sink.Accepted).IsEmpty();
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Assert.That(unobserved).IsEmpty();
            } finally {
                TaskScheduler.UnobservedTaskException -= OnUnobserved;
            }
        });
    }
}
