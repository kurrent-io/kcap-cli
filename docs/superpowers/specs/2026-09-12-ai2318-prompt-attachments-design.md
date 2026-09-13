# Attachments in the desktop prompts: paste, drop and pick files (AI-2318)

Slice of the desktop shell (parent AI-2171). Two prompts take text today and nothing else: the
Home launcher's goal box and the session Chat composer. Pasting a screenshot into either is a
silent no-op, because the composer is a plain Avalonia `TextBox` and every send path below it
carries a string. This spec gives both prompts attachments — paste an image or files from the
clipboard, drop files onto the box, or pick them with a "+" button — shown as removable chips
and delivered to the agent as files it can read.

The daemon already knows how to deliver an attachment. A server-origin `SendInput` and a
server-origin launch both carry `attachment_ids`; the daemon fetches each id from the server's
temp store into `<worktree>/.attached/` and appends `[Attached files: …]` to the prompt. The web
composer uses exactly this. What is missing is everything above it in the app, one new local
input frame, and a few daemon rules the local lane and the fail-closed contract need.

## Decisions

1. **Bytes travel through the server's temp attachment store on every lane.** The app uploads
   the staged files to `POST /api/attachments/upload` at send time, receives ids, and sends ids:
   on `RequestLaunchAgentV2.attachment_ids` for a launch (the payload already declares it), and
   on a new local frame for a chat send. The daemon's existing `DownloadAttachmentsAsync` does the
   rest. This is the one mechanism that already ships bytes to a daemon on another machine, it is
   what the web uses, its 10 MB cap is enforced server-side, and the bytes' lifetime on the server
   is already someone else's problem. Rejected: bytes over the local socket (a new chunked frame
   family, a second store with its own lifetime, no remote story, and an 8 MB frame ceiling under
   the server's 10 MB file cap); the app writing straight into the worktree (it knows
   `worktree_path` only for local agents, and a second writer in a daemon-owned tree is a race
   with cleanup).
2. **A send that carries attachments rides a frame for every vendor, PTY included.** Text-only
   PTY sends stay on the attach `Stdin` path unchanged. The daemon has to download and name the
   files, so the daemon has to compose the message; `PtyHostedAgentRuntime.SendUserInputAsync` is
   the bracketed paste the web already drives. Rejected: moving all PTY input onto a frame (out of
   AI-2197's scope for a reason — the terminal send gate's semantics are tied to the attach
   lifecycle and nothing here needs them changed).
3. **The agent learns about a file by path, the same way on every vendor.** The daemon's
   `[Attached files: …]` trailer names each file: relative to the cwd when the file is in the
   worktree, absolute otherwise (decision 4). Claude Code reads an image or a PDF through its
   `Read` tool; Codex through `view_image` and its shell (its sandboxes restrict writes, not
   reads); ACP, Pi and Antigravity agents through their own file tools, some of which confine
   themselves to the workspace — which is why those vendors keep the file in the workspace.
   Rejected: vendor-native image inputs (`codex --image` at launch, ACP `image` content blocks) —
   a per-vendor capability matrix for a result the path already gives, and a second delivery shape
   to keep in step with the first.
4. **Where the file lands follows the agent's containment.** Today every attachment goes to
   `<worktree>/.attached/`. A directory inside the agent's own tree is one the agent can replace
   with a link before the daemon writes, and the daemon is not sandboxed. For an agent that can
   already write anywhere the daemon can — Claude, the ACP vendors, Pi and Antigravity in a
   default-kind launch, none of which runs under an OS write sandbox — that is no escalation, and
   the worktree is the one place their file tools are sure to read. For a **write-contained**
   runtime — Codex under its seatbelt or landlock sandbox — it is an escalation: the sandbox stops
   the agent writing outside its cwd, and a steered daemon write would do it for the agent. So
   each runtime factory declares an `AttachmentPlacement`: `Worktree` (the default, today's
   behaviour, relative path in the trailer) or `DaemonStore` (Codex: a per-agent directory under
   the daemon's state dir, outside any cwd, absolute path in the trailer, readable under both
   Codex sandboxes). Protected kinds (reviewers, flow participants) are refused input before any
   of this. Rejected: the daemon store for every vendor (regresses the web path for a
   workspace-confined file tool, which today reads `.attached/` and would read nothing); no-follow
   check-then-create over a pathname (racy by construction, and unnecessary once the contained
   runtime is elsewhere); a native `openat`/`NtCreateFile` shim (correct, and a platform surface
   to maintain for a problem the placement rule removes).
5. **An attachment that cannot be delivered fails the send, leaves nothing behind, and is never
   silently dropped.** Today both daemon paths are best-effort: a missing id logs a warning and
   the text goes out without it, and files already fetched stay. A user who attached a file meant
   the file, and text without it changes meaning. A fetch failure (404, oversize, IO) is a
   `delivery_failed` drop naming the id on both callers, a launch whose attachments cannot be
   fetched fails with `attachment_unavailable`, and a batch is published only whole: the files
   are fetched into a staging directory and moved into place after the last one lands, or the
   staging directory is removed. This changes the web path as well, deliberately: the drop is
   reported through `ReportInputDroppedAsync`, which the web already renders.
6. **Delivery to an older daemon fails closed at the byte level on the chat lane; the launch lane
   is gated on what the app can see, and its one gap is named.** A chat send with ids uses a new
   frame type, `SendTextWithAttachments`, which an older daemon's codec rejects before routing —
   the fail-closed contract `FrameType` already documents. The reserved alternative (a trailing
   `attachment_ids` on `SendTextDto`) is rejected: an older decoder ignores an unknown member,
   delivers the text and loses the files without a word, and a capability check on the status
   connection does not cover the one-shot socket the send opens. The affordance itself is gated
   so the user is told before, not after: Chat on `input/2` from the local daemon's hello; Home
   on the target daemon's advertised version for a remote machine (`DaemonInfo.Version`, the one
   capability signal the server relays) and on `input/2` for the local one. The version gate is
   on kcap's own daemon release, not a vendor build, so the borrowed-review invariant about
   version gating does not apply. **The gap:** a launch is dispatched by the server after the
   app's check, and the server enforces no daemon version; a daemon downgraded between the check
   and the dispatch is a pre-change daemon and delivers the launch best-effort. That is an
   operator downgrading their own machine inside a window of seconds, and this spec accepts it
   rather than adding a server-side capability contract for it. It is the only case where a file
   the user attached can be dropped without a message.
7. **Chat requires text; a launch does not.** The daemon refuses an empty chat text, and a
   placeholder invented by the app to carry a lone image would be the app putting words in the
   user's mouth, so Send stays gated on non-blank text and the hint says so. Home's Start is not
   gated on the goal today — an empty goal is a supported launch — and stays that way: a launch
   with attachments and no goal is allowed, and the daemon already renders it as the trailer
   alone.
8. **Limits mirror the server's.** 10 MiB per file (the server's `MaxFileSize`, refused by the
   app before upload with the same wording), 10 files per prompt, any content type. The agent's
   own tools decide what it can read; a type blocklist maintained here buys nothing, and the web
   has none.
9. **Staged bytes live in the app's memory until Send, and a launch's bytes until the launch
   settles or the server's copy has expired.** The server's temp store expires an upload after
   10 minutes, so uploading on paste would strand a chip the user takes their time over. A launch
   is only *accepted* when the hub returns; its attachments are fetched later by the daemon, so
   the sent draft is retained until the launch is confirmed or fails, and restored on failure
   when that is safe (§2). One retained draft at a time, at most 100 MiB by the limits above.
10. **Recall restores text only.** The arrow-key prompt history is a text history; a recalled
    prompt does not re-stage files that were already delivered.
11. **Confidentiality between agents of one user is not a boundary here, as it is not anywhere
    in the daemon.** Every hosted agent runs as the daemon's OS user and can read every other
    agent's worktree under `<repo>/.capacitor/worktrees/` today, `.attached/` included; the
    daemon-store directory is no more and no less readable. What this design protects is the
    *integrity* of where a file lands (decision 4), not who else on the same account can read
    it. The hashed directory names are for path safety — an agent id crosses the wire
    unconstrained — not secrecy, and the state directory's mode is not relied on.

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
daemon is older), "sign in to attach files". When the "+" is disabled its tooltip carries the
same text. Nothing toasts.

While sending, the hint reads "Uploading 2 files…" then "Sending…" (Chat) or the Start button
is disabled with "Uploading…" (Home). On success the sent text and the sent chips clear; anything
typed or added meanwhile stays. On any failure both stay, and the hint says why. A launch
failure that arrives after the hub accepted the request restores the draft when that is safe
(§2, Launch). The user's own turn appears in Chat from the transcript as it does today, with the
daemon's trailer on it — the same text the web shows.

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

`AddAll` is the one boundary every source passes through, so it enforces both limits from
`InputWire` (§3) whatever the source: a file over `MaxAttachmentBytes` is refused "is over 10 MB"
(intake refuses it earlier when it can, so it is never read, but a pasted bitmap whose PNG
encoding runs over the cap is caught here), and files past the tenth are refused "only 10 files
per message". A file name is deduplicated against the tray by appending ` (2)`, ` (3)` before the
extension, so two screenshots pasted in one second are two chips and two files. The thumbnail is
a `StagedAttachmentViewModel` concern — decoded lazily on a worker, published on the UI thread,
disposed with the chip.

### Intake: `AttachmentIntake`

A static classifier the views call, pure over Avalonia's data-transfer and storage types so it is
unit-testable without a clipboard:

```csharp
public static class AttachmentIntake {
    public static IntakeKind Classify(IReadOnlyList<DataFormat> formats, bool hasNonBlankText);
    public static Task<IntakeResult> ReadFilesAsync(IEnumerable<IStorageItem> items, CancellationToken ct);
    public static IntakeResult FromBitmap(Bitmap bitmap, TimeProvider time);
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
`IOException` on one item refuses that item ("could not be read") and the walk continues.
`FromBitmap` encodes PNG with `Bitmap.Save` into a capped buffer and refuses the result the same
way when it runs over; the caller disposes the `Bitmap` afterwards. Every result carries both
lists; the caller stages `Accepted` through `AddAll` and shows every refusal — intake's and the
tray's — as the one line described in §1. `ContentTypeFor` is a small extension table (png,
jpg/jpeg, gif, webp, pdf, txt, md, json, csv, log, xml, yaml/yml, common source extensions →
`text/plain`), defaulting to `application/octet-stream`.

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
staged file, in order, each `Id` valid by `InputWire.IsValidAttachmentId` (§3) and distinct
under `AttachmentIds.Canonical`**. Anything else — fewer or more elements, a null, blank,
malformed or repeated id, an unparsable body — is `Rejected` with "the server returned an
unexpected upload response", and the draft stays. `Ids` is empty on every non-`Uploaded`
outcome, never null.

The self-scoped route rather than `/api/agents/{agentId}/attachments`: the daemon downloads with
its own account's token, and the store admits a download when the downloader owns the upload —
which holds for every send the desktop composer can make (a local socket is the owner's; a remote
daemon is one of the signed-in user's own). The agent-scoped route adds a visibility gate that
only matters for a non-owner writing into someone else's agent, which the desktop never does. If
the local daemon is bound to a different server or account than the app's profile, the download
404s and the send fails with the id named (decision 5) — visible, not silent.

### Chat send

`ChatInput` gains the attachment channel:

```csharp
public abstract Task<ChatSendOutcome> SendAsync(string text, IReadOnlyList<string> attachmentIds, CancellationToken ct);
/// Whether a send with attachments can be accepted now; false carries the reason in AttachHint.
public abstract bool CanAttach { get; }
public abstract string? AttachHint { get; }
```

- `LocalFrameChatInput`: `CanAttach` iff `Availability == Ready` and the daemon advertises
  `input/2`; `AttachHint` names whichever fails. `SendAsync` with ids is one
  `SendTextWithAttachments` exchange through `ILocalControlOps.SendTextWithAttachmentsAsync`;
  without ids it is today's `SendTextAsync`. A refused ack maps `attachments_refused` to its
  `Error` text, like `delivery_failed`.
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

**The launch is built from one snapshot.** `StartAsync` first captures a `LaunchDraft` — machine,
repo, vendor, goal, model, effort, permission mode, the goal's edit count, and the tray's
`Snapshot()` — and every later step reads the draft, never the live properties. A selection the
user changes while the upload is in flight changes the next launch, not this one. After the
upload (if any) the remote ownership check and the capability gate run again **against the
draft's machine**, and the request is built from the draft. Order: capture → gate (`CanAttach` for
the draft's machine, when it has files) → upload (`Uploading = true`, Start disabled, label
"Uploading…"; `Unauthorized` → the existing `_signInRequired` signal; other failures →
`StartError` with the reason, nothing launched) → re-check ownership and gate for the draft's
machine → build and send. `ReactiveCommand.Execute` can bypass `CanExecute`, so the in-method
checks are the boundary, as they are today for ownership.

**The composer clears only what was sent.** On `Started`, `Goal` is cleared iff its edit count
still equals the draft's — the goal box stays editable during the upload and the hub call, and a
newer draft is the user's — and the tray is cleared iff `ContentEquals(draft.Files)`; otherwise
each keeps its newer contents. This is the rule Chat already applies. An accepted launch whose
returned id does not normalise (`UnusableIdMessage` today) cannot be correlated, so its draft is
not retained; the message gains " — re-attach the files if it did not start".

**The draft outlives the accepted request.** `LaunchOutcome.Started` is request acceptance, not
success — the daemon fetches the files later and a failure arrives as a `LaunchFailed`
broadcast. So every pending launch's entry records `HadAttachments` and the upload time beside
the timestamp it keeps today, and the most recent attachment-bearing launch additionally retains
its whole `LaunchDraft` (goal, files **and target**) as `_retainedDraft`, keyed by the normalised
agent id:

- **bytes for one launch at a time**: when a second attachment-bearing launch is *accepted*, its
  draft replaces the retained one; the first launch's entry keeps `HadAttachments`, so its later
  failure still reads as "re-attach" below rather than as a plain text failure. A launch that is
  refused before acceptance leaves the retained draft alone. Text-only launches never touch it;
- **retention is 10 minutes from the upload**, the server's own TTL for the bytes, checked on the
  app's `TimeProvider` tick as well as on every settlement, so a draft whose launch never reports
  is released without waiting for an event;
- a directory row confirms the launch (`ConfirmPendingRows`) → its entry and, if it is the
  retained one, the draft are discarded;
- a `LaunchFailed` for a pending id (`ApplyFailureIfPending`, or the buffered failure
  `RecordPendingLaunch` finds) → `StartError` is `FriendlyLaunchFailure(reason)`, the real
  reason, **never** rewritten as an attachment failure. Then, when that entry `HadAttachments`:
  if it is the retained draft, the composer is blank, the tray empty **and the current target
  (machine, repo, vendor) equals the draft's**, the goal and files are restored and the message
  gains " — your draft is back"; in every other attachment case it gains " — re-attach the files
  to send them again";
- a failure after the retention cutoff, `ForgetLaunch`, or a failure for an untracked id →
  discarded; a fetch that 404s because the server's copy expired lands here when the launch was
  that slow, and the user re-attaches.

Navigating away from Home while a launch is pending keeps the draft in the view model, which
outlives the view. `attachment_unavailable` from the daemon renders through
`FriendlyLaunchFailure` as "the attached files could not be delivered to the machine".

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

New constants and one validator beside `MaxTextBytes`, in `Capacitor.Cli.Core`:

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
public static class AttachmentIds {
    public static string Canonical(string id) => id.ToLowerInvariant();
    /// Null when the list is acceptable; otherwise the refusal text. Checks count, each id's
    /// syntax, and distinctness under Canonical. Every caller that will fetch runs this first.
    public static string? Validate(IReadOnlyList<string?>? ids);
}
public static class SendTextReasons {
    …
    public const string AttachmentsRefused = "attachments_refused";  // Error names why
}
public static class AttachmentTrailer {
    public const string Prefix = "[Attached files: ";
    public static string For(IEnumerable<string> paths);  // "[Attached files: a, b]"
}
```

`AttachmentIds.Validate` is the one definition of an acceptable id list: the uploader runs it
over a 200 response, and the daemon runs it in front of every fetch (§4), whichever lane the ids
arrived on.

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

### Placement

```csharp
internal enum AttachmentPlacement { Worktree, DaemonStore }
```

`IHostedAgentRuntimeFactory.AttachmentPlacement` — `Worktree` for every factory except Codex,
which declares `DaemonStore` because its runtime is write-contained by an OS sandbox (decision
4). The value is copied onto `AgentInstance` at launch so `DeliverInputAsync` reads it from the
agent, and the launch path reads it from the factory it is about to use. A runtime that ever
gains an OS write sandbox changes its one declaration and nothing else.

- `Worktree`: `<agent cwd>/.attached/` — created if absent with the `.gitignore` it has today;
  trailer paths relative (`.attached/x.png`). This is today's location and today's contract for
  every file tool that confines itself to the workspace.
- `DaemonStore`: `<DaemonStore.StateDirectory(config.Name)>/attachments/<AgentFileNames.For(agentId)>/`
  — created on first fetch; trailer paths absolute. Two daemons never collide, and no agent's
  cwd contains it. Removed in `CleanupAgentAsync` beside the worktree removal and on every
  launch-failure path that removes the worktree, through one `AttachmentStore.Remove(agentId)`
  (`DeleteTreeNoFollow`, absent is fine); a startup sweep removes directories whose agent has no
  live PID record, on the liveness predicate the journal sweep uses.

The `.attached/` exclusions and the reserved-path check in `WorktreeManager` are unchanged and
still load-bearing for the `Worktree` placement.

### Fetching: `DownloadAttachmentsAsync`

```csharp
sealed record AttachmentFetch(IReadOnlyList<string> Paths, string? FailedId, string? Error);
```

Every caller — the local frame handler, the server-origin `HandleSendInput` and the launch path —
runs `AttachmentIds.Validate` over the ids before this method is reached and treats a refusal as
the lane's failure (`attachments_refused` on the local ack, `DeliveryFailed` for the server
caller, `attachments_refused` as the launch failure), so no id that is not a Guid-N is ever
interpolated into `/api/attachments/{id}`, and no caller can request more than ten fetches.

**A batch is published whole or not at all.** The files are fetched into a staging directory
beside the destination — `<destination>/.pending-<Guid:N>/` — and moved into the destination one
by one only after the last id has landed, each under the unique-name helper with
`FileMode.CreateNew` semantics (a move never replaces an existing file); the staging directory is
then removed. On any failure the staging directory is removed with everything in it, so a failed
send leaves no new file where the agent looks for attachments. A same-user process that lists the
staging directory during the fetch can see files arriving — nothing hides bytes from a process
running as the same user (decision 11) — but nothing remains once the send has been refused.
Staging directories left by a crash are removed by the next fetch into the same destination and,
for `DaemonStore`, by the startup sweep.

Per id, in order: `GetAsync(url, HttpCompletionOption.ResponseHeadersRead)` so the body is never
buffered by the client; the response is disposed with the loop iteration. A non-success status, a
`Content-Length` over `MaxAttachmentBytes`, a body that exceeds it while streaming (the copy is
capped at `MaxAttachmentBytes + 1` bytes and stops reading there), a `Content-Disposition` name
that reduces to nothing after `Path.GetFileName`, or an IO exception ends the fetch with that id
and error. The trailer is composed only when every id landed, from the published paths in the
placement's form. The content type is read but not acted on.

### Handler

`LocalControlServer` routes `SendTextWithAttachments` to
`AgentOrchestrator.HandleLocalSendTextWithAttachmentsAsync`, which shares `AnswerSendTextAsync`'s
body through one private core taking `(agentId, text, string[]? attachmentIds)`. After the
existing text checks and before the delivery core: an empty `attachment_ids` on this frame is
`malformed` ("send_text carries no attachments" — the plain frame is for that); a `Validate`
refusal is `attachments_refused` with its text. Then `DeliverInputAsync(agent, text, ids)`.

### `DeliverInputAsync`

Runs `Validate` first for the server caller's sake (the local handler already did). A failed
fetch is `InputDeliveryOutcome.Drop(SendInputDropReason.DeliveryFailed,
$"attachment {id} unavailable: {error}")`. Both callers already route that reason — the server
caller through `ReportInputDroppedAsync`, the local one into the ack. The message is `text`
alone, or `text + "\n\n" + AttachmentTrailer.For(paths)`.

### Launch

The launch path's fetch keeps its place and its `OwnedWorktree` guard for the `Worktree`
placement (a borrowed cwd is never written to; `DaemonStore` needs no guard, and hub launches are
owned anyway) and stops being best-effort: a `Validate` refusal or a failed fetch throws, the
existing failure path removes the worktree (and the store directory, when the placement made
one), and the server sees `LaunchFailed` with `attachments_refused: …` or
`attachment_unavailable: <id>`. No launcher argv changes.

### PTY input lane

`PtyHostedAgentRuntime` gains one `SemaphoreSlim(1, 1)` over every write it makes to the PTY:
`SendUserInputAsync` holds it across the paste **and** `SubmitAsync`; `RequestGracefulStopAsync`
holds it across its `/exit` and submit; `SendRawInputAsync` and `SendSpecialKeyAsync` take it per
call. These four are the runtime's whole write surface — the attach loop's stdin (the app's
Terminal tab, `kcap agent attach`, including the Escape and Ctrl+C bytes those send), the server's
special keys, the web's and the app's composer sends, and the graceful stop. Nothing bypasses the
lane: `IPtyProcess.SendInterrupt` has no caller in the daemon and gains none here.

What this buys and costs: a keystroke that arrives during a composer send is written after the
CR, never inside the bracketed paste or between it and the CR. Within one writer, order is the
order of its awaited calls (each writer awaits its own writes in sequence); across writers there
is no order to promise, and `SemaphoreSlim`'s lack of FIFO is not a contract anyone relies on. The
wait is at most the paste plus `SingleSubmitDelay` — 150 ms — for an interactive agent whose
submit is a single CR. Under the approvals-disabled spray schedule the lane is held for the whole
spray, about 2.4 s. That schedule applies to review-flow launches, whose attach is read-only so
nothing waits, **and to a default-kind Codex session launched with the `never` approval posture,
whose terminal is writable**: a keystroke typed there during an attachment send lands up to
2.4 s late. Accepted and stated here: it is the same interval that session's composer sends
already take to submit, and releasing after the first CR would instead let the typed bytes be
swallowed by the later CRs.

### `SingleSubmitDelay`

Goes from 50 ms to 150 ms. The interactive Codex TUI treats an Enter within about 120 ms of a
paste as a newline, and the app's own terminal channel already waits 150 ms for that reason; the
frame path now carries the app's PTY sends and must clear the same window. The spray schedule is
unchanged.

## 5. Compatibility

| App | Daemon | Server | Behaviour |
|---|---|---|---|
| new | new (`input/2`) | any | full: chips, paste, drop; chat sends with ids on `SendTextWithAttachments`; launch ids on V2 |
| new | old, local | any | Chat and Home "+", paste-as-file and drop refused with "attachments need the daemon updated"; text unchanged |
| new | old, remote (version below the gate) | any | Home attachments refused with the same hint; a text launch unchanged |
| new | downgraded between the app's gate and the server's dispatch | any | the one accepted gap (decision 6): the old daemon launches best-effort |
| new | restarted to an old build between hello and a chat send | any | `SendTextWithAttachments` is undecodable there; the connection closes; the composer shows "delivery unconfirmed"; nothing delivered |
| old | new | any | old app sends `SendText` only; text-only as today |
| web (any) | new | any | same locations as today except Codex, whose files move to the daemon store; a missing attachment now fails the send visibly and leaves no partial batch |
| new | any | old (no V2 attachments) | V2 has carried `attachment_ids` since hosted agents shipped; not a live case |
| new | new | temp store id expired (>10 min between upload and fetch) | fetch 404 → chat send refused with the id named, draft kept; launch failed, draft restored if still within retention and the target unchanged, else "re-attach" |

## 6. Security and privacy

- The app uploads with the signed-in profile's token through the same client lease every other
  app HTTP call uses; the daemon downloads with its own — no new credential path, and the daemon
  still looks for none.
- **Integrity of placement** (decision 4): a write-contained runtime's files land outside every
  cwd, where the sandbox that contains it also stops it steering the daemon's write. An
  uncontained runtime keeps the in-worktree directory, where a steered write would give it
  nothing it lacks. Files never land in a borrowed cwd (the existing guard).
- **No confidentiality between same-user agents** (decision 11), the same as for worktrees
  today. The trailer's absolute path under `DaemonStore` names the daemon's state directory in
  the prompt and so in the transcript; transcripts already carry absolute worktree paths in every
  tool call, so this discloses nothing new.
- **Nothing left behind on failure** (decision 5): staging plus whole-batch publication.
- Streaming with `ResponseHeadersRead` plus the byte cap closes an unbounded response from a
  compromised or misconfigured server; `AttachmentIds.Validate` in front of every fetch closes a
  path segment smuggled as an id and a fetch storm from a misbehaving server, whichever lane the
  ids arrived on.
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
  chars, dashes, a path segment, null, blank. `AttachmentIds.Validate`: null and empty accepted;
  eleven refused; a malformed element refused; two ids equal under `Canonical` refused.
- `AttachmentTrailer.For` shapes one, two and many paths; `Prefix` matches.
- `LocalControlOps.SendTextWithAttachmentsAsync` serialises the ids as `attachment_ids`, sends
  frame 24, and maps EOF to `transport`.

**Daemon** (`Capacitor.Cli.Daemon.Tests.Unit`)
- Handler: an empty id list, over-count, a malformed id, and a mixed-case duplicate each refuse
  with the stated reason and error before the delivery core runs; a valid list reaches
  `DeliverInputAsync`. The same over-count, malformed and duplicate lists through
  `HandleSendInput` (server caller) and through the launch command are `DeliveryFailed` drops and
  a failed launch respectively, with no HTTP call made.
- Placement: the Codex factory declares `DaemonStore`, every other factory `Worktree`; the
  agent instance carries the factory's value; a `Worktree` fetch writes under `<cwd>/.attached/`
  with the `.gitignore` and relative trailer paths; a `DaemonStore` fetch writes under the hashed
  state-dir directory with absolute trailer paths. `AttachmentStore.Remove` deletes the tree
  without following a link planted inside it; the startup sweep deletes only directories whose
  agent is not live, and stale `.pending-*` directories.
- `DownloadAttachmentsAsync` against WireMock: success stages, then publishes every file and
  removes the staging directory; the request uses `ResponseHeadersRead`; a 404, a
  `Content-Length` over the cap, and a chunked over-cap body with no `Content-Length` each end
  the fetch naming the id, and the destination holds **no new file** afterwards (the earlier
  successes of that batch included) while files from an earlier successful batch are untouched;
  the chunked case is shown (through a counting handler) to stop reading at
  `MaxAttachmentBytes + 1`; a `Content-Disposition` that reduces to nothing is refused; a
  repeated file name gets the next unique name; a stale `.pending-*` directory from a crash is
  removed by the next fetch.
- `DeliverInputAsync`: a failed fetch is a `DeliveryFailed` drop with the id in the error, and the
  runtime receives nothing; a successful fetch delivers `text + "\n\n" + trailer`.
- `PtyHostedAgentRuntime`: a paste is followed by one CR no earlier than 150 ms later
  (`FakeTimeProvider`); raw input and special keys issued before, during and after
  `SendUserInputAsync` land entirely before the `ESC[200~` or entirely after the CR, with every
  byte accounted for and each writer's own order preserved; under the spray schedule (a Codex
  runtime built with `approvalsDisabled`, as a default-kind `never` posture yields) the lane is
  held until the last CR and concurrent raw input lands after it; `RequestGracefulStopAsync`
  cannot interleave with a paste.
- Launch: a failed fetch fails the launch with `attachment_unavailable`, removes the worktree and
  the store directory, and starts no process; a `Validate` refusal fails it with
  `attachments_refused`; `CleanupAgentAsync` removes the store directory when one exists.
- `LocalControlCapabilities.Current` contains `input/2`, and the routing switch handles frame 24
  (the existing pin, extended).

**App** (`Capacitor.App.Tests.Unit`, `[NotInParallel("AvaloniaSession")]` where a VM or view is built)
- `AttachmentTray`: `AddAll` refuses an oversize file and stages up to the cap, naming the rest;
  dedups names; `Snapshot` is a copy; `ContentEquals`; `Restore` replaces; `Clear`.
- `AttachmentIntake.Classify`: files beat text beats bitmap; nothing → `Nothing`.
  `ReadFilesAsync`: a folder, an oversize file (by reported size, and by stream length with no
  size), and an unreadable file are each refused with their wording while valid siblings are
  accepted, in order; content types assigned. `FromBitmap` yields a PNG named by the fake clock,
  and refuses one whose encoding runs over the cap.
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
  exchange with `CanAcceptText` false meanwhile, cleared on `Ok`; `CanAttach` follows `input/2`.
  `LocalFrameChatInput`: `attachments_refused` maps to the hint.
- `HomeViewModel`: `CanAttach` follows sign-in, `input/2` for the local machine and the version
  gate for a remote one (below, equal, above, unparsable, missing); the launch is built from the
  captured draft — a machine, repo or vendor changed during the upload does not reach the request,
  and the post-upload gate runs against the draft's machine; an empty goal with files launches;
  upload failure → `StartError`, no launch; on `Started` a goal edited during the upload or the
  hub call is kept and only an unedited one clears, a file added meanwhile is kept and only an
  unchanged tray clears; an unusable returned id retains no draft and says so; `Started` retains
  the draft; launch A with files then launch B with files accepted, then A fails → A's real
  reason plus "re-attach", B's draft untouched; `LaunchFailed` before registration (buffered),
  after the request returned, and after the retention cutoff; a row arriving before
  registration; the retention timer releasing a draft with no event; restore into an empty
  composer with an unchanged target, no restore into an edited composer, no restore after a
  target change, each with its message; a non-attachment failure reason with files staged keeps
  the real reason; `LaunchPayload.For` emits `attachment_ids` only when non-empty.
- Headless smoke: the chip strip renders one chip per staged file and removing one updates it;
  a drop of a file on the card stages it; a `PastingFromClipboard` raise with the headless
  clipboard holding text pastes text once. Bitmap paste is verified by hand on macOS, where the
  clipboard carries TIFF and PNG, and noted in the PR.

**By hand, recorded in the PR:** one image and one PDF attached from the desktop to each of
Claude and Codex (both placements), and one text file to one ACP vendor, one to Pi and one to
Antigravity, each confirmed read by the agent from the trailer's path. The `Worktree` placement
is today's contract for those vendors, so this is a regression check, not a new proof.

## 8. Out of scope

- A composer for remote sessions (there is none yet; when it lands, its channel is the server's
  `SendUserInput(agentId, text, attachmentIds)` and this design's tray and uploader apply
  unchanged, gated on the same version rule as Home).
- Rendering attachments as thumbnails inside Chat turns, and rendering the agent's own image output.
- Vendor-native image inputs (decision 3).
- A `kcap agent send --attach` verb.
- A server-side daemon capability contract for launches (decision 6 names the gap it would close).
- Restoring a text-only launch's goal on failure.
- Read isolation between agents of one user (decision 11).

## Risks

- **A vendor that confines its file tool to the workspace** reads attachments today because they
  are in the workspace, and keeps doing so under the `Worktree` placement. The by-hand check
  above guards the regression. Only Codex moves, and Codex reads anywhere under both sandboxes.
- **The 150 ms submit delay** is calibrated to today's Codex suppression window; a TUI that widens
  it turns a frame send into a newline. Same exposure the app's terminal channel carries, and
  now in one constant with one measurement behind it.
- **The version gate** is only as good as the constant: both halves ship in one PR, so no released
  daemon sits between them, but a hotfix branch that back-ports one without the other would
  reopen the gap. The PR notes this.
- **Memory.** Ten 10 MiB files staged is 100 MiB held until Send, and one retained launch draft
  holds its copy for up to 10 minutes more; a user who stages and walks away holds it
  indefinitely. Accepted for parity with the web composer; a tray is cleared when its workspace
  is torn down.
