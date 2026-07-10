# AI-688 Option B — Task 5 (capstone): live tool-using E2E + real wire-shape confirmation report

## What was done

1. **Step 1 (OBSERVE)** — wrote a throwaway python probe (extends the gap-1 probe pattern:
   newline-delimited JSON-RPC over stdio, minimal `clientCapabilities` matching
   `AcpHostedAgentRuntime.StartAsync`'s real `InitializeParams`) and ran it against a real
   `cursor-agent acp` process with model `claude-sonnet-4-5[thinking=true,context=200k]` and the
   prompt *"Use your shell/command tool to run exactly `echo kcap-e2e-marker` and report the
   output."* Captured all 43 `session/update` notifications + the one `session/request_permission`
   the agent sent. Appended a new "Tool-using turn (AI-688 task 5)" section to
   `docs/ai-688-cursor-prototype-findings.md` with verbatim shapes for every kind observed
   (`tool_call`, `tool_call_update`×2, `agent_thought_chunk`, `session/request_permission` +
   our response, plus the new `session_info_update` kind) and the fs/terminal negative result.
2. **Step 2 (reconcile)** — concluded, and documented in the findings doc, that **no changes are
   needed** to `AcpSessionUpdate.Reduce()` or `AcpEventTranslator` — every real shape observed
   matches an assumption those already encode, including the FALLBACK paths (`ExtractToolResultText`'s
   `rawOutput` fallback is exactly what fired for the real `tool_call_update`, and the unrecognized
   `session_info_update` kind safely falls to `AcpUpdateKind.Unknown` as designed). No production
   code was touched by this task.
3. **Step 3 (live E2E)** — added
   `test/Capacitor.Cli.Tests.Unit/Services/AcpHostedAgentRuntimeFactoryToolUseLiveTests.cs`, one
   TUnit test gated behind `KCAP_ACP_LIVE=1` (`Skip.Unless`, same mechanism as
   `AcpHostedAgentRuntimeFactoryLiveTests`). It constructs the REAL `AcpHostedAgentRuntimeFactory`
   (`connectionSource: null` → real `cursor-agent acp` spawn) with the SAME tool-forcing prompt/model
   as the Step 1 probe, wires a new `AutoApproveServerConnection` (a `ServerConnection` subclass
   whose `RequestAcpInteractionAsync` picks an `allow_once`-flavored offered option and returns
   `Outcome: "allow_once"` + that option's `OptionId` — matching `AcpInteractionBridge.MapPermissionDecision`'s
   allowlist/id-match contract exactly), then reads `HostedRuntimeStart.Transcript!.Envelopes` (task
   2/4's bind-handoff shape — no downcasting to the concrete runtime type) for up to 90s. Asserts
   (resiliently — real-model non-determinism, this is a manual/gated E2E not a CI gate) that a
   `UserMessage` and an `AssistantText` envelope were surfaced, and separately reports (without
   hard-failing on model choice) whether `ToolCall`/`ToolResult` envelopes appeared.

## Step 1 — real observed shapes (verbatim; full detail + more context in the findings doc)

`tool_call`:
```json
{"sessionUpdate":"tool_call","toolCallId":"toolu_bdrk_01WHvLzppLFXQQdgguTnpoVs","title":"`echo kcap-e2e-marker`","kind":"execute","status":"pending","rawInput":{"command":"echo kcap-e2e-marker"}}
```

`tool_call_update` (terminal):
```json
{"sessionUpdate":"tool_call_update","toolCallId":"toolu_bdrk_01WHvLzppLFXQQdgguTnpoVs","status":"completed","rawOutput":{"exitCode":0,"stdout":"kcap-e2e-marker\n","stderr":""}}
```

`agent_thought_chunk`:
```json
{"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":"The user wants me to run a specific shell command:"}}
```

`session/request_permission` (agent→client REQUEST, `id: 0`):
```json
{"jsonrpc":"2.0","id":0,"method":"session/request_permission","params":{"sessionId":"...","toolCall":{"toolCallId":"...","title":"`echo kcap-e2e-marker`","kind":"execute","status":"pending","content":[{"type":"content","content":{"type":"text","text":"Not in allowlist: echo"}}]},"options":[{"optionId":"allow-once","name":"Allow once","kind":"allow_once"},{"optionId":"allow-always","name":"Allow always","kind":"allow_always"},{"optionId":"reject-once","name":"Reject","kind":"reject_once"}]}}
```
We answered `{"outcome":{"outcome":"selected","optionId":"allow-once"}}` and the turn proceeded.

**Permission path: FIRED.** Matches `SessionRequestPermissionParams`/`PermissionOptionDto`/
`PermissionOutcomeResult`/`PermissionOutcomeDto` field-for-field — AI-686's `AcpInteractionBridge` is
now confirmed correct against a REAL agent, not just `FakeAcpAgent`.

**fs/terminal: NOT observed.** Across the whole tool-using turn only ONE agent→client request
arrived (`session/request_permission`); no `fs/*`/`terminal/*` call was made — the shell command ran
and returned via `rawOutput` without needing client fs/terminal capabilities. Signal for AI-687: at
least for an `execute`-kind tool, Cursor does not need the client to expose those capabilities. This
probe did not exercise an `edit`/`read`-kind tool, so whether file-editing calls `fs/*` remains
unconfirmed.

## Step 2 — translation adjustments

**None.** See `docs/ai-688-cursor-prototype-findings.md`'s "Step 2 conclusion" — every real shape
(including the `rawOutput`-not-`content` fallback case) already round-trips correctly through the
existing `Reduce()`/`AcpEventTranslator` code. One non-blocking polish idea is flagged there for a
future task: the `rawOutput` fallback surfaces the WHOLE JSON object (`{exitCode,stdout,stderr}`) as
`ToolResultText` rather than just `stdout`, which is functionally correct but not the cleanest
possible rendered text.

## Step 3 — live E2E run

### Gate ON (`KCAP_ACP_LIVE=1`)

```
KCAP_ACP_LIVE=1 dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -c Debug --no-build --treenode-filter "/*/*/AcpHostedAgentRuntimeFactoryToolUseLiveTests/*"
```
Result: **1 passed**, duration ~95s.

Observed `AcpEventEnvelope` sequence (from the test's own console log, captured via the TUnit HTML
report since MTP doesn't echo passing-test stdout to the console by default):

```
seq=0 kind=user_message      text="Use your shell/command tool to run exactly `echo kcap-e2e-marker` and report the output."
seq=0 kind=assistant_thinking text="The user wants me to run a specific shell command: `echo kcap-e2e-marker` and report the output.\n\nThis is straightforward - I need to use the Shell tool to exec..." (truncated)
seq=0 kind=assistant_text     text="I'll run that command for you."
seq=0 kind=tool_call          toolCallId=toolu_bdrk_01UCxHpHKCybukpEWbCJy9mX toolName=`echo kcap-e2e-marker` toolInput={"command":"echo kcap-e2e-marker"}
seq=0 kind=tool_result        toolCallId=toolu_bdrk_01UCxHpHKCybukpEWbCJy9mX toolResult={"exitCode":0,"stdout":"kcap-e2e-marker\n","stderr":""} toolIsError=False
seq=0 kind=assistant_thinking text="The command executed successfully with exit code 0. The output was \"kcap-e2e-marker\" as expected. I should report this to the user."
seq=0 kind=assistant_text     text="The command executed successfully. The output is:\n\n```\nkcap-e2e-marker\n```"
```

7 envelopes total: `UserMessage`, 2×`AssistantThinking`, 2×`AssistantText`, `ToolCall`, `ToolResult`.
Both required assertions passed (`UserMessage` and `AssistantText` present); the optional
`ToolCall`/`ToolResult` branch also fired and its extra assertion (`ToolCallId` non-null) passed.

`AcpInteractionRequest`s seen by `AutoApproveServerConnection`: **1** (`kind=permission`,
`tool=`echo kcap-e2e-marker``, `options=[allow-once,allow-always,reject-once]`) — **AI-686
permission path fired: True**, and the daemon-level round-trip (not just the raw python probe)
completed the turn.

`Seq` stays `0` on every envelope, as documented — this test reads directly off
`IAcpTranscriptSource.Envelopes`, upstream of task 3's forwarder, which is the component that assigns
the real monotonic seq on dequeue.

### Gate OFF (default)

```
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -c Debug --no-build --treenode-filter "/*/*/AcpHostedAgentRuntimeFactoryToolUseLiveTests/*"
```
Result: **skipped** (18ms, no `cursor-agent` process spawned) —
`Gated live E2E against a real 'cursor-agent acp' tool-using turn — set KCAP_ACP_LIVE=1 to run ...`.

## Full unit suite (gate OFF)

```
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -c Debug --no-build
```
**2710 / 2712 passed, 0 failed, 2 skipped** (both gated live tests — this task's new one, plus the
pre-existing gap-1 `AcpHostedAgentRuntimeFactoryLiveTests`). Duration ~1m49s.

## AOT/trim gate

```
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```
**Empty** (publish succeeded, native code generated, zero IL2xxx/IL3xxx warnings).

## Concerns / follow-ups (not blocking this task)

- `tool_call_update`'s terminal `status: "failed"` case was not exercised (the probe/E2E's shell
  command always succeeds) — `AcpEventTranslator`'s `ToolIsError` mapping for that case remains
  spec-assumed, not probe-confirmed.
- Whether an `edit`/`read`-kind tool calls `fs/*` against the client (vs. `execute`'s observed
  server-side execution) is still unconfirmed — feeds AI-687, would need a file-editing prompt to
  check.
- Polish idea (deliberately NOT done here per the "do NOT redesign" rule): extract just
  `rawOutput.stdout`/`.stderr` for `kind: "execute"` tool results instead of the whole raw JSON blob,
  for cleaner rendered transcript text.
