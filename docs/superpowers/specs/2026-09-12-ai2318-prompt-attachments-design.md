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
one trailing field on the local input frame, and a few daemon rules the local lane needs.

## Decisions

1. **Bytes travel through the server's temp attachment store on every lane.** The app uploads
   the staged files to `POST /api/attachments/upload` at send time, receives ids, and sends ids:
   on `RequestLaunchAgentV2.attachment_ids` for a launch (the payload already declares it), and
   on a trailing `attachment_ids` member of the local `SendText` frame for a chat send. The
   daemon's existing `DownloadAttachmentsAsync` does the rest. This is the one mechanism that
   already ships bytes to a daemon on another machine, it is what the web uses, its 10 MB cap is
   enforced server-side, and it leaves nothing new to sweep. Rejected: bytes over the local
   socket (a new chunked frame family, a second store with its own lifetime, no remote story,
   and an 8 MB frame ceiling under the server's 10 MB file cap); the app writing straight into
   the worktree (it knows `worktree_path` only for local agents, and a second writer in a
   daemon-owned tree is a race with cleanup).
2. **A send that carries attachments rides the `SendText` frame for every vendor, PTY included.**
   Text-only PTY sends stay on the attach `Stdin` path unchanged. The frame is vendor-agnostic
   already (`PtyHostedAgentRuntime.SendUserInputAsync` is the bracketed paste the web uses); the
   daemon has to download and name the files, so the daemon has to compose the message. Rejected:
   moving all PTY input onto the frame (out of AI-2197's scope for a reason — the terminal send
   gate's semantics are tied to the attach lifecycle and nothing here needs them changed).
3. **The agent learns about a file by path, the same way on every vendor.** The daemon's
   `[Attached files: …]` trailer names each file relative to the agent's cwd. Claude Code reads
   an image or a PDF through its `Read` tool; Codex through `view_image` and its shell; ACP, Pi
   and Antigravity agents through their own file tools. Rejected: vendor-native image inputs
   (`codex --image` at launch, ACP `image` content blocks) — a per-vendor capability matrix for
   a result the path already gives, and a second delivery shape to keep in step with the first.
4. **An attachment that cannot be delivered fails the send; it is never silently dropped.** Today
   both paths are best-effort: a missing id logs a warning and the text goes out without it. A
   user who attached a file meant the file, and text without it changes meaning. A fetch failure
   (404, oversize, IO) is a `delivery_failed` drop naming the id on both callers, and a launch
   whose attachments cannot be fetched fails with `attachment_unavailable`. This is a change for
   the web path as well, deliberately: the drop is reported through `ReportInputDroppedAsync`,
   which the web already renders.
5. **Attachments are refused for an agent that does not run in a daemon-owned worktree.** The
   launch path already guards on `WorkLocation.OwnedWorktree`; the send path does not, so a
   follow-up attachment to an in-place agent would write `.attached/` into the user's own
   checkout. The daemon refuses with a coded reason, and the app hides the affordance when the
   status dto's `work_location` is not the owned worktree.
6. **Capability `input/2` gates the field.** An older daemon's JSON decoder ignores an unknown
   member, so `attachment_ids` sent to it would deliver the text and lose the files without a
   word. The app never sends ids to a daemon that does not advertise `input/2`; the affordance
   is disabled with a hint that names the fix.
7. **Text is required; attachments are additive.** Send and Start stay gated on non-blank text.
   The daemon already refuses an empty text, and a placeholder invented by the app to carry a
   lone image would be the app putting words in the user's mouth. The hint says what is missing.
8. **Limits mirror the server's.** 10 MiB per file (the server's `MaxFileSize`, refused by the
   app before upload with the same wording), 10 files per prompt, any content type. The agent's
   own tools decide what it can read; a type blocklist maintained here buys nothing, and the web
   has none.
9. **Staged bytes live in the app's memory until Send.** The server's temp store expires an
   upload after 10 minutes, so uploading on paste would strand a chip the user takes their time
   over. At most 100 MiB by the limits above, the same shape the web's Blazor composer holds.
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
one intake and cleared by the next edit or intake: "`<name>` is over 10 MB", "up to 10 files per
message", "folders can't be attached", "attachments need the daemon updated", "attachments
aren't available for an in-place session", "sign in to attach files". Nothing toasts.

While sending, the hint reads "Uploading 2 files…" then "Sending…" (Chat) or the Start button
is disabled with "Uploading…" (Home). On success the text and the chips clear together. On any
failure both stay, and the hint says why. The user's own turn appears in Chat from the
transcript as it does today, with the daemon's trailer on it — the same text the web shows.

## 2. App

### Staging: `AttachmentTray`

One `ReactiveObject` owned by the prompt's view model (`ChatTabViewModel`, `HomeViewModel`):

```csharp
public sealed class AttachmentTray : ReactiveObject {
    public ReadOnlyObservableCollection<StagedAttachment> Items { get; }
    public int Count { get; }
    public long TotalBytes { get; }
    /// Null on success; otherwise the wording the prompt shows.
    public string? TryAdd(StagedAttachment file);
    public void Remove(StagedAttachment file);
    public IReadOnlyList<StagedAttachment> Snapshot();
    public void Clear();
}

public sealed record StagedAttachment(string FileName, string ContentType, ReadOnlyMemory<byte> Bytes) {
    public string SizeLabel { get; }
    public bool IsImage { get; }   // ContentType starts with "image/"
}
```

`TryAdd` enforces the two limits from `InputWire` (§3) and dedups a file name against the tray by
appending ` (2)`, ` (3)` before the extension, so two screenshots pasted in one second are two
chips and two files. The thumbnail is a `StagedAttachmentViewModel` concern — decoded lazily on a
worker, published on the UI thread, disposed with the chip.

### Intake: `AttachmentIntake`

A static classifier the views call, pure over Avalonia's data-transfer types so it is unit-testable
without a clipboard:

```csharp
public static class AttachmentIntake {
    public static IntakeKind Classify(IReadOnlyList<DataFormat> formats, bool hasNonBlankText);
    public static Task<IReadOnlyList<StagedAttachment>> ReadFilesAsync(IEnumerable<IStorageItem> items, CancellationToken ct);
    public static StagedAttachment FromBitmap(Bitmap bitmap, TimeProvider time);
    public static string ContentTypeFor(string fileName);
}
public enum IntakeKind { Files, Text, Bitmap, Nothing }
```

`Classify` implements the precedence in §1. `ReadFilesAsync` skips an `IStorageFolder`
(reporting it, so the prompt can say "folders can't be attached") and reads an `IStorageFile`
through `OpenReadAsync`, capping the read at `MaxAttachmentBytes + 1` so an oversize file is
refused without being fully read. `FromBitmap` encodes PNG with `Bitmap.Save`. `ContentTypeFor`
is a small extension table (png, jpg/jpeg, gif, webp, pdf, txt, md, json, csv, log, xml, yaml/yml,
common source extensions → `text/plain`), defaulting to `application/octet-stream`.

### View wiring: `AttachmentDropPaste`

An attached behaviour applied to both cards, so `ChatTabView` and `LauncherPaneView` register the
same code once:

- **Paste**: a handler on the TextBox's `PastingFromClipboard`. The event is synchronous and the
  clipboard read is not, so the handler always marks the event handled, then reads
  `TopLevel.Clipboard.TryGetDataAsync()` on the UI thread. `Files` → `ReadFilesAsync` → tray.
  `Bitmap` → `TryGetBitmapAsync` → `FromBitmap` → tray. `Text` → set a re-entrancy flag and call
  `TextBox.Paste()`, whose second raise of the event the handler lets through. `Nothing` → no-op.
  Every branch runs on the dispatcher; a `TextBox` that has been unloaded since the paste began
  drops the result.
- **Drop**: `DragDrop.SetAllowDrop(card, true)`; `DragOver` sets `DragDropEffects.Copy` iff
  `e.DataTransfer.Contains(DataFormat.File)`, else `None`; `Drop` → `TryGetFiles()` →
  `ReadFilesAsync` → tray. Enter/Leave toggle a `dragOver` pseudo-class on the card for the
  border colour.
- **Pick**: the "+" button → `OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true })`
  → `ReadFilesAsync` → tray.

The behaviour talks to the tray through a small `IAttachmentSink` (`TryAdd`, `Report(string)`),
which each view model exposes, so the view never reaches into a VM's internals.

### Upload: `IAttachmentUploader`

```csharp
public interface IAttachmentUploader {
    Task<UploadOutcome> UploadAsync(IReadOnlyList<StagedAttachment> files, CancellationToken ct);
}
public sealed record UploadOutcome(UploadKind Kind, IReadOnlyList<string>? Ids, string? Reason);
public enum UploadKind { Uploaded, Unauthorized, Rejected, Unreachable }
```

`ServerAttachmentUploader` follows `ServerSessionHttp` exactly: a client leased per call from
`ICapacitorHttpClient.ForWaitAsync`, `AuthStatus` other than `Ok`/`NoAuthRequired` →
`Unauthorized`; one `MultipartFormDataContent` with each file as a `ByteArrayContent` named
`files` carrying its `ContentType` and file name, posted to `{server}/api/attachments/upload`;
200 → ids in the response order (`UploadedAttachment[]`), 400 → `Rejected` with the body, 401 →
`Unauthorized`, anything else or a transport exception → `Unreachable`. Nothing throws past
cancellation. Timeout: the leased client's own.

The self-scoped route rather than `/api/agents/{agentId}/attachments`: the daemon downloads with
its own account's token, and the store admits a download when the downloader owns the upload —
which holds for every send the desktop composer can make (a local socket is the owner's; a remote
daemon is one of the signed-in user's own). The agent-scoped route adds a visibility gate that
only matters for a non-owner writing into someone else's agent, which the desktop never does. If
the local daemon is bound to a different server or account than the app's profile, the download
404s and the send fails with the id named (decision 4) — visible, not silent.

### Chat send

`ChatInput.SendAsync` takes the ids:

```csharp
public abstract Task<ChatSendOutcome> SendAsync(string text, IReadOnlyList<string> attachmentIds, CancellationToken ct);
/// Whether a send with attachments can be accepted now; false carries a reason in AttachHint.
public abstract bool CanAttach { get; }
public abstract string? AttachHint { get; }
```

- `LocalFrameChatInput`: `CanAttach` iff `Availability == Ready`, the daemon advertises `input/2`,
  and `Dto.WorkLocation` is the owned worktree. `SendAsync` passes the ids through
  `ILocalControlOps.SendTextAsync(agentId, text, attachmentIds, ct)`; a refused ack maps the new
  reason (§3) to the hint like the existing ones.
- `TerminalChatInput` gains the same inputs (`agentId`, `IDaemonClientService`, `ILocalControlOps`,
  presence) and the same `CanAttach` rule. `SendAsync` with an empty id list is today's
  synchronous `TrySendText`. With ids it is one `SendText` exchange: `_sending` closes
  `CanAcceptText` for its duration so the terminal channel cannot interleave a paste, and the
  outcome maps as `LocalFrameChatInput` maps it, clearing on `Ok`. The frame path for a PTY runtime
  is the daemon's bracketed paste plus its own submit (§4), so the app sends no CR of its own.

`ChatTabViewModel.SendCommand` does the upload, once, before the channel:

1. Snapshot text, edit count and the tray (`Snapshot()`).
2. If the snapshot has files and `!_input.CanAttach`, refuse with `_input.AttachHint`; nothing is
   sent.
3. If it has files, set `Uploading` (hint "Uploading N files…", `canSend` false), call the
   uploader with the lifetime token. `Unauthorized` → hint "sign in to attach files";
   `Rejected`/`Unreachable` → hint with the reason. Any failure keeps text and chips and stops.
4. `_input.SendAsync(text, ids, ct)`. `Accepted` clears the text (existing rule: only if the
   snapshot is still the draft) **and clears the tray iff its contents still equal the snapshot**
   — a file added during the round trip stays. `Rejected` and `Unconfirmed` keep both.

`canSend` adds `!Uploading`. `QueuedChatMessage.Matches` accepts a transcript text that equals the
sent text, or equals it followed by the daemon's trailer (`AttachmentTrailer.Prefix` from Core,
§3, preceded by the blank line) — otherwise a send with attachments would never be acknowledged
and the queued banner would never clear.

### Launch

`LaunchRequest` gains `IReadOnlyList<string>? AttachmentIds`; `LaunchPayload.For` copies it to
the payload's existing `attachment_ids` (null when empty, so the wire an older server expects is
untouched). `HomeViewModel.StartAsync`, after the remote ownership check and before building the
request: if the tray has files, `Uploading = true` (Start disabled, label "Uploading…"), upload;
`Unauthorized` → the existing `_signInRequired` signal; other failures → `StartError` with the
reason, nothing launched. On `Started`, `Goal = ""` and `Tray.Clear()` together. A launch that
later fails with `attachment_unavailable` arrives as a `LaunchFailed` broadcast and renders through
`FriendlyLaunchFailure` like every other launch failure: "the attached files could not be
delivered to the machine — try again".

Home never asks the daemon anything: a launch always goes through the server, always into an
owned worktree, so decisions 5 and 6 do not apply to it. `CanAttach` for Home is "signed in".

## 3. Wire

### Local input frame (append-only)

`SendTextDto` gains a trailing member; the frame type and ack are unchanged:

```csharp
public sealed record SendTextDto(string AgentId, string Text, string[]? AttachmentIds = null);
```

`{"agent_id","text","attachment_ids"}`. Always emitted by a current client (an empty array for no
attachments), null from an older one. `InputWire.IsStructurallyValid` is unchanged — null is
structurally fine; emptiness and count are the handler's call.

New constants beside `MaxTextBytes`:

```csharp
public static class InputWire {
    public const int  MaxTextBytes            = 256 * 1024;
    public const int  MaxAttachmentsPerPrompt = 10;
    public const long MaxAttachmentBytes      = 10L * 1024 * 1024;  // the server's per-file cap
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
recognises it from one definition.

Capability: `"input/2"` appended to `LocalControlCapabilities.Current`, meaning "the `SendText`
handler reads `attachment_ids`". `input/1` stays, so an older app's gate is unchanged.

`ILocalControlOps.SendTextAsync(string agentId, string text, IReadOnlyList<string> attachmentIds,
CancellationToken ct)`; the existing two-argument form is removed — the app is its only caller.

### Server

No change. `RequestLaunchAgentV2` already admits and grants `attachment_ids`; the temp store, the
10 MB cap, the 10-minute TTL and the owner-or-granted download rule are what this design leans on.

## 4. Daemon

### `AnswerSendTextAsync`

After the existing text checks and before `DeliverInputAsync`:

- `attachment_ids` longer than `MaxAttachmentsPerPrompt`, or containing a null, blank or
  malformed id → `attachments_refused`, Error "up to 10 attachments per message" / "malformed
  attachment id".
- non-empty ids and `agent.Work != WorkLocation.OwnedWorktree` → `attachments_refused`, Error
  "attachments need a daemon-owned worktree".

Then `DeliverInputAsync(agent, dto.Text, dto.AttachmentIds)` — the `null` the local lane passes
today becomes the dto's ids.

### `DeliverInputAsync` and `DownloadAttachmentsAsync`

`DownloadAttachmentsAsync` returns a result rather than the paths it managed:

```csharp
sealed record AttachmentFetch(IReadOnlyList<string> RelativePaths, string? FailedId, string? Error);
```

Per id, in order: a non-success status, a `Content-Length` over `MaxAttachmentBytes`, a body that
exceeds it while streaming (the copy is capped at `MaxAttachmentBytes + 1` bytes and the partial
file deleted), an escaping file name, or an IO exception ends the fetch with that id and error;
files already written stay (they are inside `.attached/`, gitignored, and go with the worktree).
The trailer is composed only when every id landed.

`DeliverInputAsync`: a failed fetch is `InputDeliveryOutcome.Drop(SendInputDropReason.DeliveryFailed,
$"attachment {id} unavailable: {error}")`. Both callers already route that reason — the server
caller through `ReportInputDroppedAsync`, the local one into the ack. It also gains the work
location guard as defence in depth (the handler refuses first; this catches the server caller):
ids on a non-owned work location are the same `DeliveryFailed` drop with "attachments need a
daemon-owned worktree".

The message is `text` alone, or `text + "\n\n" + AttachmentTrailer.For(paths)`.

### Launch

The launch path's fetch stays where it is, guarded by `OwnedWorktree`, and stops being
best-effort: a failed fetch throws, the existing failure path removes the worktree, and the
server sees `LaunchFailed` with `attachment_unavailable: <id>`. Ids on a non-owned launch (only
possible from a server that ignored its own contract) fail the same way with
`attachments_refused`.

### PTY submit delay

`PtyHostedAgentRuntime.SingleSubmitDelay` goes from 50 ms to 150 ms. The interactive Codex TUI
treats an Enter within about 120 ms of a paste as a newline, and the app's own terminal channel
already waits 150 ms for that reason; the frame path now carries the app's PTY sends and must
clear the same window. The approvals-disabled spray schedule is unchanged.

## 5. Compatibility

| App | Daemon | Server | Behaviour |
|---|---|---|---|
| new | new (`input/2`) | any | full: chips, paste, drop; chat sends with ids over `SendText`; launch ids over V2 |
| new | old (no `input/2`) | any | chat "+", paste-as-file and drop are refused with "attachments need the daemon updated"; text sends unchanged; Home launch attachments work (server path) |
| old | new | any | old `SendTextDto` has no `attachment_ids`; decodes as null; text-only as today |
| new | any | old (no V2 attachments) | V2 has carried `attachment_ids` since hosted agents shipped; not a live case |
| new | new | temp store id expired (>10 min between upload and fetch) | fetch 404 → send refused / launch failed with the id named; the user re-sends |

`work_location` null on a PTY dto (a daemon older than that field) also predates `input/2`, so it
takes the "daemon updated" hint, never a false "in-place".

## 6. Security and privacy

- The app uploads with the signed-in profile's token through the same client lease every other
  app HTTP call uses; the daemon downloads with its own — no new credential path, and the daemon
  still looks for none.
- Files land only inside `<owned worktree>/.attached/`, gitignored, excluded from borrowed
  snapshots, removed with the worktree at cleanup. Decision 5 closes the one path that could have
  written outside it.
- File names are already reduced to `Path.GetFileName` and containment-checked; the byte cap is
  new and closes an unbounded stream from a compromised or misconfigured server.
- Bytes sit on the server for at most 10 minutes and are never part of the session record; an
  attached text file's contents reach a transcript only if the agent reads it, at which point the
  watcher's redaction applies as to any tool result. `SecretRedactor` is not run over attachments:
  it rewrites JSON transcript lines, and a file is neither.
- The clipboard is read only on an explicit paste gesture, never polled.

## 7. Testing

**Core** (`Capacitor.Cli.Core.Tests.Unit`)
- `InputIpc`: `SendTextDto` round-trips with and without `attachment_ids`; a payload lacking the
  member decodes to null; `IsStructurallyValid` unchanged.
- `AttachmentTrailer.For` shapes one, two and many paths; `Prefix` matches.
- `LocalControlOps.SendTextAsync` serialises the ids as `attachment_ids` and an empty list as `[]`.

**Daemon** (`Capacitor.Cli.Daemon.Tests.Unit`)
- `AnswerSendTextAsync`: over-count, malformed id and non-owned work location each refuse with
  `attachments_refused` and the stated error before the delivery core runs; a valid id list
  reaches `DeliverInputAsync`.
- `DownloadAttachmentsAsync` against WireMock: success writes under `.attached/` with the
  `.gitignore`; a 404, a `Content-Length` over the cap, an over-cap body without `Content-Length`,
  and an escaping `Content-Disposition` each end the fetch naming the id, leaving no partial
  file; earlier files stay.
- `DeliverInputAsync`: a failed fetch is a `DeliveryFailed` drop with the id in the error, and the
  runtime receives nothing; a successful fetch delivers `text + "\n\n" + trailer`; a PTY runtime
  receives it as one bracketed paste followed by one CR no earlier than 150 ms later
  (`FakeTimeProvider`).
- Launch: a failed fetch fails the launch with `attachment_unavailable`, removes the worktree, and
  starts no process.
- `LocalControlCapabilities.Current` contains `input/2`, and the routing switch handles `SendText`
  (the existing pin, extended).

**App** (`Capacitor.App.Tests.Unit`, `[NotInParallel("AvaloniaSession")]` where a VM or view is built)
- `AttachmentTray`: caps by count and bytes with the stated wording; dedups names; `Snapshot` is
  a copy; `Clear`.
- `AttachmentIntake.Classify`: files beat text beats bitmap; nothing → `Nothing`.
  `ReadFilesAsync` skips a folder and reports it, refuses an oversize file without reading it
  through, assigns content types. `FromBitmap` yields a PNG named by the fake clock.
- `ServerAttachmentUploader` against WireMock: multipart shape (field name, file name, content
  type per part), 200 → ids in order, 400 → `Rejected` with body, 401 → `Unauthorized`, refused
  connection → `Unreachable`.
- `ChatTabViewModel`: a send with attachments uploads first and passes the ids to the channel;
  an upload failure sends nothing and keeps text and chips; `CanAttach` false refuses before the
  upload; an accepted send clears text and chips together; a chip added mid-flight survives;
  `Matches` acknowledges a transcript user turn carrying the trailer.
- `TerminalChatInput`: no ids → `TrySendText`, synchronous, unchanged; ids → one `SendText`
  exchange with `CanAcceptText` false meanwhile, cleared on `Ok`; `CanAttach` follows `input/2`
  and `work_location`. `LocalFrameChatInput`: `attachments_refused` maps to the hint.
- `HomeViewModel`: files → upload → `LaunchRequest.AttachmentIds`; failure → `StartError`, no
  launch; success clears goal and tray; `LaunchPayload.For` emits `attachment_ids` only when
  non-empty.
- Headless smoke: the chip strip renders one chip per staged file and removing one updates it;
  a drop of a file on the card stages it; a `PastingFromClipboard` raise with the headless
  clipboard holding text pastes text once (the re-entrancy guard). Bitmap paste is verified by
  hand on macOS, where the clipboard carries TIFF and PNG, and noted in the PR.

## 8. Out of scope

- A composer for remote sessions (there is none yet; when it lands, its channel is the server's
  `SendUserInput(agentId, text, attachmentIds)` and this design's tray and uploader apply unchanged).
- Rendering attachments as thumbnails inside Chat turns, and rendering the agent's own image output.
- Vendor-native image inputs (decision 3).
- A `kcap agent send --attach` verb.
- Attachments from the web into an in-place agent (decision 5 makes the refusal visible; enabling
  it needs a daemon-owned scratch location and is its own decision).

## Risks

- **A vendor that cannot read a file by path.** The trailer is the contract; Claude and Codex are
  verified by hand in the PR against a PNG and a PDF. An ACP agent without a file tool reads
  nothing, which is what the web already gives it.
- **The 150 ms submit delay** is calibrated to today's Codex suppression window; a TUI that widens
  it turns a frame send into a newline. Same exposure the app's terminal channel carries, and
  now in one constant with one measurement behind it.
- **Paste re-entrancy.** The guard flag must be cleared on every path out of the handler,
  including a thrown clipboard read, or the next paste is swallowed. The test pins the text case;
  the handler's `finally` clears it.
- **Memory.** Ten 10 MiB files staged is 100 MiB held until Send; a user who stages and walks
  away holds it indefinitely. Accepted for parity with the web composer; a tray is cleared when
  its workspace is torn down.
