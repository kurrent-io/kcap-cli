# Attachments in the desktop prompts: paste, drop and pick files (AI-2318)

Slice of the desktop shell (parent AI-2171). Two prompts take text today and nothing else: the
Home launcher's goal box and the session Chat composer. Pasting a screenshot into either is a
silent no-op, because the composer is a plain Avalonia `TextBox` and every send path below it
carries a string. This spec gives both prompts attachments — paste an image or files from the
clipboard, drop files onto the box, or pick them with a "+" button — shown as removable chips
and delivered to the agent as files it can read.

The daemon already knows how to deliver an attachment. A server-origin `SendInput` and a
server-origin launch both carry `attachment_ids`; the daemon fetches each id from the server's
temp store into `<worktree>/.attached/` and appends `[Attached files: .attached/x.png]` to the
prompt. The web composer uses exactly this. What is missing is everything above it in the app,
one new local input frame, and a few daemon rules the local lane and the fail-closed contract
need.

## Decisions

1. **Bytes travel through the server's temp attachment store on every lane.** The app uploads
   the staged files to `POST /api/attachments/upload` at send time, receives ids, and sends ids:
   on `RequestLaunchAgentV2.attachment_ids` for a launch (the payload already declares it), and
   on a new local frame for a chat send. The daemon's existing `DownloadAttachmentsAsync` does the
   rest. This is the one mechanism that already ships bytes to a daemon on another machine, it is
   what the web uses, its 10 MB cap is enforced server-side, and it leaves nothing new to sweep.
   Rejected: bytes over the local socket (a new chunked frame family, a second store with its own
   lifetime, no remote story, and an 8 MB frame ceiling under the server's 10 MB file cap); the
   app writing straight into the worktree (it knows `worktree_path` only for local agents, and a
   second writer in a daemon-owned tree is a race with cleanup).
2. **A send that carries attachments rides a frame for every vendor, PTY included.** Text-only
   PTY sends stay on the attach `Stdin` path unchanged. The daemon has to download and name the
   files, so the daemon has to compose the message; `PtyHostedAgentRuntime.SendUserInputAsync` is
   the bracketed paste the web already drives. Rejected: moving all PTY input onto a frame (out of
   AI-2197's scope for a reason — the terminal send gate's semantics are tied to the attach
   lifecycle and nothing here needs them changed).
3. **The agent learns about a file by path, the same way on every vendor.** The daemon's
   `[Attached files: …]` trailer names each file relative to the agent's cwd. Claude Code reads
   an image or a PDF through its `Read` tool; Codex through `view_image` and its shell; ACP, Pi
   and Antigravity agents through their own file tools. Rejected: vendor-native image inputs
   (`codex --image` at launch, ACP `image` content blocks) — a per-vendor capability matrix for
   a result the path already gives, and a second delivery shape to keep in step with the first.
4. **An attachment that cannot be delivered fails the send; it is never silently dropped.** Today
   both daemon paths are best-effort: a missing id logs a warning and the text goes out without
   it. A user who attached a file meant the file, and text without it changes meaning. A fetch
   failure (404, oversize, IO, containment) is a `delivery_failed` drop naming the id on both
   callers, and a launch whose attachments cannot be fetched fails with `attachment_unavailable`.
   This changes the web path as well, deliberately: the drop is reported through
   `ReportInputDroppedAsync`, which the web already renders. **The app offers attachments only
   to a daemon that enforces this** (decision 6); a daemon that predates it would still be
   best-effort, so the app never hands it an id.
5. **Attachments are refused for an agent that does not run in a daemon-owned worktree.** The
   launch path already guards on `WorkLocation.OwnedWorktree`; the send path does not, so a
   follow-up attachment to an in-place agent would write `.attached/` into the user's own
   checkout. The daemon refuses with a coded reason, and the app hides the affordance when the
   status dto's `work_location` is not the owned worktree.
6. **Delivery to an older daemon fails closed at the byte level; the affordance is gated on
   what the app can see.** A chat send with ids uses a new frame type, `SendTextWithAttachments`,
   which an older daemon's codec rejects before routing — the fail-closed contract `FrameType`
   already documents. The reserved alternative (a trailing `attachment_ids` on `SendTextDto`) is
   rejected: an older decoder ignores an unknown member, delivers the text and loses the files
   without a word, and the capability check that would prevent it runs on the status connection,
   not on the one-shot socket the send opens — a daemon restarted to an older build between the
   two would slip through. The affordance itself is gated so the user is told before, not after:
   the Chat composer on `input/2` from the local daemon's hello; the Home launcher on the target
   daemon's advertised version for a remote machine (`DaemonInfo.Version`, the one capability
   signal the server relays) and on `input/2` for the local one. The version gate is on kcap's
   own daemon release, not a vendor build, so the borrowed-review invariant about version gating
   does not apply.
7. **Text is required; attachments are additive.** Send and Start stay gated on non-blank text.
   The daemon already refuses an empty text, and a placeholder invented by the app to carry a
   lone image would be the app putting words in the user's mouth. The hint says what is missing.
8. **Limits mirror the server's.** 10 MiB per file (the server's `MaxFileSize`, refused by the
   app before upload with the same wording), 10 files per prompt, any content type. The agent's
   own tools decide what it can read; a type blocklist maintained here buys nothing, and the web
   has none.
9. **Staged bytes live in the app's memory until Send, and a launch's bytes until the launch
   settles.** The server's temp store expires an upload after 10 minutes, so uploading on paste
   would strand a chip the user takes their time over. A launch is only *accepted* when the hub
   returns; its attachments are fetched later by the daemon, so the sent draft is retained until
   the launch is confirmed or fails, and restored on failure (§2). At most 100 MiB per draft by
   the limits above, the same shape the web's Blazor composer holds.
10. **Recall restores text only.** The arrow-key prompt history is a text history; a recalled
    prompt does not re-stage files that were already delivered.

## 1. What the user sees

Both prompts gain the same three pieces inside their existing card, above the text box:

- **A chip strip**, one chip per staged file: a 32×32 thumbnail for an image (decoded scaled,
  off the UI thread) or a file glyph, the file name (ellipsised), the size (`184 KB`, `2.3 MB`),
  and a remove button. Empty strip, zero height.
- **A "+" button** in the footer row beside the hint (Chat) and beside the chips row (Home),
  opening the OS file picker (`IStorageProvider.OpenFilePickerAsync`, multi-select).
- **A drop target**: the whole card. While files are dragged over it the card's border takes the
  primary colour; a drop stages them. A dragged folder is refused.

Paste (Cmd+V / Ctrl+V, or the context menu) into either text box:

- clipboard has files → stage them, no text inserted;
- otherwise clipboard has non-blank text → the TextBox's own paste, unchanged;
- otherwise clipboard has a bitmap → stage it as `pasted-image-<yyyyMMdd-HHmmss>.png`.

Text wins over a bitmap when both are present because a screenshot carries no text, a copied
web image carries HTML rather than plain text, and the case where both exist is a user who copied
text: pasting that as an image would be the surprise.

Refusals show inline where the composer hint (Chat) or `StartError` (Home) already renders, for
one intake and cleared by the next edit or intake. One line per intake: the accepted files are
staged and the refused ones are named — "`report.zip` is over 10 MB", "`Docs` is a folder", "only
10 files per message — `c.png`, `d.png` not added" — joined with "; " when several. Channel
refusals: "attachments need the daemon updated" (Chat, no `input/2`; Home, the selected machine's
daemon is older), "attachments aren't available for an in-place session", "sign in to attach
files". When the "+" is disabled its tooltip carries the same text. Nothing toasts.

While sending, the hint reads "Uploading 2 files…" then "Sending…" (Chat) or the Start button
is disabled with "Uploading…" (Home). On success the text and the chips clear together. On any
failure both stay, and the hint says why. A launch failure that arrives after the hub accepted
the request restores the draft (§2, Launch). The user's own turn appears in Chat from the
transcript as it does today, with the daemon's trailer on it — the same text the web shows.

## 2. App

### Staging: `AttachmentTray`

One `ReactiveObject` owned by the prompt's view model (`ChatTabViewModel`, `HomeViewModel`):

```csharp
public sealed class AttachmentTray : ReactiveObject {
    public ReadOnlyObservableCollection<StagedAttachment> Items { get; }
    public int Count { get; }
    public long TotalBytes { get; }
    /// Files that fit are staged in order; the rest come back named, with the refusal wording.
    public IReadOnlyList<IntakeRefusal> AddAll(IReadOnlyList<StagedAttachment> files);
    public void Remove(StagedAttachment file);
    public IReadOnlyList<StagedAttachment> Snapshot();
    public bool ContentEquals(IReadOnlyList<StagedAttachment> snapshot);
    public void Restore(IReadOnlyList<StagedAttachment> snapshot);   // replaces the contents
    public void Clear();
}

public sealed record StagedAttachment(string FileName, string ContentType, ReadOnlyMemory<byte> Bytes) {
    public string SizeLabel { get; }
    public bool IsImage { get; }   // ContentType starts with "image/"
}

public sealed record IntakeRefusal(string Name, string Reason);
```

`AddAll` enforces the count limit from `InputWire` (§3): files past the tenth are refused with
"only 10 files per message". Size is refused earlier, at intake, so a file over the cap is never
read into memory. A file name is deduplicated against the tray by appending ` (2)`, ` (3)` before
the extension, so two screenshots pasted in one second are two chips and two files. The thumbnail
is a `StagedAttachmentViewModel` concern — decoded lazily on a worker, published on the UI
thread, disposed with the chip.

### Intake: `AttachmentIntake`

A static classifier the views call, pure over Avalonia's data-transfer and storage types so it is
unit-testable without a clipboard:

```csharp
public static class AttachmentIntake {
    public static IntakeKind Classify(IReadOnlyList<DataFormat> formats, bool hasNonBlankText);
    public static Task<IntakeResult> ReadFilesAsync(IEnumerable<IStorageItem> items, CancellationToken ct);
    public static StagedAttachment FromBitmap(Bitmap bitmap, TimeProvider time);
    public static string ContentTypeFor(string fileName);
}
public enum IntakeKind { Files, Text, Bitmap, Nothing }
public sealed record IntakeResult(IReadOnlyList<StagedAttachment> Accepted, IReadOnlyList<IntakeRefusal> Refused);
```

`Classify` implements the precedence in §1. `ReadFilesAsync` walks the items in order: an
`IStorageFolder` is refused ("is a folder"); an `IStorageFile` whose basic properties report a
size over `MaxAttachmentBytes` is refused ("is over 10 MB") without being opened; otherwise it is
read through `OpenReadAsync` into a buffer capped at `MaxAttachmentBytes + 1` bytes, and refused
with the same wording if the stream runs past the cap (a provider that reports no size). An
`IOException` on one item refuses that item ("could not be read") and the walk continues. The
result carries both lists; the caller stages `Accepted` through `AddAll` and shows every refusal
— intake's and the tray's — as the one line described in §1. `FromBitmap` encodes PNG with
`Bitmap.Save`; the caller disposes the `Bitmap` afterwards. `ContentTypeFor` is a small
extension table (png, jpg/jpeg, gif, webp, pdf, txt, md, json, csv, log, xml, yaml/yml, common
source extensions → `text/plain`), defaulting to `application/octet-stream`.

### View wiring: `AttachmentDropPaste`

An attached behaviour applied to both cards, so `ChatTabView` and `LauncherPaneView` register the
same code once. It talks to the prompt through a small `IAttachmentSink`:

```csharp
public interface IAttachmentSink {
    bool CanAttach { get; }            // false: the intake is refused with AttachHint, nothing read
    string? AttachHint { get; }
    void Accept(IntakeResult result);  // stages Accepted, renders every refusal as the one line
}
```

- **Paste**: a handler on the TextBox's `PastingFromClipboard`. The event is synchronous and the
  clipboard read is not, so the handler always marks the event handled and starts one async
  intake on the UI thread. The `IAsyncDataTransfer` from `TopLevel.Clipboard.TryGetDataAsync()`
  is disposed in a `finally` once everything the intake needs has been materialised — after
  `ReadFilesAsync` returns, after `FromBitmap` has encoded, or immediately for `Text` and
  `Nothing` — on every path out, including a thrown read, cancellation and the TextBox unloading
  mid-read. `Files` → `ReadFilesAsync` → sink. `Bitmap` → `TryGetBitmapAsync` → `FromBitmap` →
  dispose the bitmap → sink. `Text` → set a re-entrancy flag and call `TextBox.Paste()`, whose
  second raise of the event the handler lets through; the flag is cleared in the same `finally`.
  `Nothing` → no-op. When the sink's `CanAttach` is false the intake stops after `Classify`:
  `Text` still pastes, `Files`/`Bitmap` render `AttachHint` and read nothing. A result arriving
  after the TextBox has unloaded is discarded. One intake at a time: a paste while one is in
  flight is dropped.
- **Drop**: `DragDrop.SetAllowDrop(card, true)`; `DragOver` sets `DragDropEffects.Copy` iff
  `e.DataTransfer.Contains(DataFormat.File)` and the sink's `CanAttach`, else `None`; `Drop` →
  `TryGetFiles()` → `ReadFilesAsync` → sink. Enter/Leave toggle a `dragOver` pseudo-class on the
  card for the border colour.
- **Pick**: the "+" button → `OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true })`
  → `ReadFilesAsync` → sink. The button's `IsEnabled` follows `CanAttach`, its tooltip
  `AttachHint`.

### Upload: `IAttachmentUploader`

```csharp
public interface IAttachmentUploader {
    Task<UploadOutcome> UploadAsync(IReadOnlyList<StagedAttachment> files, CancellationToken ct);
}
public sealed record UploadOutcome(UploadKind Kind, IReadOnlyList<string> Ids, string? Reason);
public enum UploadKind { Uploaded, Unauthorized, Rejected, Unreachable }
```

`ServerAttachmentUploader` follows `ServerSessionHttp` exactly: a client leased per call from
`ICapacitorHttpClient.ForWaitAsync`, `AuthStatus` other than `Ok`/`NoAuthRequired` →
`Unauthorized`; one `MultipartFormDataContent` with each file as a `ByteArrayContent` named
`files` carrying its `ContentType` and file name, posted to `{server}/api/attachments/upload`;
400 → `Rejected` with the body, 401 → `Unauthorized`, anything else or a transport exception →
`Unreachable`. Nothing throws past cancellation. Timeout: the leased client's own.

A 200 is `Uploaded` only if its body is an `UploadedAttachment[]` with **exactly one element per
staged file, in order, each `Id` valid by `InputWire.IsValidAttachmentId` (§3) and distinct**.
Anything else — fewer or more elements, a null, blank, malformed or repeated id, an unparsable
body — is `Rejected` with "the server returned an unexpected upload response", and the draft
stays. `Ids` is empty on every non-`Uploaded` outcome, never null.

The self-scoped route rather than `/api/agents/{agentId}/attachments`: the daemon downloads with
its own account's token, and the store admits a download when the downloader owns the upload —
which holds for every send the desktop composer can make (a local socket is the owner's; a remote
daemon is one of the signed-in user's own). The agent-scoped route adds a visibility gate that
only matters for a non-owner writing into someone else's agent, which the desktop never does. If
the local daemon is bound to a different server or account than the app's profile, the download
404s and the send fails with the id named (decision 4) — visible, not silent.

### Chat send

`ChatInput` gains the attachment channel:

```csharp
public abstract Task<ChatSendOutcome> SendAsync(string text, IReadOnlyList<string> attachmentIds, CancellationToken ct);
/// Whether a send with attachments can be accepted now; false carries the reason in AttachHint.
public abstract bool CanAttach { get; }
public abstract string? AttachHint { get; }
```

- `LocalFrameChatInput`: `CanAttach` iff `Availability == Ready`, the daemon advertises `input/2`,
  and `Dto.WorkLocation` is the owned worktree; `AttachHint` names whichever fails, in that
  order. `SendAsync` with ids is one `SendTextWithAttachments` exchange through
  `ILocalControlOps.SendTextWithAttachmentsAsync`; without ids it is today's `SendTextAsync`. A
  refused ack maps `attachments_refused` to its `Error` text, like `delivery_failed`.
- `TerminalChatInput` gains the same inputs (`agentId`, `IDaemonClientService`, `ILocalControlOps`,
  presence) and the same `CanAttach` rule. `SendAsync` with an empty id list is today's
  synchronous `TrySendText`. With ids it is one `SendTextWithAttachments` exchange: `_sending`
  closes `CanAcceptText` for its duration so the composer cannot issue a second send, and the
  outcome maps as `LocalFrameChatInput` maps it, clearing on `Ok`. The frame path for a PTY
  runtime is the daemon's bracketed paste plus its own submit (§4), so the app sends no CR of its
  own. Keystrokes typed on the Terminal tab during the exchange are not blocked by the app: they
  are serialised behind the paste on the daemon (§4, input lane), which is the only place both
  writers meet.

`ChatTabViewModel.SendCommand` does the upload, once, before the channel:

1. Snapshot text, edit count and the tray (`Snapshot()`).
2. If the snapshot has files and `!_input.CanAttach`, refuse with `_input.AttachHint`; nothing is
   sent.
3. If it has files, set `Uploading` (hint "Uploading N files…", `canSend` false), call the
   uploader with the lifetime token. `Unauthorized` → hint "sign in to attach files";
   `Rejected`/`Unreachable` → hint with the reason. Any failure keeps text and chips and stops.
4. `_input.SendAsync(text, ids, ct)`. `Accepted` clears the text (existing rule: only if the
   snapshot is still the draft) **and clears the tray iff `ContentEquals(snapshot)`** — a file
   added during the round trip stays. `Rejected` and `Unconfirmed` keep both.

`canSend` adds `!Uploading`. `QueuedChatMessage.Matches` accepts a transcript text that equals the
sent text, or equals it followed by the daemon's trailer (`AttachmentTrailer.Prefix` from Core,
§3, preceded by the blank line) — otherwise a send with attachments would never be acknowledged
and the queued banner would never clear.

### Launch

`LaunchRequest` gains `IReadOnlyList<string> AttachmentIds` (empty by default); `LaunchPayload.For`
copies it to the payload's existing `attachment_ids` as null when empty, so the wire an older
server expects is untouched.

`HomeViewModel` exposes `CanAttach`/`AttachHint` for the launcher card: signed in (the server lane
connected), and the selected machine attachment-capable — the local daemon advertising `input/2`,
or a remote machine whose `DaemonInfo.Version` parses as SemVer and is at least
`LaunchAttachments.MinDaemonVersion` (§3). An unparsable or missing version is not capable.
Changing the machine while files are staged does not drop them; if the new machine is not
capable, Start is disabled with the hint until the chips are removed or the machine changed back.

`StartAsync`, after the remote ownership check and before building the request: if the tray has
files, `Uploading = true` (Start disabled, label "Uploading…"), upload; `Unauthorized` → the
existing `_signInRequired` signal; other failures → `StartError` with the reason, nothing
launched.

**The draft outlives the accepted request.** `LaunchOutcome.Started` is request acceptance, not
success — the daemon fetches the files later and a failure arrives as a `LaunchFailed`
broadcast. So when the request carried attachments, `StartAsync` clears `Goal` and the tray as
today but keeps `PendingLaunchDraft(goal, files)` in `_pendingDrafts[agentId]`, beside the
`_pendingLaunches` entry and under the same lock, with the same normalised-id key and the same
10-minute TTL. It is settled with the launch:

- a directory row confirms the launch (`ConfirmPendingRows`) → the draft is discarded;
- a `LaunchFailed` for the pending id (`ApplyFailureIfPending`, or the buffered failure
  `RecordPendingLaunch` finds) → the draft is **restored** — `Goal` set and `Tray.Restore` — when
  the composer is blank and the tray empty, and `StartError` reads "the attached files could not
  be delivered — the draft is back, try again"; when the user has started a new draft, nothing is
  overwritten and `StartError` reads "the attached files could not be delivered — re-attach them";
- the TTL expiring, `ForgetLaunch`, or a failure for an untracked id → discarded, as the pending
  entry is.

A text-only launch keeps today's behaviour; there is nothing to lose but the text, which the
failure message already tells the user to retype. Navigating away from Home while a launch is
pending keeps the draft in the view model, which outlives the view.

Home never asks the daemon anything at launch time: a launch always goes through the server,
always into an owned worktree, so decision 5 does not apply to it.

## 3. Wire

### Local input frame (append-only)

A new client→daemon frame type, and a new DTO; the ack is the existing one:

```csharp
// Composer input with attachments — one-shot; acked on SendTextAck when the delivery settles.
SendTextWithAttachments = 24, // Text = SendTextWithAttachmentsDto JSON
```

```csharp
public sealed record SendTextWithAttachmentsDto(string AgentId, string Text, string[] AttachmentIds);
```

`{"agent_id","text","attachment_ids"}`, every member always emitted. `SendTextDto` is unchanged;
the app sends `SendText` for a text-only message and `SendTextWithAttachments` only with a
non-empty id list. An older daemon's `FrameCodec.ReadAsync` throws on byte 24 before routing; the
connection closes and the client reports `transport` — nothing delivered, as decision 6 requires.

New constants beside `MaxTextBytes`:

```csharp
public static class InputWire {
    public const int  MaxTextBytes            = 256 * 1024;
    public const int  MaxAttachmentsPerPrompt = 10;
    public const long MaxAttachmentBytes      = 10L * 1024 * 1024;  // the server's per-file cap
    /// The server mints Guid "N": 32 hex digits. Case-insensitive on the way in, as the server is.
    public static bool IsValidAttachmentId(string? id) => id is { Length: 32 } && Guid.TryParseExact(id, "N", out _);
    public static bool IsStructurallyValid(SendTextWithAttachmentsDto? dto) =>
        dto is not null && dto.AgentId is not null && dto.Text is not null && dto.AttachmentIds is not null;
}
public static class SendTextReasons {
    …
    public const string AttachmentsRefused = "attachments_refused";  // Error names why
}
public static class AttachmentTrailer {
    public const string Prefix = "[Attached files: ";
    public static string For(IEnumerable<string> relativePaths);  // "[Attached files: a, b]"
}
```

`AttachmentTrailer` lives in `Capacitor.Cli.Core` so the daemon composes it and the app
recognises it from one definition. `IsValidAttachmentId` is used by the uploader on the way in
and by the daemon handler on the way out, so the two can never disagree about an id.

Capability: `"input/2"` appended to `LocalControlCapabilities.Current`, meaning "the daemon routes
`SendTextWithAttachments`". `input/1` stays, so an older app's gate is unchanged.

`ILocalControlOps.SendTextWithAttachmentsAsync(string agentId, string text, IReadOnlyList<string>
attachmentIds, CancellationToken ct)` beside `SendTextAsync`, same exchange shape, same
`SendTextResult`, same absence of a reply timeout.

### Launch attachment gate

`LaunchAttachments.MinDaemonVersion` in the app: the kcap release version whose daemon fails a
launch closed on a missing attachment (§4) — set by the implementer to the version the
implementing PR ships in, and compared as SemVer against `DaemonInfo.Version`. Local daemons are
gated on `input/2` instead, which the same release adds.

### Server

No change. `RequestLaunchAgentV2` already admits and grants `attachment_ids`; the temp store, the
10 MB cap, the 10-minute TTL, the Guid-N id and the owner-or-granted download rule are what this
design leans on.

## 4. Daemon

### Routing and handler

`LocalControlServer` routes `SendTextWithAttachments` to
`AgentOrchestrator.HandleLocalSendTextWithAttachmentsAsync`, which shares `AnswerSendTextAsync`'s
body through one private core taking `(agentId, text, string[]? attachmentIds)`. Checks, in
order, after the existing text checks and before the delivery core:

- `attachment_ids` empty on this frame → `malformed` ("send_text carries no attachments" — the
  plain frame is for that);
- more than `MaxAttachmentsPerPrompt`, or any id failing `IsValidAttachmentId`, or a repeated id →
  `attachments_refused`, Error "up to 10 attachments per message" / "malformed attachment id";
- `agent.Work != WorkLocation.OwnedWorktree` → `attachments_refused`, Error "attachments need a
  daemon-owned worktree".

Then `DeliverInputAsync(agent, text, attachmentIds)`.

### PTY input lane

`PtyHostedAgentRuntime` gains one `SemaphoreSlim(1, 1)` over its writes. `SendUserInputAsync`
holds it across the paste **and** `SubmitAsync`, so nothing else reaches the PTY between the
`ESC[201~` and the CR; `SendRawInputAsync` and `SendSpecialKeyAsync` take it per write.
`SendInterrupt` does not wait — a Ctrl+C is the one key that must land regardless. So keystrokes
from an attached terminal (the app's Terminal tab, `kcap agent attach`) queue for at most the
paste-to-submit interval and land after the CR, in order; nothing interleaves and nothing is
dropped. This closes the same window for the web's `SendInput` to a PTY agent, which has had it
since it shipped.

### `SingleSubmitDelay`

Goes from 50 ms to 150 ms. The interactive Codex TUI treats an Enter within about 120 ms of a
paste as a newline, and the app's own terminal channel already waits 150 ms for that reason; the
frame path now carries the app's PTY sends and must clear the same window. The
approvals-disabled spray schedule is unchanged.

### `DownloadAttachmentsAsync`

Returns a result rather than the paths it managed:

```csharp
sealed record AttachmentFetch(IReadOnlyList<string> RelativePaths, string? FailedId, string? Error);
```

**Containment.** `.attached/` is a directory the agent can replace: an agent with write access
to its worktree can turn it into a symlink or junction, and the daemon is not sandboxed. So:

- before any write, `.attached` is created if absent and then required to be a real directory:
  `File.GetAttributes` without `ReparsePoint`, and `DirectoryInfo.ResolveLinkTarget(true)` null.
  The worktree path itself is daemon-created and not re-checked;
- each file is opened with `FileMode.CreateNew`, so an existing entry — a planted symlink
  included — is never followed or overwritten (the unique-name helper picks the next name);
- after each write the directory check is repeated; if `.attached` is now a link, the file just
  written is deleted through the path it was written to and the fetch fails with "attachment
  directory was replaced".

A directory swapped between the check and `CreateNew` is the residual window: .NET exposes no
directory-handle-relative create to close it. The window is microseconds wide, needs an agent
that already writes inside its worktree, and the post-write re-check turns a win into a deleted
file and a failed send rather than a delivered one. Recorded here and in the code as the
constraint it is.

**Caps and failure.** Per id, in order: a non-success status, a `Content-Length` over
`MaxAttachmentBytes`, a body that exceeds it while streaming (the copy is capped at
`MaxAttachmentBytes + 1` bytes and the partial file deleted), a `Content-Disposition` name that
reduces to nothing, a containment failure, or an IO exception ends the fetch with that id and
error; files already written stay (they are inside `.attached/`, gitignored, and go with the
worktree). The trailer is composed only when every id landed. The content type is read but not
acted on.

### `DeliverInputAsync`

A failed fetch is `InputDeliveryOutcome.Drop(SendInputDropReason.DeliveryFailed,
$"attachment {id} unavailable: {error}")`. Both callers already route that reason — the server
caller through `ReportInputDroppedAsync`, the local one into the ack. Ids on a non-owned work
location, which only the server caller can now present, are the same `DeliveryFailed` drop with
"attachments need a daemon-owned worktree". The message is `text` alone, or
`text + "\n\n" + AttachmentTrailer.For(paths)`.

### Launch

The launch path's fetch stays where it is, guarded by `OwnedWorktree`, and stops being
best-effort: a failed fetch throws, the existing failure path removes the worktree, and the
server sees `LaunchFailed` with `attachment_unavailable: <id>`. Ids on a non-owned launch (only
possible from a server that ignored its own contract) fail the same way with
`attachments_refused`.

## 5. Compatibility

| App | Daemon | Server | Behaviour |
|---|---|---|---|
| new | new (`input/2`) | any | full: chips, paste, drop; chat sends with ids on `SendTextWithAttachments`; launch ids on V2 |
| new | old, local | any | Chat and Home "+", paste-as-file and drop refused with "attachments need the daemon updated"; text unchanged |
| new | old, remote (version below the gate) | any | Home attachments refused with the same hint; a text launch unchanged |
| new | restarted to an old build between hello and send | any | `SendTextWithAttachments` is undecodable there; the connection closes; the composer shows "delivery unconfirmed"; nothing delivered |
| old | new | any | old app sends `SendText` only; text-only as today |
| new | any | old (no V2 attachments) | V2 has carried `attachment_ids` since hosted agents shipped; not a live case |
| new | new | temp store id expired (>10 min between upload and fetch) | fetch 404 → send refused / launch failed with the id named; the draft is restored (Home) or kept (Chat) |

`work_location` null on a PTY dto (a daemon older than that field) also predates `input/2`, so it
takes the "daemon updated" hint, never a false "in-place".

## 6. Security and privacy

- The app uploads with the signed-in profile's token through the same client lease every other
  app HTTP call uses; the daemon downloads with its own — no new credential path, and the daemon
  still looks for none.
- Files land only inside `<owned worktree>/.attached/`, gitignored, excluded from borrowed
  snapshots, removed with the worktree at cleanup. Decision 5 closes the in-place path; the
  containment rules in §4 close the planted-link path, with the residual window named there.
- The byte cap on the download closes an unbounded stream from a compromised or misconfigured
  server; id validation on both ends closes a path segment smuggled as an id.
- Bytes sit on the server for at most 10 minutes and are never part of the session record; an
  attached text file's contents reach a transcript only if the agent reads it, at which point the
  watcher's redaction applies as to any tool result. `SecretRedactor` is not run over attachments:
  it rewrites JSON transcript lines, and a file is neither.
- The clipboard is read only on an explicit paste gesture, never polled, and released after each.

## 7. Testing

**Core** (`Capacitor.Cli.Core.Tests.Unit`)
- `FrameCodec`: `SendTextWithAttachments` encodes and decodes; the value is 24 and no existing
  value moved.
- `InputIpc`: `SendTextWithAttachmentsDto` round-trips; `IsStructurallyValid` rejects a missing
  `attachment_ids`; `IsValidAttachmentId` accepts a Guid-N in either case and rejects 31/33
  chars, dashes, a path segment, null, blank.
- `AttachmentTrailer.For` shapes one, two and many paths; `Prefix` matches.
- `LocalControlOps.SendTextWithAttachmentsAsync` serialises the ids as `attachment_ids`, sends
  frame 24, and maps EOF to `transport`.

**Daemon** (`Capacitor.Cli.Daemon.Tests.Unit`)
- Handler: an empty id list, over-count, a malformed id, a repeated id, and a non-owned work
  location each refuse with the stated reason and error before the delivery core runs; a valid
  list reaches `DeliverInputAsync`.
- `DownloadAttachmentsAsync` against WireMock: success writes under `.attached/` with the
  `.gitignore`; a 404, a `Content-Length` over the cap, an over-cap body without `Content-Length`,
  and an escaping `Content-Disposition` each end the fetch naming the id, leaving no partial
  file; earlier files stay. Containment: a pre-existing symlink at `.attached` fails before any
  write; a pre-existing symlink at the target file name is not followed (the next unique name is
  used); a directory swapped for a link after the write (the test swaps it from the fetch's
  post-write hook) deletes the written file and fails with "attachment directory was replaced".
- `DeliverInputAsync`: a failed fetch is a `DeliveryFailed` drop with the id in the error, and the
  runtime receives nothing; a successful fetch delivers `text + "\n\n" + trailer`.
- `PtyHostedAgentRuntime`: a paste is followed by one CR no earlier than 150 ms later
  (`FakeTimeProvider`); raw input issued before, during and after `SendUserInputAsync` lands
  entirely before the `ESC[200~` or entirely after the CR, in issue order, with every byte
  accounted for; `SendInterrupt` during a paste is not delayed.
- Launch: a failed fetch fails the launch with `attachment_unavailable`, removes the worktree, and
  starts no process.
- `LocalControlCapabilities.Current` contains `input/2`, and the routing switch handles frame 24
  (the existing pin, extended).

**App** (`Capacitor.App.Tests.Unit`, `[NotInParallel("AvaloniaSession")]` where a VM or view is built)
- `AttachmentTray`: `AddAll` stages up to the cap and names the rest; dedups names; `Snapshot` is
  a copy; `ContentEquals`; `Restore` replaces; `Clear`.
- `AttachmentIntake.Classify`: files beat text beats bitmap; nothing → `Nothing`.
  `ReadFilesAsync`: a folder, an oversize file (by reported size, and by stream length with no
  size), and an unreadable file are each refused with their wording while valid siblings are
  accepted, in order; content types assigned. `FromBitmap` yields a PNG named by the fake clock.
- `AttachmentDropPaste` with a fake `IAsyncDataTransfer`: disposed exactly once for `Files`,
  `Text`, `Bitmap`, `Nothing`, a thrown read, cancellation, and an unload mid-read; the bitmap is
  disposed after encoding; `Text` reaches the TextBox once (the re-entrancy guard); a second paste
  during an intake is dropped; `CanAttach` false pastes text and refuses files without reading.
- `ServerAttachmentUploader` against WireMock: multipart shape (field name, file name, content
  type per part); 200 with one id per file → `Uploaded` in order; 200 with zero, fewer, more,
  null, blank, malformed or duplicate ids, or a non-JSON body → `Rejected`; 400 → `Rejected` with
  body; 401 → `Unauthorized`; refused connection → `Unreachable`; `Ids` empty on every failure.
- `ChatTabViewModel`: a send with attachments uploads first and passes the ids to the channel;
  an upload failure sends nothing and keeps text and chips; `CanAttach` false refuses before the
  upload; an accepted send clears text and chips together; a chip added mid-flight survives;
  `Matches` acknowledges a transcript user turn carrying the trailer.
- `TerminalChatInput`: no ids → `TrySendText`, synchronous, unchanged; ids → one frame-24
  exchange with `CanAcceptText` false meanwhile, cleared on `Ok`; `CanAttach` follows `input/2`
  and `work_location`. `LocalFrameChatInput`: `attachments_refused` maps to the hint.
- `HomeViewModel`: `CanAttach` follows sign-in, `input/2` for the local machine and the version
  gate for a remote one (below, equal, above, unparsable, missing); files → upload →
  `LaunchRequest.AttachmentIds`; upload failure → `StartError`, no launch; `Started` clears the
  draft and retains it; `LaunchFailed` before registration (buffered), after the request
  returned, and after the TTL; a row arriving before registration; the draft restored into an
  empty composer and not into an edited one, with the two messages; `LaunchPayload.For` emits
  `attachment_ids` only when non-empty.
- Headless smoke: the chip strip renders one chip per staged file and removing one updates it;
  a drop of a file on the card stages it; a `PastingFromClipboard` raise with the headless
  clipboard holding text pastes text once. Bitmap paste is verified by hand on macOS, where the
  clipboard carries TIFF and PNG, and noted in the PR.

## 8. Out of scope

- A composer for remote sessions (there is none yet; when it lands, its channel is the server's
  `SendUserInput(agentId, text, attachmentIds)` and this design's tray and uploader apply
  unchanged, gated on the same version rule as Home).
- Rendering attachments as thumbnails inside Chat turns, and rendering the agent's own image output.
- Vendor-native image inputs (decision 3).
- A `kcap agent send --attach` verb.
- Attachments from the web into an in-place agent (decision 5 makes the refusal visible; enabling
  it needs a daemon-owned scratch location and is its own decision).
- Restoring a text-only launch's goal on failure.

## Risks

- **A vendor that cannot read a file by path.** The trailer is the contract; Claude and Codex are
  verified by hand in the PR against a PNG and a PDF. An ACP agent without a file tool reads
  nothing, which is what the web already gives it.
- **The 150 ms submit delay** is calibrated to today's Codex suppression window; a TUI that widens
  it turns a frame send into a newline. Same exposure the app's terminal channel carries, and
  now in one constant with one measurement behind it.
- **The containment window** in §4 is real and named; closing it needs a platform primitive .NET
  does not expose.
- **The version gate** is only as good as the constant: a daemon released between the gate's
  version and the fail-closed fetch does not exist, because both ship in one PR, but a hotfix
  branch that back-ports one without the other would reopen the gap. The PR notes this.
- **Memory.** Ten 10 MiB files staged is 100 MiB held until Send, and a launch draft holds its
  copy for up to 10 minutes more; a user who stages and walks away holds it indefinitely.
  Accepted for parity with the web composer; a tray is cleared when its workspace is torn down.
