# AI-688 gap 1 — gated live E2E verification report

## What was added

`test/Capacitor.Cli.Tests.Unit/Services/AcpHostedAgentRuntimeFactoryLiveTests.cs` — one TUnit test,
`StartAsync_AgainstRealCursorAgentAcp_SelectsModelAndProducesHelloTurn`, gated behind
`KCAP_ACP_LIVE=1`. It:

1. Calls `Skip.Unless(Environment.GetEnvironmentVariable("KCAP_ACP_LIVE") == "1", ...)` as the very
   first statement — a real TUnit dynamic skip (`TUnit.Core.Skip.Unless`), not an early `return`.
2. Constructs the REAL `AcpHostedAgentRuntimeFactory` with `connectionSource: null` (its default —
   the real `Process.Start("cursor-agent", "acp")` path, `AcpHostedAgentRuntimeFactory.StartRealProcess`),
   a real `DaemonConfig()` (defaults: `CursorPath = "cursor-agent"`, `CursorModel = "claude-sonnet-4-5"`),
   a real (console) `ILoggerFactory` at Debug level (so any `TrySelectModelAsync` warning would
   surface — see "Model-selection evidence" below), and a `CaptureServerConnection` stub (mirrors the
   existing `AcpHostedAgentRuntimeFactoryTests.CaptureServerConnection` pattern) whose
   `RequestAcpInteractionAsync` is not expected to fire for a tool-free prompt.
3. Builds a `RuntimeStartContext` with a throwaway `Directory.CreateTempSubdirectory` cwd wrapped in
   `WorktreeInfo.Borrowed(...)`, `Model = ""` (forces the `DaemonConfig.CursorModel` fallback path in
   `AcpHostedAgentRuntimeFactory.ResolveRequestedModel`), and
   `Prompt = "Respond with only the single word HELLO and nothing else."`.
4. Calls `factory.StartAsync(ctx, ct)`, casts the returned `IHostedAgentRuntime` to the concrete
   `AcpHostedAgentRuntime` to reach `.Updates`, and drains that channel for up to 40s, concatenating
   every `agent_message_chunk`'s text (Cursor streams the answer across multiple chunks) until the
   buffer contains "HELLO" (case-insensitive) or the timeout elapses. Asserts the HELLO chunk WAS
   observed. Disposes the runtime and cleans up the temp dir in `finally`.

No production code was touched — this is a test-only addition on top of commit `eb0d221` (gap 1's
implementation, already on this branch).

## Live run — gate ON (`KCAP_ACP_LIVE=1`)

Environment: `cursor-agent` `2026.07.01-41b2de7` on PATH, `cursor-agent status` → logged in as
`tony.young@kurrent.io`, `cursor-agent about` → **Subscription Tier: Team** (required — see
`docs/ai-688-cursor-prototype-findings.md`'s Free-tier plan-limit gotcha).

Ran twice for reproducibility:

```
KCAP_ACP_LIVE=1 dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -c Debug --treenode-filter "/*/*/AcpHostedAgentRuntimeFactoryLiveTests/*"
```

Both runs: **Passed** (1/1), ~9-10s wall time (real process spawn + real Cursor turn).

### Run 1 (33 `session/update` notifications observed)

Key lines (full JSON in the HTML report at
`TestResults/Capacitor.Cli.Tests.Unit-macos-net10.0-report.html`):

```
kind=AvailableCommands  raw={"sessionUpdate":"available_commands_update", ...}
kind=AgentThoughtChunk  text=The
kind=AgentThoughtChunk  text= user is
...
kind=Unknown            raw={"sessionUpdate":"session_info_update","title":"Hello Responder"}
kind=AgentThoughtChunk  text= HELLO
kind=AgentMessageChunk  text=HELLO   raw={"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"HELLO"}}
concatenated agent_message_chunk text: "HELLO"
```

### Run 2 (15 `session/update` notifications observed)

```
kind=AvailableCommands  raw={"sessionUpdate":"available_commands_update", ...}
kind=AgentThoughtChunk  text=The user is asking me to respond with
kind=AgentThoughtChunk  text= only the single
kind=AgentThoughtChunk  text= word "HELLO" and nothing else
...
kind=AgentThoughtChunk  text= "HELLO" without any additional
kind=AgentThoughtChunk  text= calls, or explanations.
kind=AgentMessageChunk  text=HELLO   raw={"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"HELLO"}}
concatenated agent_message_chunk text: "HELLO"
```

**Both runs: the `agent_message_chunk` answer was exactly `"HELLO"`.** The chunk count and
`agent_thought_chunk` wording vary run-to-run (Cursor's normal non-determinism), but the final
`agent_message_chunk` text was identical both times.

### Model-selection evidence

`session/set_config_option` is a request/response the daemon's `AcpConnection` doesn't log on the
wire (only malformed-frame cases hit `LogDebug`), and it isn't itself a `session/update` notification
— so it never appears in the `Updates` channel this test drains. To still get signal on it, the test
uses a real (not Null) `ILoggerFactory` at Debug level, since
`AcpHostedAgentRuntime.TrySelectModelAsync` explicitly `LogWarning`s on either of its two failure
paths (model not found in `session/new`'s `availableModels`, or the `session/set_config_option`
request itself throwing) and is silent on success. **Neither warning fired in either run** — combined
with the real turn completing end-to-end (StartAsync's model-selection step runs synchronously
*before* the initial prompt fires; a hang or unhandled throw there would have prevented the prompt
from firing at all), this is consistent with `session/set_config_option` resolving
`"claude-sonnet-4-5"` (the `DaemonConfig.CursorModel` default) against the live account's
`claude-sonnet-4-5[thinking=true,context=200k]` entry (confirmed present in the probe's 32-model list,
`docs/ai-688-cursor-prototype-findings.md`) and succeeding.

## Gate-off run — confirms it skips

```
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -c Debug --no-build --treenode-filter "/*/*/AcpHostedAgentRuntimeFactoryLiveTests/*"
```

```
skipped StartAsync_AgainstRealCursorAgentAcp_SelectsModelAndProducesHelloTurn (20ms)
  Gated live E2E against a real 'cursor-agent acp' process — set KCAP_ACP_LIVE=1 to run (spends a real Cursor turn; requires an authenticated Team-tier `cursor-agent` on PATH).

total: 1, failed: 0, succeeded: 0, skipped: 1, duration: 477ms
```

20ms duration (vs. ~9-10s for the live run) and a `ps aux | grep "cursor-agent acp"` immediately after
showed no such process — confirms `Skip.Unless` bails out before `AcpHostedAgentRuntimeFactory` ever
spawns anything.

## Build verification

- `dotnet build test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -c Debug` — 0 errors.
- `dotnet build test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -c Release` — 0 errors,
  no new warnings attributable to the new file.
- Existing `AcpHostedAgentRuntimeFactoryTests` (the fake-based coverage) still pass (3/3) — this
  addition didn't disturb the existing seam.

## Concerns / follow-ups

- This test spends a real Cursor turn (billed against the Team account) every time it's run with the
  gate on — intentionally not part of any default/CI run.
- `session/set_config_option` itself is not directly observable from the `Updates` channel (see
  above); the evidence for it succeeding is indirect (absence of the two logged failure paths + the
  turn completing). A stronger direct assertion would need either a wire-sniffing `connectionSource`
  wrapper around the real process's streams, or a daemon-side `LogInformation` on the
  `TrySelectModelAsync` success path — the latter is a small, non-behavioral production change
  (`AcpHostedAgentRuntime.cs`) that was deliberately NOT made here per the "don't touch gap 1's
  production code" constraint; worth a small follow-up if stronger direct proof is wanted later.
- Test asserts on the exact substring `"HELLO"` in the concatenated `agent_message_chunk` text; if
  Cursor's model ever prepends punctuation/whitespace-only chunks the `Contains` check already
  tolerates that, but a future model that answers with a full sentence instead of the bare word would
  still pass (the prompt asks for the bare word, but the assertion doesn't enforce exclusivity) — an
  acceptable looseness for an E2E smoke check, not a strict wire-format test.
