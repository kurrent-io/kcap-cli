# Desktop Support & Feedback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the desktop app a way to reach Kurrent support — Help-menu items, a rail-footer help button, and a native "Send feedback" window — by posting through the CLI's existing feedback client, with no server change.

**Architecture:** `Capacitor.Cli.Core` grows a typed `FeedbackSubmission` (category and source as enums, mapped to wire strings in one place), a shared refusal-sentence table, and a message composer; `kcap feedback` keeps its behaviour on top of them. `Capacitor.App` adds `FeedbackViewModel` + `FeedbackWindow` (Settings single-instance pattern), two Help items and a footer flyout, all enabled by one oracle (a resolved server URL) evaluated before the main window is shown.

**Tech Stack:** .NET 10, Avalonia 11 (`NativeMenu`, `MenuFlyout`), ReactiveUI, TUnit on Microsoft Testing Platform, headless Avalonia tests via `AvaloniaSession`.

**Spec:** `docs/superpowers/specs/2026-09-15-ai2834-desktop-support-feedback-design.md` (Linear AI-2834; codex spec review clean after four rounds). Read it first; every decision below is argued there.

## Global Constraints

- **Worktree:** `/Users/tony/dev/kcap-cli-worktrees/ai-2834-desktop-support-feedback`, branch `tonyyoung/ai-2834-desktop-support-feedback` (based on kcap-cli `origin/main` 7b4a6181). Run every command from there.
- **No Linear ids in `.cs` files** (kcap-cli rule). The id lives in the PR title and this plan only.
- **One type per file, named after the type.** The only exception used here is none.
- **Comments are scarce:** no ticket ids as narration, no design coordinates ("D4", "Task 3"), no change narration ("previously", "moved from").
- **Wire JSON** is `snake_case` through `[property: JsonPropertyName]` on the existing records, serialized with the source-generated `CapacitorJsonContext` (AOT; no reflection serializers). No new wire type is added — `FeedbackSubmitRequest`'s shape does not change, only its file.
- **Server rules the app must mirror:** category exactly `bug` or `feedback`; message trimmed then 1–8000 characters; `context.source` is free text and is discarded by the server today.
- **Copy, verbatim from the spec:** Help items `Report a Bug…` / `Send Feedback…`; flyout items `Documentation` / `Report a bug…` / `Send feedback…`; button tooltip and automation name `Help and support`; window title `Kurrent Capacitor — Send feedback`; success line `Sent. Replies go to {email}.`; other-exception line `Couldn't send the report: {message}`; trailer `Sent from Kurrent Capacitor Desktop {app version} · daemon {name} {version}`; version fallbacks `cli {version}` then `version unknown`; hint `Attached automatically: desktop {app} · daemon {name} {version} · {OS} · {n} characters left`.
- **Tests:** TUnit on MTP. Run one project at a time and always filtered:
  `dotnet run --project test/<Project>/<Project>.csproj -- --treenode-filter "/*/*/<Class>/<Method>*"` (never `--filter`). Avalonia tests: `[NotInParallel("AvaloniaSession")]` on the class or method and `AvaloniaSession.DispatchAsync` / `AvaloniaSession.RunOnUiAsync` for UI-thread work. Never run a whole suite locally; CI does that.
- **Commits:** imperative subject ≤ 72 characters, no ticket id; optional body ≤ 5 lines naming a constraint; last line `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`. Stage by explicit path.
- **Internals:** `Capacitor.Cli.Core` grants internals to `kcap`, `kcap-daemon` and `Capacitor.Cli.Tests.Unit` only — **not** to `Capacitor.Cli.Core.Tests.Unit`. Anything a Core test needs is public. `Capacitor.App` grants internals to `Capacitor.App.Tests.Unit`.

---

## File structure

**`src/Capacitor.Cli.Core/Commands/`**
- `FeedbackCategory.cs` — `enum FeedbackCategory { Bug, Feedback }`.
- `FeedbackSource.cs` — `enum FeedbackSource { Cli, Desktop }`.
- `FeedbackSubmission.cs` — the typed submission record (the file exists and holds three wire records today; those move out).
- `FeedbackSubmitRequest.cs`, `FeedbackSubmitContext.cs`, `FeedbackSubmitResponse.cs` — the wire records, one per file, unchanged in shape; `FeedbackSubmitRequest` gains a static `From(FeedbackSubmission)` factory that owns the enum-to-wire mapping.
- `FeedbackMessageComposer.cs` — `Compose(userText, trailer)`, `MaxLength`, `Remaining(...)`.

**`src/Capacitor.Cli.Core/Http/`**
- `IFeedbackApi.cs`, `FeedbackApi.cs` — the typed `SubmitAsync(FeedbackSubmission, ct)`.
- `FeedbackResultMessages.cs` — `ForRefusal(FeedbackResult)`.

**`src/Capacitor.Cli/Commands/FeedbackCommand.cs`** — builds a `FeedbackSubmission`; prints `ForRefusal` for refusals.

**`src/Capacitor.App/`**
- `ViewModels/FeedbackTrailer.cs` — `Build(appVersion, daemonName, snapshotVersion, cliVersion)`.
- `ViewModels/FeedbackViewModel.cs` — the form, the snapshot, the send, the outcome mapping.
- `Views/FeedbackWindow.axaml`, `Views/FeedbackWindow.axaml.cs` — the window.
- `Views/AppMenuBar.cs` — Help items, per-window retention, `SetFeedbackAction`.
- `Views/SessionRailView.axaml` — footer help button + flyout.
- `ViewModels/MainWindowViewModel.cs` — `OpenDocsCommand`, `OpenFeedbackCommand`.
- `App.axaml.cs` — `OpenFeedback`, the oracle, wiring before `ShowMainWindow`, shutdown close.

**Tests**
- `test/Capacitor.Cli.Core.Tests.Unit/FeedbackSubmitRequestTests.cs`, `FeedbackMessageComposerTests.cs`, `FeedbackResultMessagesTests.cs` (new).
- `test/Capacitor.Cli.Tests.Unit/Commands/FeedbackCommandTests.cs` (modify).
- `test/Capacitor.App.Tests.Unit/FeedbackTrailerTests.cs`, `FeedbackViewModelTests.cs`, `FeedbackWindowSmokeTests.cs` (new); `AppMenuBarTests.cs`, `MainWindowSmokeTests.cs`, `DesktopWindowLifecycleTests.cs` (modify).

**Docs** — `docs/CHANGES.md` (new entry at the top, existing format), `README.md` desktop section.

---

### Task 1: Typed feedback submission in Cli.Core

**Files:**
- Create: `src/Capacitor.Cli.Core/Commands/FeedbackCategory.cs`, `FeedbackSource.cs`, `FeedbackSubmitRequest.cs`, `FeedbackSubmitContext.cs`, `FeedbackSubmitResponse.cs`
- Modify: `src/Capacitor.Cli.Core/Commands/FeedbackSubmission.cs` (becomes the typed record only), `src/Capacitor.Cli.Core/Http/IFeedbackApi.cs`, `src/Capacitor.Cli.Core/Http/FeedbackApi.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/FeedbackSubmitRequestTests.cs`

**Interfaces:**
- Produces: `enum FeedbackCategory { Bug, Feedback }`; `enum FeedbackSource { Cli, Desktop }`; `record FeedbackSubmission(FeedbackCategory Category, string Message, Guid ClientRequestId, FeedbackSource Source)`; `FeedbackSubmitRequest.From(FeedbackSubmission)`; `Task<FeedbackResult> IFeedbackApi.SubmitAsync(FeedbackSubmission submission, CancellationToken ct = default)`.

- [ ] **Step 1: Write the failing test**

```csharp
// test/Capacitor.Cli.Core.Tests.Unit/FeedbackSubmitRequestTests.cs
using Capacitor.Cli.Core.Commands;

namespace Capacitor.Cli.Core.Tests.Unit;

public class FeedbackSubmitRequestTests {
    static FeedbackSubmission Submission(FeedbackCategory category, FeedbackSource source, Guid? id = null) =>
        new(category, "It broke.", id ?? Guid.NewGuid(), source);

    [Test]
    [Arguments(FeedbackCategory.Bug, "bug")]
    [Arguments(FeedbackCategory.Feedback, "feedback")]
    public async Task Category_serialises_to_its_lowercase_wire_value(FeedbackCategory category, string wire) {
        var request = FeedbackSubmitRequest.From(Submission(category, FeedbackSource.Cli));

        await Assert.That(request.Category).IsEqualTo(wire);
    }

    [Test]
    [Arguments(FeedbackSource.Cli, "cli")]
    [Arguments(FeedbackSource.Desktop, "desktop")]
    public async Task Source_serialises_to_its_lowercase_wire_value(FeedbackSource source, string wire) {
        var request = FeedbackSubmitRequest.From(Submission(FeedbackCategory.Bug, source));

        await Assert.That(request.Context.Source).IsEqualTo(wire);
    }

    [Test]
    public async Task Message_and_client_request_id_pass_through_unchanged() {
        var id      = Guid.NewGuid();
        var request = FeedbackSubmitRequest.From(Submission(FeedbackCategory.Feedback, FeedbackSource.Desktop, id));

        await Assert.That(request.Message).IsEqualTo("It broke.");
        await Assert.That(request.ClientRequestId).IsEqualTo(id);
        await Assert.That(request.Context.ClientVersion).IsEqualTo(CapacitorVersion.CurrentDisplay());
        await Assert.That(request.Context.Os).IsNotEmpty();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/FeedbackSubmitRequestTests/*"`
Expected: build error — `FeedbackCategory`, `FeedbackSource` and `FeedbackSubmitRequest.From` do not exist.

- [ ] **Step 3: Create the enums and the typed record; split the wire records into their own files**

```csharp
// src/Capacitor.Cli.Core/Commands/FeedbackCategory.cs
namespace Capacitor.Cli.Core.Commands;

public enum FeedbackCategory { Bug, Feedback }
```

```csharp
// src/Capacitor.Cli.Core/Commands/FeedbackSource.cs
namespace Capacitor.Cli.Core.Commands;

public enum FeedbackSource { Cli, Desktop }
```

```csharp
// src/Capacitor.Cli.Core/Commands/FeedbackSubmission.cs  (replace the whole file)
namespace Capacitor.Cli.Core.Commands;

/// <summary>One report as a caller means it. <see cref="ClientRequestId"/> is the idempotency key the
/// server dedupes on, so a retry of the SAME report must reuse it — mint it once, thread it through.</summary>
public sealed record FeedbackSubmission(
    FeedbackCategory Category,
    string           Message,
    Guid             ClientRequestId,
    FeedbackSource   Source
);
```

```csharp
// src/Capacitor.Cli.Core/Commands/FeedbackSubmitRequest.cs
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Commands;

/// <summary>Request body for the tenant's <c>POST /api/feedback</c>. Built only through
/// <see cref="From"/>, the one place the enums become wire strings.</summary>
public sealed record FeedbackSubmitRequest(
        [property: JsonPropertyName("category")]          string                Category,
        [property: JsonPropertyName("message")]           string                Message,
        [property: JsonPropertyName("client_request_id")] Guid                  ClientRequestId,
        [property: JsonPropertyName("context")]           FeedbackSubmitContext Context
    ) {
    public static FeedbackSubmitRequest From(FeedbackSubmission submission) => new(
        Category:        Wire(submission.Category),
        Message:         submission.Message,
        ClientRequestId: submission.ClientRequestId,
        Context: new FeedbackSubmitContext(
            Source:        Wire(submission.Source),
            ClientVersion: CapacitorVersion.CurrentDisplay(),
            Os:            RuntimeInformation.OSDescription
        )
    );

    static string Wire(FeedbackCategory category) => category switch {
        FeedbackCategory.Bug      => "bug",
        FeedbackCategory.Feedback => "feedback",
        _                         => throw new ArgumentOutOfRangeException(nameof(category), category, null)
    };

    static string Wire(FeedbackSource source) => source switch {
        FeedbackSource.Cli     => "cli",
        FeedbackSource.Desktop => "desktop",
        _                      => throw new ArgumentOutOfRangeException(nameof(source), source, null)
    };
}
```

```csharp
// src/Capacitor.Cli.Core/Commands/FeedbackSubmitContext.cs
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Commands;

public sealed record FeedbackSubmitContext(
        [property: JsonPropertyName("source")]         string  Source,
        [property: JsonPropertyName("client_version")] string? ClientVersion,
        [property: JsonPropertyName("os")]             string? Os
    );
```

```csharp
// src/Capacitor.Cli.Core/Commands/FeedbackSubmitResponse.cs
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Commands;

public sealed record FeedbackSubmitResponse {
    [JsonPropertyName("reporter_email")] public string ReporterEmail { get; init; } = "";
}
```

Keep the existing doc comments' substance where it still holds (the idempotency note moved onto `FeedbackSubmission`); drop the sentences that narrate the CLI as the only caller.

- [ ] **Step 4: Retype the API**

```csharp
// src/Capacitor.Cli.Core/Http/IFeedbackApi.cs
using Capacitor.Cli.Core.Commands;

namespace Capacitor.Cli.Core.Http;

/// <summary>Our own server's <c>POST /api/feedback</c> — see <see cref="ISessionsApi"/> for the
/// no-URL-composition, no-status-inspection contract every API interface in this namespace
/// follows.</summary>
public interface IFeedbackApi {
    Task<FeedbackResult> SubmitAsync(FeedbackSubmission submission, CancellationToken ct = default);
}
```

In `FeedbackApi.SubmitAsync`, replace the signature and the request construction:

```csharp
public async Task<FeedbackResult> SubmitAsync(FeedbackSubmission submission, CancellationToken ct = default) {
    var request = FeedbackSubmitRequest.From(submission);

    using var content = JsonContent.Create(request, CapacitorJsonContext.Default.FeedbackSubmitRequest);
    // ... the rest of the method is unchanged
```

Remove the now-unused `using System.Runtime.InteropServices;` from `FeedbackApi.cs`. `FeedbackCommand` no longer compiles; Task 2 fixes it — do not touch it here beyond what the build needs to stay green in `Capacitor.Cli.Core` (the `kcap` project is built in Task 2).

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/FeedbackSubmitRequestTests/*"`
Expected: 5 passed.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli.Core/Commands/FeedbackCategory.cs src/Capacitor.Cli.Core/Commands/FeedbackSource.cs \
        src/Capacitor.Cli.Core/Commands/FeedbackSubmission.cs src/Capacitor.Cli.Core/Commands/FeedbackSubmitRequest.cs \
        src/Capacitor.Cli.Core/Commands/FeedbackSubmitContext.cs src/Capacitor.Cli.Core/Commands/FeedbackSubmitResponse.cs \
        src/Capacitor.Cli.Core/Http/IFeedbackApi.cs src/Capacitor.Cli.Core/Http/FeedbackApi.cs \
        test/Capacitor.Cli.Core.Tests.Unit/FeedbackSubmitRequestTests.cs
git commit -m "Type the feedback submission and map its wire values in one place" \
  -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: Shared refusal sentences; `kcap feedback` on the typed lane

**Files:**
- Create: `src/Capacitor.Cli.Core/Http/FeedbackResultMessages.cs`
- Modify: `src/Capacitor.Cli/Commands/FeedbackCommand.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/FeedbackResultMessagesTests.cs` (new), `test/Capacitor.Cli.Tests.Unit/Commands/FeedbackCommandTests.cs` (modify)

**Interfaces:**
- Consumes: `FeedbackSubmission`, `IFeedbackApi.SubmitAsync(FeedbackSubmission, ct)` from Task 1.
- Produces: `static string? FeedbackResultMessages.ForRefusal(FeedbackResult result)` — `null` for `Sent`.

- [ ] **Step 1: Write the failing messages test**

```csharp
// test/Capacitor.Cli.Core.Tests.Unit/FeedbackResultMessagesTests.cs
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Core.Tests.Unit;

public class FeedbackResultMessagesTests {
    [Test]
    public async Task Sent_has_no_refusal_sentence() =>
        await Assert.That(FeedbackResultMessages.ForRefusal(new FeedbackResult.Sent("a@b.c"))).IsNull();

    [Test]
    public async Task Each_refusal_has_its_sentence() {
        await Assert.That(FeedbackResultMessages.ForRefusal(new FeedbackResult.NotConfigured()))
            .IsEqualTo("This server doesn't have support intake enabled.");
        await Assert.That(FeedbackResultMessages.ForRefusal(new FeedbackResult.Unavailable()))
            .IsEqualTo("Support intake isn't configured on this server — ask your admin.");
        await Assert.That(FeedbackResultMessages.ForRefusal(new FeedbackResult.NoEmailOnFile()))
            .IsEqualTo("Your account has no email on file — sign in to the web app once, then retry.");
        await Assert.That(FeedbackResultMessages.ForRefusal(new FeedbackResult.RateLimited()))
            .IsEqualTo("You've sent several reports recently — try again in a few minutes.");
        await Assert.That(FeedbackResultMessages.ForRefusal(new FeedbackResult.TemporarilyUnavailable(TimeSpan.FromSeconds(7.2))))
            .IsEqualTo("Couldn't reach Kurrent support (temporary) — try again in 8s.");
        await Assert.That(FeedbackResultMessages.ForRefusal(new FeedbackResult.TemporarilyUnavailable(null)))
            .IsEqualTo("Couldn't reach Kurrent support (temporary) — try again.");
        await Assert.That(FeedbackResultMessages.ForRefusal(new FeedbackResult.Invalid("Too long.")))
            .IsEqualTo("Too long.");
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/FeedbackResultMessagesTests/*"`
Expected: build error — `FeedbackResultMessages` does not exist.

- [ ] **Step 3: Create the table**

```csharp
// src/Capacitor.Cli.Core/Http/FeedbackResultMessages.cs
using System.Diagnostics;

namespace Capacitor.Cli.Core.Http;

/// <summary>The sentence a person reads for each refusal the feedback lane distinguishes. Refusals
/// only: a success is presented by the surface that sent it.</summary>
public static class FeedbackResultMessages {
    public static string? ForRefusal(FeedbackResult result) => result switch {
        FeedbackResult.Sent                                    => null,
        FeedbackResult.NotConfigured                           => "This server doesn't have support intake enabled.",
        FeedbackResult.Unavailable                             => "Support intake isn't configured on this server — ask your admin.",
        FeedbackResult.NoEmailOnFile                           => "Your account has no email on file — sign in to the web app once, then retry.",
        FeedbackResult.RateLimited                             => "You've sent several reports recently — try again in a few minutes.",
        FeedbackResult.TemporarilyUnavailable(var retryAfter)  => $"Couldn't reach Kurrent support (temporary) — try again{Suffix(retryAfter)}",
        FeedbackResult.Invalid(var message)                    => message,
        _                                                      => throw new UnreachableException()
    };

    static string Suffix(TimeSpan? retryAfter) =>
        retryAfter is { } delta ? $" in {(int)Math.Ceiling(delta.TotalSeconds)}s." : ".";
}
```

- [ ] **Step 4: Rewrite `FeedbackCommand` on the typed lane**

Replace the category string with the enum and the refusal `switch` with the table; the success line and the exit codes stay exactly as they are:

```csharp
var category = isBug ? FeedbackCategory.Bug : FeedbackCategory.Feedback;
// ...
return await HandleCore(feedbackApi, category, message);

internal static async Task<int> HandleCore(IFeedbackApi feedbackApi, FeedbackCategory category, string message) {
    try {
        var submission = new FeedbackSubmission(category, message, Guid.NewGuid(), FeedbackSource.Cli);
        return await ReportResultAsync(await feedbackApi.SubmitAsync(submission));
    } catch (CapacitorApiException ex) {
        // unchanged
    }
}

static async Task<int> ReportResultAsync(FeedbackResult result) {
    if (result is FeedbackResult.Sent(var reporterEmail)) {
        await Console.Out.WriteLineAsync($"{SuccessPrefix}{reporterEmail} — replies will reach you by email.");
        return 0;
    }

    await Console.Error.WriteLineAsync(FeedbackResultMessages.ForRefusal(result));
    return 1;
}
```

Add `using Capacitor.Cli.Core.Commands;` and `using Capacitor.Cli.Core.Http;` as needed; delete the `using System.Diagnostics;` if `UnreachableException` is no longer referenced.

- [ ] **Step 5: Update `FeedbackCommandTests`**

Open `test/Capacitor.Cli.Tests.Unit/Commands/FeedbackCommandTests.cs`. Wherever the fake `IFeedbackApi` implements `SubmitAsync(string category, string message, ...)`, change it to `SubmitAsync(FeedbackSubmission submission, CancellationToken ct = default)` and record `submission`; wherever a test asserts the recorded category string, assert `submission.Category` is `FeedbackCategory.Bug` / `FeedbackCategory.Feedback` and `submission.Source` is `FeedbackSource.Cli`. Every existing assertion on stdout/stderr text and exit codes stays as it is — that is the pin that the CLI's behaviour did not change. Add one test:

```csharp
[Test]
public async Task Bug_flag_sends_the_bug_category_from_the_cli() {
    var api  = new RecordingFeedbackApi(new FeedbackResult.Sent("a@b.c"));
    var code = await FeedbackCommand.HandleCore(api, FeedbackCategory.Bug, "It broke.");

    await Assert.That(code).IsEqualTo(0);
    await Assert.That(api.Last!.Category).IsEqualTo(FeedbackCategory.Bug);
    await Assert.That(api.Last.Source).IsEqualTo(FeedbackSource.Cli);
    await Assert.That(api.Last.ClientRequestId).IsNotEqualTo(Guid.Empty);
}
```

(`RecordingFeedbackApi` is whatever the file's existing fake is called — keep its name, give it a `Last` property.)

- [ ] **Step 6: Run both test classes**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/FeedbackResultMessagesTests/*"`
Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/FeedbackCommandTests/*"`
Expected: all passed; no stdout/stderr assertion changed.

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.Cli.Core/Http/FeedbackResultMessages.cs src/Capacitor.Cli/Commands/FeedbackCommand.cs \
        test/Capacitor.Cli.Core.Tests.Unit/FeedbackResultMessagesTests.cs test/Capacitor.Cli.Tests.Unit/Commands/FeedbackCommandTests.cs
git commit -m "Share the feedback refusal sentences below the CLI" \
  -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: Message composer

**Files:**
- Create: `src/Capacitor.Cli.Core/Commands/FeedbackMessageComposer.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/FeedbackMessageComposerTests.cs`

**Interfaces:**
- Produces: `FeedbackMessageComposer.MaxLength` (= 8000), `string Compose(string userText, string trailer)`, `int Remaining(string userText, string trailer)`.

- [ ] **Step 1: Write the failing test**

```csharp
// test/Capacitor.Cli.Core.Tests.Unit/FeedbackMessageComposerTests.cs
using Capacitor.Cli.Core.Commands;

namespace Capacitor.Cli.Core.Tests.Unit;

public class FeedbackMessageComposerTests {
    const string Trailer = "Sent from Kurrent Capacitor Desktop 1.0.3 · daemon tonys-mbp 1.0.3";

    [Test]
    public async Task Compose_trims_then_joins_with_a_blank_line() =>
        await Assert.That(FeedbackMessageComposer.Compose("  It broke.  \n", Trailer))
            .IsEqualTo("It broke.\n\n" + Trailer);

    [Test]
    public async Task Remaining_counts_the_composed_message_against_the_cap() {
        var overhead = 2 + Trailer.Length;

        await Assert.That(FeedbackMessageComposer.Remaining("", Trailer)).IsEqualTo(8000 - overhead);
        await Assert.That(FeedbackMessageComposer.Remaining(new string('x', 8000 - overhead), Trailer)).IsEqualTo(0);
        await Assert.That(FeedbackMessageComposer.Remaining(new string('x', 8001 - overhead), Trailer)).IsEqualTo(-1);
    }

    [Test]
    public async Task Largest_accepted_user_text_composes_to_exactly_the_cap() {
        var largest = new string('x', 8000 - 2 - Trailer.Length);

        await Assert.That(FeedbackMessageComposer.Compose(largest, Trailer).Length).IsEqualTo(8000);
        await Assert.That(FeedbackMessageComposer.Compose(largest + "x", Trailer).Length).IsEqualTo(8001);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/FeedbackMessageComposerTests/*"`
Expected: build error.

- [ ] **Step 3: Implement**

```csharp
// src/Capacitor.Cli.Core/Commands/FeedbackMessageComposer.cs
namespace Capacitor.Cli.Core.Commands;

/// <summary>The one wire message a desktop report sends: the reporter's text and a trailer naming
/// the client. The server trims and caps the whole message, so the cap is checked on the composed
/// value, never on the reporter's text alone.</summary>
public static class FeedbackMessageComposer {
    public const int MaxLength = 8000;

    public static string Compose(string userText, string trailer) => $"{userText.Trim()}\n\n{trailer}";

    public static int Remaining(string userText, string trailer) => MaxLength - Compose(userText, trailer).Length;
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: the Step 2 command. Expected: 3 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Commands/FeedbackMessageComposer.cs test/Capacitor.Cli.Core.Tests.Unit/FeedbackMessageComposerTests.cs
git commit -m "Compose the desktop feedback message under the server's cap" \
  -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: Trailer selection

**Files:**
- Create: `src/Capacitor.App/ViewModels/FeedbackTrailer.cs`
- Test: `test/Capacitor.App.Tests.Unit/FeedbackTrailerTests.cs`

**Interfaces:**
- Produces: `static string FeedbackTrailer.Build(string appVersion, string daemonName, string? snapshotVersion, string? cliVersion)`.

- [ ] **Step 1: Write the failing test**

```csharp
// test/Capacitor.App.Tests.Unit/FeedbackTrailerTests.cs
using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

public class FeedbackTrailerTests {
    [Test]
    public async Task Connected_daemon_reports_its_snapshot_version() =>
        await Assert.That(FeedbackTrailer.Build("1.0.3", "tonys-mbp", "1.0.2", "1.0.3"))
            .IsEqualTo("Sent from Kurrent Capacitor Desktop 1.0.3 · daemon tonys-mbp 1.0.2");

    [Test]
    public async Task Never_observed_daemon_falls_back_to_the_installed_cli() =>
        await Assert.That(FeedbackTrailer.Build("1.0.3", "tonys-mbp", null, "1.0.3"))
            .IsEqualTo("Sent from Kurrent Capacitor Desktop 1.0.3 · daemon tonys-mbp cli 1.0.3");

    [Test]
    public async Task No_version_anywhere_says_so() =>
        await Assert.That(FeedbackTrailer.Build("1.0.3", "tonys-mbp", "", null))
            .IsEqualTo("Sent from Kurrent Capacitor Desktop 1.0.3 · daemon tonys-mbp version unknown");
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/FeedbackTrailerTests/*"`
Expected: build error.

- [ ] **Step 3: Implement**

```csharp
// src/Capacitor.App/ViewModels/FeedbackTrailer.cs
namespace Capacitor.App.ViewModels;

/// <summary>The line appended to a desktop report so support can see which client and daemon it
/// came from. The daemon's last observed version wins: a daemon that crashed after being observed
/// reports the version the bug is about.</summary>
public static class FeedbackTrailer {
    public static string Build(string appVersion, string daemonName, string? snapshotVersion, string? cliVersion) {
        var version = snapshotVersion is { Length: > 0 } observed ? observed
            : cliVersion is { Length: > 0 } installed ? $"cli {installed}"
            : "version unknown";

        return $"Sent from Kurrent Capacitor Desktop {appVersion} · daemon {daemonName} {version}";
    }
}
```

- [ ] **Step 4: Run the test to verify it passes** — Step 2 command; 3 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/ViewModels/FeedbackTrailer.cs test/Capacitor.App.Tests.Unit/FeedbackTrailerTests.cs
git commit -m "Name the client and daemon on a desktop feedback report" \
  -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: `FeedbackViewModel`

**Files:**
- Create: `src/Capacitor.App/ViewModels/FeedbackViewModel.cs`
- Test: `test/Capacitor.App.Tests.Unit/FeedbackViewModelTests.cs`

**Interfaces:**
- Consumes: `IFeedbackApi`, `FeedbackSubmission`, `FeedbackCategory`, `FeedbackSource`, `FeedbackResult`, `FeedbackResultMessages`, `FeedbackMessageComposer` (Tasks 1–3); `HomeViewModel.SignInExpiredNotice` (existing, `internal const`).
- Produces:

```csharp
public sealed class FeedbackViewModel : ReactiveObject, IDisposable {
    public FeedbackViewModel(IFeedbackApi api, FeedbackCategory initial, IObservable<string> trailer,
                             string osDescription, Action? signIn, CancellationToken appLifetime = default);
    public FeedbackCategory Category { get; set; }
    public string Message { get; set; }
    public string Hint { get; }                 // "Attached automatically: … · {n} characters left"
    public bool CanSend { get; }
    public bool IsBusy { get; }
    public bool IsSent { get; }
    public string? ReporterEmail { get; }
    public string? Outcome { get; }             // refusal / failure sentence, null otherwise
    public bool SignInOffered { get; }
    public ReactiveCommand<Unit, Unit> SendCommand { get; }
    public ReactiveCommand<Unit, Unit> SendAnotherCommand { get; }
    public ReactiveCommand<Unit, Unit> SignInCommand { get; }
    public void Reopen(FeedbackCategory category); // second entry-point click
    internal Guid CurrentId { get; }               // for tests
}
```

- [ ] **Step 1: Write the failing tests**

```csharp
// test/Capacitor.App.Tests.Unit/FeedbackViewModelTests.cs
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.Http;

namespace Capacitor.App.Tests.Unit;

public class FeedbackViewModelTests {
    const string TrailerA = "Sent from Kurrent Capacitor Desktop 1.0.3 · daemon d cli 1.0.3";
    const string TrailerB = "Sent from Kurrent Capacitor Desktop 1.0.3 · daemon d 1.0.3";

    sealed class ScriptedFeedbackApi : IFeedbackApi {
        readonly Queue<Func<FeedbackSubmission, FeedbackResult>> _script = new();
        public List<FeedbackSubmission> Sent { get; } = [];
        public ScriptedFeedbackApi Then(FeedbackResult result) { _script.Enqueue(_ => result); return this; }
        public ScriptedFeedbackApi ThenThrow(Exception ex) { _script.Enqueue(_ => throw ex); return this; }
        public Task<FeedbackResult> SubmitAsync(FeedbackSubmission submission, CancellationToken ct = default) {
            Sent.Add(submission);
            return Task.FromResult(_script.Dequeue()(submission));
        }
    }

    static (FeedbackViewModel Vm, ScriptedFeedbackApi Api, BehaviorSubject<string> Trailer, List<bool> SignIns) New(
            FeedbackCategory category = FeedbackCategory.Bug) {
        var api     = new ScriptedFeedbackApi();
        var trailer = new BehaviorSubject<string>(TrailerA);
        var signIns = new List<bool>();
        var vm      = new FeedbackViewModel(api, category, trailer, "macOS 15.6", () => signIns.Add(true));
        return (vm, api, trailer, signIns);
    }

    [Test]
    public async Task Send_is_disabled_on_empty_and_whitespace_text_and_over_the_composed_cap() {
        var (vm, _, _, _) = New();

        await Assert.That(vm.CanSend).IsFalse();
        vm.Message = "   \n";
        await Assert.That(vm.CanSend).IsFalse();
        var largest = new string('x', 8000 - 2 - TrailerA.Length);
        vm.Message = largest;
        await Assert.That(vm.CanSend).IsTrue();
        vm.Message = largest + "x";
        await Assert.That(vm.CanSend).IsFalse();
    }

    [Test]
    public async Task Hint_names_the_attachment_and_the_remaining_allowance() {
        var (vm, _, _, _) = New();
        vm.Message = "abc";

        await Assert.That(vm.Hint).IsEqualTo(
            $"Attached automatically: desktop 1.0.3 · daemon d cli 1.0.3 · macOS 15.6 · {8000 - 2 - TrailerA.Length - 3} characters left");
    }

    [Test]
    public async Task Send_composes_the_trailer_and_the_desktop_source() {
        var (vm, api, _, _) = New(FeedbackCategory.Feedback);
        api.Then(new FeedbackResult.Sent("a@b.c"));
        vm.Message = " Nice. ";

        await vm.SendCommand.Execute().ToTask();

        await Assert.That(api.Sent).HasCount().EqualTo(1);
        await Assert.That(api.Sent[0].Message).IsEqualTo("Nice.\n\n" + TrailerA);
        await Assert.That(api.Sent[0].Category).IsEqualTo(FeedbackCategory.Feedback);
        await Assert.That(api.Sent[0].Source).IsEqualTo(FeedbackSource.Desktop);
        await Assert.That(vm.IsSent).IsTrue();
        await Assert.That(vm.ReporterEmail).IsEqualTo("a@b.c");
    }

    [Test]
    public async Task Unchanged_retry_resends_the_same_snapshot_even_if_the_daemon_version_arrived() {
        var (vm, api, trailer, _) = New();
        api.Then(new FeedbackResult.TemporarilyUnavailable(TimeSpan.FromSeconds(3))).Then(new FeedbackResult.Sent("a@b.c"));
        vm.Message = "It broke.";

        await vm.SendCommand.Execute().ToTask();
        await Assert.That(vm.Outcome).IsEqualTo("Couldn't reach Kurrent support (temporary) — try again in 3s.");
        trailer.OnNext(TrailerB);
        await Assert.That(vm.Hint).Contains("daemon d cli 1.0.3");   // still the frozen trailer
        await vm.SendCommand.Execute().ToTask();

        await Assert.That(api.Sent).HasCount().EqualTo(2);
        await Assert.That(api.Sent[1]).IsEqualTo(api.Sent[0]);
    }

    [Test]
    public async Task An_edit_after_a_refusal_mints_a_new_id_and_a_fresh_trailer() {
        var (vm, api, trailer, _) = New();
        api.Then(new FeedbackResult.RateLimited()).Then(new FeedbackResult.Sent("a@b.c"));
        vm.Message = "It broke.";
        await vm.SendCommand.Execute().ToTask();
        trailer.OnNext(TrailerB);

        vm.Message = "It broke twice.";
        await vm.SendCommand.Execute().ToTask();

        await Assert.That(api.Sent[1].ClientRequestId).IsNotEqualTo(api.Sent[0].ClientRequestId);
        await Assert.That(api.Sent[1].Message).IsEqualTo("It broke twice.\n\n" + TrailerB);
    }

    [Test]
    public async Task A_category_switch_after_a_refusal_mints_a_new_id() {
        var (vm, api, _, _) = New();
        api.Then(new FeedbackResult.RateLimited()).Then(new FeedbackResult.Sent("a@b.c"));
        vm.Message = "It broke.";
        await vm.SendCommand.Execute().ToTask();

        vm.Reopen(FeedbackCategory.Feedback);
        await vm.SendCommand.Execute().ToTask();

        await Assert.That(api.Sent[1].Category).IsEqualTo(FeedbackCategory.Feedback);
        await Assert.That(api.Sent[1].ClientRequestId).IsNotEqualTo(api.Sent[0].ClientRequestId);
    }

    [Test]
    public async Task A_lost_success_response_followed_by_an_edit_sends_a_new_id() {
        var (vm, api, _, _) = New();
        api.ThenThrow(new HttpRequestException("socket closed")).Then(new FeedbackResult.Sent("a@b.c"));
        vm.Message = "It broke.";
        await vm.SendCommand.Execute().ToTask();
        await Assert.That(vm.Outcome).IsEqualTo("Couldn't send the report: socket closed");
        await Assert.That(vm.CanSend).IsTrue();

        vm.Message = "It broke, really.";
        await vm.SendCommand.Execute().ToTask();

        await Assert.That(api.Sent[1].ClientRequestId).IsNotEqualTo(api.Sent[0].ClientRequestId);
    }

    [Test]
    public async Task Sent_then_send_another_starts_a_blank_report_with_a_new_id() {
        var (vm, api, _, _) = New();
        api.Then(new FeedbackResult.Sent("a@b.c"));
        vm.Message = "It broke.";
        await vm.SendCommand.Execute().ToTask();
        var first = vm.CurrentId;

        await vm.SendAnotherCommand.Execute().ToTask();

        await Assert.That(vm.IsSent).IsFalse();
        await Assert.That(vm.Message).IsEqualTo("");
        await Assert.That(vm.CurrentId).IsNotEqualTo(first);
    }

    [Test]
    public async Task Reopen_on_a_sent_window_starts_a_blank_report_with_the_requested_category() {
        var (vm, api, _, _) = New();
        api.Then(new FeedbackResult.Sent("a@b.c"));
        vm.Message = "It broke.";
        await vm.SendCommand.Execute().ToTask();

        vm.Reopen(FeedbackCategory.Feedback);

        await Assert.That(vm.IsSent).IsFalse();
        await Assert.That(vm.Category).IsEqualTo(FeedbackCategory.Feedback);
        await Assert.That(vm.Message).IsEqualTo("");
    }

    [Test]
    public async Task Every_refusal_shows_its_sentence_and_leaves_the_form_editable() {
        foreach (var refusal in new FeedbackResult[] {
                     new FeedbackResult.NotConfigured(), new FeedbackResult.Unavailable(), new FeedbackResult.NoEmailOnFile(),
                     new FeedbackResult.RateLimited(), new FeedbackResult.Invalid("Bad.") }) {
            var (vm, api, _, _) = New();
            api.Then(refusal);
            vm.Message = "It broke.";
            await vm.SendCommand.Execute().ToTask();

            await Assert.That(vm.Outcome).IsEqualTo(FeedbackResultMessages.ForRefusal(refusal));
            await Assert.That(vm.CanSend).IsTrue();
            await Assert.That(vm.SignInOffered).IsFalse();
        }
    }

    [Test]
    public async Task A_final_401_offers_sign_in() {
        var (vm, api, _, signIns) = New();
        api.ThenThrow(new CapacitorApiException(401, "unauthorized"));
        vm.Message = "It broke.";

        await vm.SendCommand.Execute().ToTask();

        await Assert.That(vm.Outcome).IsEqualTo(HomeViewModel.SignInExpiredNotice);
        await Assert.That(vm.SignInOffered).IsTrue();
        await vm.SignInCommand.Execute().ToTask();
        await Assert.That(signIns).HasCount().EqualTo(1);
    }

    [Test]
    public async Task Reopen_while_a_send_is_in_flight_does_not_touch_the_snapshot() {
        var api     = new BlockingFeedbackApi();
        var trailer = new BehaviorSubject<string>(TrailerA);
        var vm      = new FeedbackViewModel(api, FeedbackCategory.Bug, trailer, "macOS 15.6", null);
        vm.Message = "It broke.";
        var send = vm.SendCommand.Execute().ToTask();
        await api.Started.Task;

        vm.Reopen(FeedbackCategory.Feedback);
        await Assert.That(vm.Category).IsEqualTo(FeedbackCategory.Bug);
        await Assert.That(vm.IsBusy).IsTrue();

        api.Release(new FeedbackResult.Sent("a@b.c"));
        await send;
        await Assert.That(api.Sent[0].Category).IsEqualTo(FeedbackCategory.Bug);
    }

    sealed class BlockingFeedbackApi : IFeedbackApi {
        readonly TaskCompletionSource<FeedbackResult> _gate = new();
        public TaskCompletionSource Started { get; } = new();
        public List<FeedbackSubmission> Sent { get; } = [];
        public void Release(FeedbackResult result) => _gate.SetResult(result);
        public async Task<FeedbackResult> SubmitAsync(FeedbackSubmission submission, CancellationToken ct = default) {
            Sent.Add(submission);
            Started.TrySetResult();
            return await _gate.Task;
        }
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/FeedbackViewModelTests/*"`
Expected: build error — `FeedbackViewModel` does not exist.

- [ ] **Step 3: Implement the view model**

```csharp
// src/Capacitor.App/ViewModels/FeedbackViewModel.cs
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.Http;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// <summary>
/// One report, from the form to the lane. A pressed Send binds the report's content — category,
/// composed message, id — into a snapshot; an unchanged retry re-sends that snapshot, and any edit
/// releases it and mints a new id, so a retry can never quietly carry an older text or a newer
/// trailer under an id the server may already have accepted.
/// </summary>
public sealed class FeedbackViewModel : ReactiveObject, IDisposable {
    readonly IFeedbackApi _api;
    readonly string _os;
    readonly Action? _signIn;
    readonly CancellationTokenSource _lifetime;
    readonly CompositeDisposable _subscriptions = new();
    readonly ObservableAsPropertyHelper<bool> _canSend;
    readonly ObservableAsPropertyHelper<string> _hint;

    string _liveTrailer = "";
    FeedbackSubmission? _bound;
    string? _boundTrailer;
    Guid _id = Guid.NewGuid();
    FeedbackCategory _category;
    string _message = "";
    bool _isBusy;
    bool _isSent;
    string? _reporterEmail;
    string? _outcome;
    bool _signInOffered;

    public FeedbackViewModel(IFeedbackApi api, FeedbackCategory initial, IObservable<string> trailer,
            string osDescription, Action? signIn, CancellationToken appLifetime = default) {
        _api      = api;
        _os       = osDescription;
        _signIn   = signIn;
        _category = initial;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(appLifetime);

        trailer.Subscribe(t => { _liveTrailer = t; this.RaisePropertyChanged(nameof(Hint)); }).DisposeWith(_subscriptions);

        _canSend = this.WhenAnyValue(x => x.Message, x => x.IsBusy, x => x.IsSent, (m, busy, sent) =>
                !busy && !sent && m.Trim().Length > 0 && FeedbackMessageComposer.Remaining(m, EffectiveTrailer) >= 0)
            .ToProperty(this, x => x.CanSend)
            .DisposeWith(_subscriptions);

        _hint = this.WhenAnyValue(x => x.Message).Select(_ => BuildHint())
            .ToProperty(this, x => x.Hint, BuildHint())
            .DisposeWith(_subscriptions);

        SendCommand        = ReactiveCommand.CreateFromTask(SendAsync, this.WhenAnyValue(x => x.CanSend));
        SendAnotherCommand = ReactiveCommand.Create(() => StartNewReport(_category));
        SignInCommand      = ReactiveCommand.Create(() => { _signIn?.Invoke(); }, this.WhenAnyValue(x => x.SignInOffered));
    }

    public FeedbackCategory Category {
        get => _category;
        set { if (_category == value) return; Release(); this.RaiseAndSetIfChanged(ref _category, value); }
    }

    public string Message {
        get => _message;
        set { if (_message == value) return; Release(); this.RaiseAndSetIfChanged(ref _message, value); this.RaisePropertyChanged(nameof(Hint)); }
    }

    public string Hint => _hint.Value;
    public bool CanSend => _canSend.Value;
    public bool IsBusy { get => _isBusy; private set => this.RaiseAndSetIfChanged(ref _isBusy, value); }
    public bool IsSent { get => _isSent; private set => this.RaiseAndSetIfChanged(ref _isSent, value); }
    public string? ReporterEmail { get => _reporterEmail; private set => this.RaiseAndSetIfChanged(ref _reporterEmail, value); }
    public string? Outcome { get => _outcome; private set => this.RaiseAndSetIfChanged(ref _outcome, value); }
    public bool SignInOffered { get => _signInOffered; private set => this.RaiseAndSetIfChanged(ref _signInOffered, value); }
    public ReactiveCommand<Unit, Unit> SendCommand { get; }
    public ReactiveCommand<Unit, Unit> SendAnotherCommand { get; }
    public ReactiveCommand<Unit, Unit> SignInCommand { get; }
    internal Guid CurrentId => _id;

    /// <summary>A second entry-point click. Ignored mid-send; a sent window starts over.</summary>
    public void Reopen(FeedbackCategory category) {
        if (IsBusy) return;
        if (IsSent) { StartNewReport(category); return; }
        Category = category;
    }

    string EffectiveTrailer => _bound is not null ? _boundTrailer! : _liveTrailer;

    string BuildHint() {
        var remaining = FeedbackMessageComposer.Remaining(_message, EffectiveTrailer);
        var daemon    = EffectiveTrailer[(EffectiveTrailer.IndexOf("· daemon ", StringComparison.Ordinal) + 2)..];
        var app       = CapacitorVersion.CurrentDisplay();
        return $"Attached automatically: desktop {app} · {daemon} · {_os} · {remaining} characters left";
    }

    void Release() {
        if (_bound is null) return;
        _bound = null;
        _boundTrailer = null;
        _id = Guid.NewGuid();
        Outcome = null;
        SignInOffered = false;
    }

    void StartNewReport(FeedbackCategory category) {
        _bound = null;
        _boundTrailer = null;
        _id = Guid.NewGuid();
        IsSent = false;
        ReporterEmail = null;
        Outcome = null;
        SignInOffered = false;
        _category = category; this.RaisePropertyChanged(nameof(Category));
        _message = "";        this.RaisePropertyChanged(nameof(Message));
        this.RaisePropertyChanged(nameof(Hint));
    }

    async Task SendAsync() {
        if (_bound is null) {
            _boundTrailer = _liveTrailer;
            _bound = new FeedbackSubmission(_category, FeedbackMessageComposer.Compose(_message, _boundTrailer), _id, FeedbackSource.Desktop);
        }

        IsBusy = true;
        Outcome = null;
        SignInOffered = false;
        try {
            var result = await _api.SubmitAsync(_bound, _lifetime.Token);
            if (result is FeedbackResult.Sent(var email)) {
                ReporterEmail = email;
                IsSent = true;
                _bound = null;
                _boundTrailer = null;
                _id = Guid.NewGuid();
            } else {
                Outcome = FeedbackResultMessages.ForRefusal(result);
            }
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
            // The app is quitting; nothing to show.
        } catch (CapacitorApiException ex) when (ex.Status == 401) {
            Outcome = HomeViewModel.SignInExpiredNotice;
            SignInOffered = _signIn is not null;
        } catch (Exception ex) {
            Outcome = $"Couldn't send the report: {ex.Message}";
        } finally {
            IsBusy = false;
        }
    }

    public void Dispose() {
        _lifetime.Cancel();
        _lifetime.Dispose();
        _subscriptions.Dispose();
    }
}
```

Notes for the implementer: `HomeViewModel.SignInExpiredNotice` is `internal const` in the same assembly — reuse it, do not copy the string. The hint's daemon part is sliced from the trailer so that the hint and the trailer can never disagree; `FeedbackTrailer.Build` (Task 4) is what produces the observable's values (Task 9 wires it). Keep the doc comment at the top and nothing else: no comments narrating the tests.

- [ ] **Step 4: Run the tests to verify they pass**

Run: the Step 2 command. Expected: 12 passed. If `CanSend` lags a `Message` set in a test, `ObservableAsPropertyHelper` needs the `RxApp.MainThreadScheduler` the test host installs — check how `SettingsViewModelTests` constructs its view model and mirror its scheduler setup before changing the view model.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/ViewModels/FeedbackViewModel.cs test/Capacitor.App.Tests.Unit/FeedbackViewModelTests.cs
git commit -m "Add the desktop feedback form's view model" \
  -m "A Send binds category, composed text and id into one snapshot; an edit releases it and mints a new id." \
  -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: `FeedbackWindow`

**Files:**
- Create: `src/Capacitor.App/Views/FeedbackWindow.axaml`, `src/Capacitor.App/Views/FeedbackWindow.axaml.cs`
- Test: `test/Capacitor.App.Tests.Unit/FeedbackWindowSmokeTests.cs`

**Interfaces:**
- Consumes: `FeedbackViewModel` (Task 5).
- Produces: `FeedbackWindow : Window` with `DataContext = FeedbackViewModel`; named controls `BugChip`, `FeedbackChip`, `MessageInput`, `HintText`, `SendButton`, `CancelButton`, `OutcomeText`, `SentPanel`, `SendAnotherButton`, `SignInButton`.

- [ ] **Step 1: Write the failing smoke test**

```csharp
// test/Capacitor.App.Tests.Unit/FeedbackWindowSmokeTests.cs
using System.Reactive.Subjects;
using Avalonia.Controls;
using Avalonia.Threading;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.Http;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class FeedbackWindowSmokeTests {
    sealed class SentApi : IFeedbackApi {
        public Task<FeedbackResult> SubmitAsync(FeedbackSubmission submission, CancellationToken ct = default) =>
            Task.FromResult<FeedbackResult>(new FeedbackResult.Sent("a@b.c"));
    }

    [Test]
    public Task Window_reflects_the_category_gates_send_and_swaps_to_the_confirmation() => AvaloniaSession.RunOnUiAsync(async () => {
        var trailer = new BehaviorSubject<string>("Sent from Kurrent Capacitor Desktop 1.0.3 · daemon d 1.0.3");
        using var vm = new FeedbackViewModel(new SentApi(), FeedbackCategory.Feedback, trailer, "macOS 15.6", null);
        var window = new FeedbackWindow { DataContext = vm };
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var bug      = window.FindControl<RadioButton>("BugChip")!;
            var feedback = window.FindControl<RadioButton>("FeedbackChip")!;
            var message  = window.FindControl<TextBox>("MessageInput")!;
            var send     = window.FindControl<Button>("SendButton")!;
            var sent     = window.FindControl<StackPanel>("SentPanel")!;

            await Assert.That(feedback.IsChecked).IsEqualTo(true);
            await Assert.That(bug.IsChecked).IsEqualTo(false);
            await Assert.That(send.IsEnabled).IsFalse();
            await Assert.That(message.Classes.Contains("kcapField")).IsTrue();

            message.Text = "Nice.";
            Dispatcher.UIThread.RunJobs();
            await Assert.That(send.IsEnabled).IsTrue();

            send.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();

            await Assert.That(sent.IsVisible).IsTrue();
            await Assert.That(window.FindControl<TextBlock>("SentText")!.Text).IsEqualTo("Sent. Replies go to a@b.c.");
        } finally {
            window.Close();
        }
    });
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/FeedbackWindowSmokeTests/*"`
Expected: build error — `FeedbackWindow` does not exist.

- [ ] **Step 3: Write the window**

```xml
<!-- src/Capacitor.App/Views/FeedbackWindow.axaml -->
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="clr-namespace:Capacitor.App.ViewModels"
        xmlns:cmd="clr-namespace:Capacitor.Cli.Core.Commands;assembly=Capacitor.Cli.Core"
        x:Class="Capacitor.App.Views.FeedbackWindow"
        x:DataType="vm:FeedbackViewModel"
        Title="Kurrent Capacitor — Send feedback"
        Icon="avares://Kurrent Capacitor/Assets/kcap-icon.png"
        Width="560" Height="560" CanResize="False" WindowStartupLocation="CenterOwner"
        Background="{StaticResource KcapCanvasBrush}">
    <Grid Margin="28,24" RowDefinitions="Auto,*,Auto">
        <StackPanel Spacing="4">
            <TextBlock Classes="kcapTitle" Text="Send feedback" />
            <TextBlock Classes="kcapSubtitle" Text="Goes to Kurrent support. Replies arrive by email." />
        </StackPanel>

        <StackPanel Grid.Row="1" Margin="0,18,0,0" Spacing="18" IsVisible="{Binding !IsSent}">
            <StackPanel Spacing="8">
                <TextBlock Classes="kcapLabel" Text="What is this about?" />
                <StackPanel Orientation="Horizontal" Spacing="8">
                    <RadioButton x:Name="BugChip" Classes="kcapChoice" Content="Bug" GroupName="category"
                                 IsChecked="{Binding Category, Converter={x:Static views:FeedbackCategoryConverter.Instance}, ConverterParameter={x:Static cmd:FeedbackCategory.Bug}}" />
                    <RadioButton x:Name="FeedbackChip" Classes="kcapChoice" Content="Feedback" GroupName="category"
                                 IsChecked="{Binding Category, Converter={x:Static views:FeedbackCategoryConverter.Instance}, ConverterParameter={x:Static cmd:FeedbackCategory.Feedback}}" />
                </StackPanel>
            </StackPanel>
            <StackPanel Spacing="8">
                <TextBlock Classes="kcapLabel" Text="Message" />
                <TextBox x:Name="MessageInput" Classes="kcapField" AcceptsReturn="True" TextWrapping="Wrap" Height="170"
                         Watermark="What happened, and what you expected instead." Text="{Binding Message}" />
                <TextBlock x:Name="HintText" Classes="kcapHint" Text="{Binding Hint}" TextWrapping="Wrap" />
            </StackPanel>
            <TextBlock x:Name="OutcomeText" Classes="kcapHint" Foreground="{StaticResource KcapWarningBrush}"
                       Text="{Binding Outcome}" TextWrapping="Wrap"
                       IsVisible="{Binding Outcome, Converter={x:Static StringConverters.IsNotNullOrEmpty}}" />
        </StackPanel>

        <StackPanel x:Name="SentPanel" Grid.Row="1" Margin="0,18,0,0" Spacing="12" IsVisible="{Binding IsSent}">
            <TextBlock x:Name="SentText" Classes="kcapLabel" Text="{Binding ReporterEmail, StringFormat='Sent. Replies go to {0}.'}" />
        </StackPanel>

        <StackPanel Grid.Row="2" Orientation="Horizontal" HorizontalAlignment="Right" Spacing="10" Margin="0,18,0,0">
            <Button x:Name="SignInButton" Classes="kcapChip" Content="Sign in" Command="{Binding SignInCommand}"
                    IsVisible="{Binding SignInOffered}" />
            <Button x:Name="SendAnotherButton" Classes="kcapChip" Content="Send another" Command="{Binding SendAnotherCommand}"
                    IsVisible="{Binding IsSent}" />
            <Button x:Name="CancelButton" Classes="kcapChip" Content="Cancel" Click="OnCancel" IsVisible="{Binding !IsSent}" />
            <Button x:Name="CloseButton" Classes="kcapChip" Content="Close" Click="OnCancel" IsVisible="{Binding IsSent}" />
            <Button x:Name="SendButton" Classes="kcapPrimary" Content="Send" Command="{Binding SendCommand}" IsVisible="{Binding !IsSent}" />
        </StackPanel>
    </Grid>
</Window>
```

Add `xmlns:views="clr-namespace:Capacitor.App.Views"` to the root. If `App.axaml` has no `Button.kcapPrimary` style (check with `grep -n kcapPrimary src/Capacitor.App/App.axaml`), add one beside `Button.kcapChip` with `Background="{StaticResource KcapPrimaryBrush}"`, `Foreground="{StaticResource KcapOnPrimaryBrush}"`, `CornerRadius="8"`, `Padding="16,8"`, `FontWeight="SemiBold"`; likewise a `RadioButton.kcapChoice` style rendering the chip look (raised background, border, radius 8, padding 14,8) with the `:checked` state using `KcapPrimaryBrush`/`KcapOnPrimaryBrush`. Both are kit additions, one type per file does not apply to styles.

```csharp
// src/Capacitor.App/Views/FeedbackWindow.axaml.cs
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Capacitor.App.Views;

public partial class FeedbackWindow : Window {
    public FeedbackWindow() => InitializeComponent();

    void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
```

```csharp
// src/Capacitor.App/Views/FeedbackCategoryConverter.cs
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Capacitor.Cli.Core.Commands;

namespace Capacitor.App.Views;

/// <summary>Binds one radio chip to one enum value: checked when the bound category equals the
/// parameter, and writes the parameter back when the chip is checked.</summary>
public sealed class FeedbackCategoryConverter : IValueConverter {
    public static readonly FeedbackCategoryConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is FeedbackCategory current && parameter is FeedbackCategory wanted && current == wanted;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is FeedbackCategory wanted ? wanted : BindingOperations.DoNothing;
}
```

- [ ] **Step 4: Run the smoke test to verify it passes** — Step 2 command; 1 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/Views/FeedbackWindow.axaml src/Capacitor.App/Views/FeedbackWindow.axaml.cs \
        src/Capacitor.App/Views/FeedbackCategoryConverter.cs src/Capacitor.App/App.axaml \
        test/Capacitor.App.Tests.Unit/FeedbackWindowSmokeTests.cs
git commit -m "Add the desktop Send feedback window" \
  -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: Help-menu items

**Files:**
- Modify: `src/Capacitor.App/Views/AppMenuBar.cs`
- Test: `test/Capacitor.App.Tests.Unit/AppMenuBarTests.cs`

**Interfaces:**
- Consumes: `FeedbackCategory` (Task 1).
- Produces: `void AppMenuBar.SetFeedbackAction(Action<FeedbackCategory>? open)`.

- [ ] **Step 1: Write the failing tests** (append to the existing class; keep its helpers)

```csharp
[Test]
[NotInParallel("AvaloniaSession")]
public async Task Help_menu_offers_the_report_items_after_a_separator() {
    var layout = await AvaloniaSession.DispatchAsync(() => Layout(Submenu(NewBar().Build(new Window()), "Help")));

    await Assert.That(layout).IsEqualTo("Kurrent Capacitor Documentation|Changelog|-|Report a Bug…|Send Feedback…");
}

[Test]
[NotInParallel("AvaloniaSession")]
public async Task Report_items_are_disabled_until_an_action_exists_and_then_click_through_with_their_category() {
    var opened = new List<FeedbackCategory>();
    var (bugBefore, feedbackBefore, bugAfter, feedbackAfter) = await AvaloniaSession.DispatchAsync(() => {
        var bar    = NewBar();
        var window = new Window();
        bar.Attach(window);
        var help = Submenu(NativeMenu.GetMenu(window)!, "Help");
        var bug = Item(help, "Report a Bug…"); var feedback = Item(help, "Send Feedback…");
        var before = (bug.IsEnabled, feedback.IsEnabled);
        bar.SetFeedbackAction(opened.Add);
        Click(bug); Click(feedback);
        return (before.Item1, before.Item2, bug.IsEnabled, feedback.IsEnabled);
    });

    await Assert.That(bugBefore).IsFalse();
    await Assert.That(feedbackBefore).IsFalse();
    await Assert.That(bugAfter).IsTrue();
    await Assert.That(feedbackAfter).IsTrue();
    await Assert.That(opened).IsEquivalentTo([FeedbackCategory.Bug, FeedbackCategory.Feedback]);
}

[Test]
[NotInParallel("AvaloniaSession")]
public async Task A_window_attached_after_the_action_is_set_gets_enabled_items() {
    var enabled = await AvaloniaSession.DispatchAsync(() => {
        var bar = NewBar();
        bar.SetFeedbackAction(_ => { });
        var window = new Window();
        bar.Attach(window);
        return Item(Submenu(NativeMenu.GetMenu(window)!, "Help"), "Report a Bug…").IsEnabled;
    });

    await Assert.That(enabled).IsTrue();
}

[Test]
[NotInParallel("AvaloniaSession")]
public async Task Closing_an_attached_window_drops_its_items_from_later_updates() {
    var (liveEnabled, closedEnabled) = await AvaloniaSession.DispatchAsync(() => {
        var bar = NewBar();
        var live = new Window(); var closed = new Window();
        bar.Attach(live); bar.Attach(closed);
        var closedItem = Item(Submenu(NativeMenu.GetMenu(closed)!, "Help"), "Report a Bug…");
        closed.Show(); closed.Close();
        bar.SetFeedbackAction(_ => { });
        return (Item(Submenu(NativeMenu.GetMenu(live)!, "Help"), "Report a Bug…").IsEnabled, closedItem.IsEnabled);
    });

    await Assert.That(liveEnabled).IsTrue();
    await Assert.That(closedEnabled).IsFalse();
}
```

Add `using Capacitor.Cli.Core.Commands;` to the test file.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/AppMenuBarTests/*"`
Expected: the new tests fail (layout mismatch / `SetFeedbackAction` missing); the existing four still pass.

- [ ] **Step 3: Implement**

In `AppMenuBar`:

```csharp
Action<FeedbackCategory>? _openFeedback;
readonly List<ReportItems> _reportItems = [];

sealed record ReportItems(Window Window, NativeMenuItem Bug, NativeMenuItem Feedback);

public void SetFeedbackAction(Action<FeedbackCategory>? open) {
    _openFeedback = open;
    foreach (var items in _reportItems) {
        items.Bug.IsEnabled = open is not null;
        items.Feedback.IsEnabled = open is not null;
    }
}

public NativeMenu Build(Window window) => new() {
    new NativeMenuItem(WindowMenuTitle) { Menu = BuildWindowMenu(window) },
    new NativeMenuItem(HelpMenuTitle) { Menu = BuildHelpMenu(window) },
};

NativeMenu BuildHelpMenu(Window window) {
    // Resolved at click time: an item enabled by a later SetFeedbackAction must invoke that action.
    var bug      = Item("Report a Bug…",  () => _openFeedback?.Invoke(FeedbackCategory.Bug),      enabled: _openFeedback is not null);
    var feedback = Item("Send Feedback…", () => _openFeedback?.Invoke(FeedbackCategory.Feedback), enabled: _openFeedback is not null);
    var items    = new ReportItems(window, bug, feedback);
    _reportItems.Add(items);
    window.Closed += (_, _) => _reportItems.Remove(items);

    return new NativeMenu {
        Item("Kurrent Capacitor Documentation", () => LinkPolicy.Open(opener, DocsUrl)),
        Item("Changelog", () => LinkPolicy.Open(opener, ChangelogUrl)),
        new NativeMenuItemSeparator(),
        bug,
        feedback,
    };
}
```

Move `ReportItems` to its own file `src/Capacitor.App/Views/ReportItems.cs` (one type per file) — a `sealed record` in the `Capacitor.App.Views` namespace, `internal`. Add `using Capacitor.Cli.Core.Commands;`.

- [ ] **Step 4: Run the tests to verify they pass** — Step 2 command; all passed.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/Views/AppMenuBar.cs src/Capacitor.App/Views/ReportItems.cs test/Capacitor.App.Tests.Unit/AppMenuBarTests.cs
git commit -m "Offer bug reports and feedback from the Help menu" \
  -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: Rail footer help button and flyout

**Files:**
- Modify: `src/Capacitor.App/Views/SessionRailView.axaml`, `src/Capacitor.App/ViewModels/MainWindowViewModel.cs`
- Test: `test/Capacitor.App.Tests.Unit/MainWindowSmokeTests.cs`, `test/Capacitor.App.Tests.Unit/MainWindowViewModelTests.cs`

**Interfaces:**
- Consumes: `FeedbackCategory`, `AppMenuBar.DocsUrl`, `LinkPolicy.Open`, `IUrlOpener`.
- Produces on `MainWindowViewModel`: constructor parameters `Action<FeedbackCategory>? openFeedback = null, IUrlOpener? opener = null` (append to the optional list); `ReactiveCommand<Unit, Unit> OpenDocsCommand`; `ReactiveCommand<FeedbackCategory, Unit> OpenFeedbackCommand`; `bool CanOpenFeedback`.

- [ ] **Step 1: Write the failing view-model test** (append to `MainWindowViewModelTests`, using that file's existing factory for a view model; pass the two new arguments)

```csharp
[Test]
public async Task Footer_help_commands_follow_the_feedback_action() {
    var opened = new List<FeedbackCategory>();
    var opener = new RecordingOpener();
    var without = NewViewModel();                                            // the file's factory, no action
    var with    = NewViewModel(openFeedback: opened.Add, opener: opener);

    await Assert.That(without.CanOpenFeedback).IsFalse();
    await Assert.That(with.CanOpenFeedback).IsTrue();
    await with.OpenFeedbackCommand.Execute(FeedbackCategory.Feedback).ToTask();
    await with.OpenDocsCommand.Execute().ToTask();
    await Assert.That(opened).IsEquivalentTo([FeedbackCategory.Feedback]);
    await Assert.That(opener.Opened).IsEquivalentTo([AppMenuBar.DocsUrl]);
}
```

(`RecordingOpener` lives in `RecordingAppServices.cs`; read it for the name of its recorded-URL list and use that.)

- [ ] **Step 2: Write the failing smoke test** (append to `MainWindowSmokeTests`, using that file's window factory)

```csharp
[Test]
[NotInParallel("AvaloniaSession")]
public Task Rail_footer_offers_help_with_docs_always_and_reports_only_with_a_server() => AvaloniaSession.RunOnUiAsync(async () => {
    var (window, _) = NewWindow(openFeedback: null);          // extend the factory to forward the new arguments
    try {
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var help = window.FindDescendantOfType<SessionRailView>()!.FindControl<Button>("RailHelpButton")!;
        await Assert.That(help.IsEnabled).IsTrue();
        await Assert.That(ToolTip.GetTip(help)).IsEqualTo("Help and support");
        await Assert.That(AutomationProperties.GetName(help)).IsEqualTo("Help and support");
        var flyout = (MenuFlyout)help.Flyout!;
        var items  = flyout.Items.OfType<MenuItem>().ToList();
        await Assert.That(items.Select(i => i.Header)).IsEquivalentTo(["Documentation", "Report a bug…", "Send feedback…"]);
        await Assert.That(items[0].IsEnabled).IsTrue();
        await Assert.That(items[1].IsEnabled).IsFalse();
        await Assert.That(items[2].IsEnabled).IsFalse();
    } finally {
        window.Close();
    }
});
```

- [ ] **Step 3: Run both to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/MainWindowViewModelTests/Footer_help*"` and the `MainWindowSmokeTests/Rail_footer*` filter.
Expected: build errors.

- [ ] **Step 4: Add the commands to `MainWindowViewModel`**

Append `Action<FeedbackCategory>? openFeedback = null, IUrlOpener? opener = null` to the constructor's optional parameters and, beside the other command constructions:

```csharp
CanOpenFeedback     = openFeedback is not null;
OpenFeedbackCommand = ReactiveCommand.Create<FeedbackCategory>(c => openFeedback?.Invoke(c), Observable.Return(CanOpenFeedback));
OpenDocsCommand     = ReactiveCommand.Create(() => LinkPolicy.Open(opener ?? new ShellUrlOpener(), AppMenuBar.DocsUrl));
```

with the three public members declared beside `CloseWorkspaceCommand`. Add the `using`s for `Capacitor.App.Views`, `Capacitor.App.Services` and `Capacitor.Cli.Core.Commands`.

- [ ] **Step 5: Add the footer button**

In `SessionRailView.axaml`, replace the right-docked `HostedText` TextBlock with a right-docked horizontal `StackPanel` holding it and the button:

```xml
<StackPanel DockPanel.Dock="Right" Orientation="Horizontal" Spacing="8">
    <TextBlock Text="{Binding Rail.HostedText, FallbackValue=''}" FontSize="11.5"
               Foreground="{StaticResource KcapFaintBrush}" VerticalAlignment="Center" />
    <Button x:Name="RailHelpButton" Classes="kcapChip" Width="22" Height="22" Padding="0" CornerRadius="6"
            ToolTip.Tip="Help and support" AutomationProperties.Name="Help and support" VerticalAlignment="Center">
        <PathIcon Width="14" Height="14" Foreground="{StaticResource KcapMutedBrush}"
                  Data="M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zm0 1.8a7.2 7.2 0 1 1 0 14.4 7.2 7.2 0 0 1 0-14.4zm0 3.2a3 3 0 0 0-3 3h1.8a1.2 1.2 0 1 1 1.7 1.1c-.9.4-1.4 1.1-1.4 2.2h1.8c0-.5.2-.8.7-1a3 3 0 0 0-1.6-5.3zM11 15.5v1.8h1.8v-1.8z" />
        <Button.Flyout>
            <MenuFlyout Placement="TopEdgeAlignedRight" FlyoutPresenterClasses="kcapPanel">
                <MenuItem Header="Documentation" Command="{Binding OpenDocsCommand}" />
                <MenuItem Header="Report a bug…" Command="{Binding OpenFeedbackCommand}" CommandParameter="{x:Static cmd:FeedbackCategory.Bug}" />
                <MenuItem Header="Send feedback…" Command="{Binding OpenFeedbackCommand}" CommandParameter="{x:Static cmd:FeedbackCategory.Feedback}" />
            </MenuFlyout>
        </Button.Flyout>
    </Button>
</StackPanel>
```

Add `xmlns:cmd="clr-namespace:Capacitor.Cli.Core.Commands;assembly=Capacitor.Cli.Core"` to the root element. `ToolTip.Tip` on the button takes precedence over the footer `Border`'s tooltip for the pointer over the button.

- [ ] **Step 6: Run the tests to verify they pass** — Step 3 commands; all passed.

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.App/Views/SessionRailView.axaml src/Capacitor.App/ViewModels/MainWindowViewModel.cs \
        test/Capacitor.App.Tests.Unit/MainWindowSmokeTests.cs test/Capacitor.App.Tests.Unit/MainWindowViewModelTests.cs
git commit -m "Put help and support one click away in the rail footer" \
  -m "Documentation stays enabled without a server; only the report items follow it." \
  -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 9: Wire the window, the oracle and the menu in `App`

**Files:**
- Modify: `src/Capacitor.App/App.axaml.cs`
- Test: `test/Capacitor.App.Tests.Unit/DesktopWindowLifecycleTests.cs` (and `AppStartupTests.cs` if the startup factory there is the one that shows the main window)

**Interfaces:**
- Consumes: everything above; `App.ServerHttp(profiles)` (existing), `OpenSignInDialog(profiles, notifier)` (existing, the `requestSignIn` action at L600), `DaemonLifecycleController.CliVersion`, `IDaemonClientService.Snapshots` / `DaemonName`.
- Produces: `internal void OpenFeedback(IClassicDesktopStyleApplicationLifetime desktop, FeedbackCategory category)`, `_feedbackWindow` field, `_menuBar` field.

- [ ] **Step 1: Read the two places to change**

`grep -n "new AppMenuBar" src/Capacitor.App/App.axaml.cs` (the bar is a local today; it becomes the `_menuBar` field). `grep -n "_coordinator.ShowMainWindow()" src/Capacitor.App/App.axaml.cs` (the oracle is evaluated just above it). `grep -n "requestSignIn = " src/Capacitor.App/App.axaml.cs` (the sign-in action to reuse). `grep -n "_settingsWindow" src/Capacitor.App/App.axaml.cs` (every site, including the shutdown close at ~L1645 — `_feedbackWindow` gets a twin at each).

- [ ] **Step 2: Write the failing lifecycle tests** (append to `DesktopWindowLifecycleTests`; follow its existing pattern for driving `App` with `FakeClassicDesktopLifetime` — read the file's first test before writing)

```csharp
[Test]
[NotInParallel("AvaloniaSession")]
public Task Feedback_window_is_single_instance_and_switches_category() => AvaloniaSession.RunOnUiAsync(async () => {
    var app = NewApp();                                    // the file's factory
    app.OpenFeedback(Desktop, FeedbackCategory.Bug);
    var first = app.FeedbackWindowForTests!;
    app.OpenFeedback(Desktop, FeedbackCategory.Feedback);

    await Assert.That(app.FeedbackWindowForTests).IsSameReferenceAs(first);
    await Assert.That(((FeedbackViewModel)first.DataContext!).Category).IsEqualTo(FeedbackCategory.Feedback);
    first.Close();
    await Assert.That(app.FeedbackWindowForTests).IsNull();
});

[Test]
[NotInParallel("AvaloniaSession")]
public Task Shutdown_closes_the_feedback_window() => AvaloniaSession.RunOnUiAsync(async () => {
    var app = NewApp();
    app.OpenFeedback(Desktop, FeedbackCategory.Bug);

    await app.DisposeAndShutdownAsync();                   // the file's shutdown entry point

    await Assert.That(app.FeedbackWindowForTests).IsNull();
});
```

- [ ] **Step 3: Run them to verify they fail** — filter `/*/*/DesktopWindowLifecycleTests/Feedback*` and `/Shutdown_closes_the_feedback*`; build errors.

- [ ] **Step 4: Implement**

```csharp
FeedbackWindow? _feedbackWindow;
internal FeedbackWindow? FeedbackWindowForTests => _feedbackWindow;
AppMenuBar? _menuBar;
```

Where the bar is created: `_menuBar = new AppMenuBar(new ShellUrlOpener(), () => desktop.Windows, () => MainWindowAction(_coordinator)); _menuBar.Install();`.

Just above `_coordinator.ShowMainWindow()` in `StartAsync`, after `profiles` and `service` exist:

```csharp
Action<FeedbackCategory>? openFeedback = ServerHttp(profiles) is not null
    ? category => OpenFeedback(desktop, category)
    : null;
_menuBar?.SetFeedbackAction(openFeedback);
```

and thread `openFeedback` into the `MainWindowViewModel` construction the coordinator performs (find `new MainWindowViewModel(` — it is built inside `BuildAndShowMainWindow`; add the `openFeedback:` argument beside `tenantName:`/`laneStatus:`, and `opener: new ShellUrlOpener()`).

```csharp
internal void OpenFeedback(IClassicDesktopStyleApplicationLifetime desktop, FeedbackCategory category) {
    if (_shutdownStarted) return;
    if (_feedbackWindow is { } open) {
        if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
        open.Activate();
        ((FeedbackViewModel)open.DataContext!).Reopen(category);
        return;
    }

    var api = _serverHttp?.GetRequiredService<IFeedbackApi>();
    if (api is null) return;

    var service    = _service!;                                   // the IDaemonClientService StartAsync built
    var appVersion = CapacitorVersion.CurrentDisplay();
    var trailer    = service.Snapshots
        .Select(s => FeedbackTrailer.Build(appVersion, service.DaemonName, s.Daemon.Version, _lifecycle?.CliVersion))
        .StartWith(FeedbackTrailer.Build(appVersion, service.DaemonName, null, _lifecycle?.CliVersion));
    var vm = new FeedbackViewModel(api, category, trailer, RuntimeInformation.OSDescription,
        signIn: () => OpenSignInDialog(_profiles, _notifier), appLifetime: _shutdown.Token);

    var window = new FeedbackWindow { DataContext = vm };
    _feedbackWindow = window;
    window.Closing += (_, e) => { if (vm.IsBusy && !_shutdownStarted) e.Cancel = true; };
    window.Closed  += (_, _) => { _feedbackWindow = null; vm.Dispose(); };
    window.Show();
    window.Activate();
}
```

`_service`, `_lifecycle`, `_profiles` and `_notifier` are whatever fields (or captured locals promoted to fields) `StartAsync` already holds for the daemon client service, the `DaemonLifecycleController`, the resolved `ProfileContext` and the `IAppNotifier` — use the existing names; promote a local to a field only if no field exists, with the same lifetime `_settingsWindow`'s inputs have. In the shutdown path that closes `_settingsWindow`, add `if (_feedbackWindow is { } feedback) feedback.Close();`.

- [ ] **Step 5: Run the lifecycle tests, then the four earlier App test classes once more**

Run: filters `DesktopWindowLifecycleTests/*`, `AppMenuBarTests/*`, `MainWindowSmokeTests/*`, `FeedbackViewModelTests/*`, `FeedbackWindowSmokeTests/*`. Expected: all passed.

- [ ] **Step 6: Build the whole app once and run it**

Run: `dotnet build src/Capacitor.App/Capacitor.App.csproj` then `dotnet run --project src/Capacitor.App/Capacitor.App.csproj`. Check by hand: Help → Send Feedback… opens the window with Feedback selected; the rail footer button shows the tooltip and the three items; with a signed-in profile a Send lands a thread in the Capacitor Plain workspace and the confirmation names the reporter's email. Note the thread id in the PR description.

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.App/App.axaml.cs test/Capacitor.App.Tests.Unit/DesktopWindowLifecycleTests.cs
git commit -m "Open the feedback window from the menu, the rail and one server oracle" \
  -m "The oracle is a resolved server URL, evaluated before the main window is shown." \
  -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 10: Documentation

**Files:**
- Modify: `docs/CHANGES.md` (new entry at the top, the existing entry style: an `##` heading naming the shape, then the reasoning a diff would not show), `README.md` (the desktop section names the Help menu and the footer button as the way to reach support).

- [ ] **Step 1: Write the CHANGES entry**

```markdown
## Support from the desktop rides the feedback lane

The desktop app has no web view, so the Plain chat widget the web app uses cannot embed; instead a
native "Send feedback" window posts through the same `POST /api/feedback` lane `kcap feedback` uses.
Three things are deliberate. The message the server receives is the reporter's text plus one trailer
line naming the client and daemon, because the lane's context has no slot for the daemon and the
server caps the context fields — the hint under the form discloses the trailer and counts it
against the 8000-character cap. A pressed Send binds the report into a snapshot with its
`client_request_id`; an unchanged retry re-sends the snapshot, any edit mints a new id, and three
duplicate cases are accepted rather than solved (an unchanged retry after Plain accepted but the
tenant answered non-success, a changed retry after an ambiguous failure, a quit mid-call). The
report entries follow one oracle — a resolved server URL — evaluated before the main window is
shown, while Documentation stays reachable without a server; on Windows and Linux the rail-footer
flyout is the only entry, since those builds draw no menu bar. `context.source = "desktop"` reaches
the tenant server and stops there; the trailer is what support reads.
```

- [ ] **Step 2: Update the README desktop section** — one sentence: "Help → Report a Bug… / Send Feedback… (or the help button in the session rail's footer) sends a report to Kurrent support; replies arrive by email."

- [ ] **Step 3: Commit**

```bash
git add docs/CHANGES.md README.md
git commit -m "Document how the desktop app reaches support" \
  -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Self-review

- **Spec coverage:** D1 (Tasks 5, 9), D2 items 1–3 (Tasks 7, 8; tray untouched), D3 oracle + click-time resolution + retention/removal (Tasks 7, 9), D4 window/lifecycle/form/composition/trailer timing/snapshot/outcomes/401 (Tasks 4, 5, 6, 9), D5 typed lane, wire mapping in one place, shared refusals, CLI unchanged (Tasks 1, 2), D6 exclusions honoured (no tray item, no Question, no probe), Delivery = one PR, Testing section: every named test class has a task. Docs (Task 10).
- **Placeholder scan:** none; each code step carries the code.
- **Type consistency:** `FeedbackSubmission(FeedbackCategory, string, Guid, FeedbackSource)` (Task 1) is what Task 2's command, Task 5's view model and the fakes construct; `FeedbackResultMessages.ForRefusal` (Task 2) is what Task 5 shows; `FeedbackMessageComposer.Compose/Remaining` (Task 3) and `FeedbackTrailer.Build` (Task 4) are what Task 5 and Task 9 call; `Reopen`, `IsBusy`, `CurrentId` (Task 5) are what Tasks 6 and 9 use; `SetFeedbackAction` (Task 7) and `openFeedback`/`opener` (Task 8) are what Task 9 wires.
