using System.Runtime.ExceptionServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Views;

/// Paste, drop and pick for one prompt card. One intake at a time, and nothing escapes a handler:
/// a read that fails becomes a refusal line the sink renders, one cancelled by teardown becomes
/// nothing at all.
public sealed class AttachmentDropPaste : IDisposable {
    readonly Control _card;
    readonly TextBox _textBox;
    readonly Button? _pick;
    readonly Func<IAttachmentSink?> _sink;
    readonly TimeProvider _time;
    readonly Func<Task<IAsyncDataTransfer?>> _clipboard;
    readonly Func<Task<IReadOnlyList<IStorageFile>>> _picker;
    readonly CancellationTokenSource _lifetime = new();
    bool _reentrantPaste;
    bool _disposed;
    bool _busy;
    Task? _intake;

    AttachmentDropPaste(
            Control card, TextBox textBox, Button? pick, Func<IAttachmentSink?> sink, TimeProvider time,
            Func<Task<IAsyncDataTransfer?>> clipboard, Func<Task<IReadOnlyList<IStorageFile>>> picker) {
        _card = card;
        _textBox = textBox;
        _pick = pick;
        _sink = sink;
        _time = time;
        _clipboard = clipboard;
        _picker = picker;
    }

    public Task? PendingIntakeForTesting => _intake;

    public static AttachmentDropPaste Attach(
            Control card, TextBox textBox, Button? pickButton, Func<IAttachmentSink?> sink, TimeProvider time,
            Func<Task<IAsyncDataTransfer?>>? clipboard = null, Func<Task<IReadOnlyList<IStorageFile>>>? picker = null) {
        var behaviour = new AttachmentDropPaste(card, textBox, pickButton, sink, time,
            clipboard ?? (() => TopLevel.GetTopLevel(textBox)?.Clipboard?.TryGetDataAsync() ?? Task.FromResult<IAsyncDataTransfer?>(null)),
            picker ?? (async () => {
                var storage = TopLevel.GetTopLevel(textBox)?.StorageProvider;
                return storage is null ? [] : await storage.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true });
            }));
        textBox.AddHandler(TextBox.PastingFromClipboardEvent, behaviour.OnPasting, RoutingStrategies.Bubble);
        DragDrop.SetAllowDrop(card, true);
        card.AddHandler(DragDrop.DragEnterEvent, behaviour.OnDragOver);
        card.AddHandler(DragDrop.DragOverEvent, behaviour.OnDragOver);
        card.AddHandler(DragDrop.DragLeaveEvent, behaviour.OnDragLeave);
        card.AddHandler(DragDrop.DropEvent, behaviour.OnDrop);
        if (pickButton is not null) pickButton.Click += behaviour.OnPick;
        return behaviour;
    }

    void OnPasting(object? sender, RoutedEventArgs e) {
        if (_reentrantPaste) {
            _reentrantPaste = false;
            return;
        }
        e.Handled = true;
        StartIntake(async ct => {
            var transfer = await _clipboard();
            if (transfer is null) return null;
            using (transfer) {
                // Files win, so their presence settles the kind before any other flavour is touched: a
                // copied file often carries a text flavour too, and one that faults must not cost the files.
                var text = !transfer.Contains(DataFormat.File) && transfer.Contains(DataFormat.Text) ? await transfer.TryGetTextAsync() : null;
                var sink = _sink();
                switch (AttachmentIntake.Classify(transfer.Formats, !string.IsNullOrWhiteSpace(text))) {
                    case IntakeKind.Text:
                        PasteText();
                        return null;
                    case IntakeKind.Files:
                        if (sink is { CanAttach: false }) return Refusal(sink);
                        return await AttachmentIntake.ReadFilesAsync(await transfer.TryGetFilesAsync() ?? [], Capacity(sink), ct);
                    case IntakeKind.Bitmap: {
                        if (sink is { CanAttach: false }) return Refusal(sink);
                        using var bitmap = await transfer.TryGetBitmapAsync();
                        return bitmap is null ? IntakeResult.Empty : AttachmentIntake.FromBitmap(bitmap, _time);
                    }
                    default:
                        return null;
                }
            }
        }, new IntakeRefusal("clipboard", "the clipboard could not be read"));
    }

    /// The TextBox's own paste was handled away, so the text case runs it again. The latch has to
    /// outlive the call — the platform clipboard read inside it is asynchronous, and the paste event
    /// it raises on the way back would otherwise read as the user's and be handled away in turn —
    /// and the arrival it was armed for drops it, so the very next paste is the user's again. The
    /// post is the fallback for a paste that raises no event at all.
    void PasteText() {
        _reentrantPaste = true;
        try { _textBox.Paste(); } finally {
            Dispatcher.UIThread.Post(() => _reentrantPaste = false, DispatcherPriority.Background);
        }
    }

    void OnDragOver(object? sender, DragEventArgs e) {
        var takeable = e.DataTransfer.Contains(DataFormat.File) && _sink() is { CanAttach: true };
        e.DragEffects = takeable ? DragDropEffects.Copy : DragDropEffects.None;
        _card.Classes.Set("dragOver", takeable);
        e.Handled = true;
    }

    void OnDragLeave(object? sender, DragEventArgs e) => _card.Classes.Set("dragOver", false);

    void OnDrop(object? sender, DragEventArgs e) {
        _card.Classes.Set("dragOver", false);
        e.Handled = true;
        // Enumerated now, while the platform's payload is still live, but a provider that faults
        // becomes the intake's refusal rather than an exception loose in the event handler.
        List<IStorageItem>? files = null;
        Exception? failure = null;
        try { files = e.DataTransfer.TryGetFiles()?.ToList() ?? []; } catch (Exception ex) { failure = ex; }
        StartIntake(async ct => {
            if (failure is not null) ExceptionDispatchInfo.Throw(failure);
            var sink = _sink();
            return sink is { CanAttach: false } ? null : await AttachmentIntake.ReadFilesAsync(files!, Capacity(sink), ct);
        }, new IntakeRefusal("dropped files", "the dropped files could not be read"));
    }

    void OnPick(object? sender, RoutedEventArgs e) => StartIntake(async ct => {
        var sink = _sink();
        if (sink is { CanAttach: false }) return Refusal(sink);
        var picking = _picker();
        IReadOnlyList<IStorageFile> files;
        try { files = await picking.WaitAsync(ct); } catch (OperationCanceledException) {
            // The picker is still open on a card that is gone: whatever it answers, including a
            // failure, is observed here rather than left to the finalizer.
            _ = picking.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            throw;
        }
        return await AttachmentIntake.ReadFilesAsync(files, Capacity(sink), ct);
    }, new IntakeRefusal("file picker", "the file picker could not be opened"));

    static int Capacity(IAttachmentSink? sink) => sink?.FreeSlots ?? InputWire.MaxAttachmentsPerPrompt;

    static IntakeResult Refusal(IAttachmentSink sink) =>
        new([], [new("attachments", sink.AttachHint ?? "attachments are not available")]);

    /// The busy flag is raised here rather than inside the delegate: the delegate runs a dispatcher
    /// turn later, and a source that pumps the queue on its way in — a file picker does — would
    /// otherwise open a second intake before the first is recorded. The sink's own call is guarded
    /// too: nothing it throws may reach a task nobody awaits.
    void StartIntake(Func<CancellationToken, Task<IntakeResult?>> work, IntakeRefusal onFailure) {
        if (_busy) return;
        _busy = true;
        var ct = _lifetime.Token;
        _intake = Dispatcher.UIThread.InvokeAsync(async () => {
            try {
                IntakeResult? result;
                try { result = await work(ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    return;
                } catch (Exception ex) {
                    Console.Error.WriteLine($"attachment intake: {ex.Message}");
                    result = new IntakeResult([], [onFailure]);
                }
                if (ct.IsCancellationRequested || result is null) return;
                _sink()?.Accept(result);
            } catch (Exception ex) {
                Console.Error.WriteLine($"attachment intake: {ex.Message}");
            } finally {
                _busy = false;
            }
        });
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        DragDrop.SetAllowDrop(_card, false);
        _textBox.RemoveHandler(TextBox.PastingFromClipboardEvent, OnPasting);
        _card.RemoveHandler(DragDrop.DragEnterEvent, OnDragOver);
        _card.RemoveHandler(DragDrop.DragOverEvent, OnDragOver);
        _card.RemoveHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        _card.RemoveHandler(DragDrop.DropEvent, OnDrop);
        if (_pick is not null) _pick.Click -= OnPick;
        _lifetime.Dispose();
    }
}
