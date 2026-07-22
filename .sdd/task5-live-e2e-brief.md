# AI-688 Option B — Task 5: live tool-using E2E + confirm/adjust real wire shapes (implementer brief)

The capstone. Prove the full daemon transcript pipeline (tasks 1–4) surfaces a REAL Cursor turn end-to-end, and
confirm (or correct) the `tool_call`/`tool_call_update`/`agent_thought_chunk` wire shapes that were spec-derived and
never observed. This is EXPLORATORY — observe real behavior, adjust minimally, document. `cursor-agent` v2026.07.01
is authed (Team tier) on this machine.

Work ONLY in `/Users/tony/Documents/kcap-cli-wt/ai-688-cursor-hosted-agent-prototype`, branch
`ai-688-cursor-hosted-agent-prototype`. Commit; **do NOT push**; no delegation. Spec §2.2/§4/§5 + the existing
`docs/ai-688-cursor-prototype-findings.md` and the gap-1 live test `test/Capacitor.Cli.Tests.Unit/Services/AcpHostedAgentRuntimeFactoryLiveTests.cs`.

## Step 1 — OBSERVE first (before touching any translation code)
Run a quick observational probe of a TOOL-USING turn against `cursor-agent acp` to capture the REAL wire shapes.
You may reuse/extend the python harness pattern from `docs/ai-688-cursor-prototype-findings.md` (initialize →
session/new → set_config_option model=claude-sonnet-4-5 → session/prompt), OR a throwaway. Use a prompt that
strongly forces a tool call, e.g. *"Use your shell/command tool to run exactly `echo kcap-e2e-marker` and report the
output."* Capture and record, from the raw `session/update` notifications:
- the exact `tool_call` shape (does it carry the tool INPUT args? under what field — `rawInput`? something else?);
- the exact `tool_call_update` shape (is the result under `content`/`rawOutput`? what `status` values appear —
  is terminal `completed`/`failed`?);
- `agent_thought_chunk` shape (confirm `content.text`);
- **does the agent issue a `session/request_permission` (client→ ... actually agent→client) request** for the
  tool? (the AI-686 path). If your probe doesn't answer client-side requests it may hang — answer
  `request_permission` with an allow/selected outcome so the turn proceeds, and NOTE that it fired.
- **does the agent call `fs/*` or `terminal/*` against the client** (we advertise neither)? If so, NOTE it (this
  feeds AI-687) and observe what happens (does the tool run server-side anyway, or error?).
Append all of this to `docs/ai-688-cursor-prototype-findings.md` under a new "Tool-using turn (AI-688 task 5)"
section, INCLUDING a verbatim example of each shape.

## Step 2 — reconcile task 1's assumptions with reality
Task 1's `AcpSessionUpdate.Reduce()` currently extracts: `ToolInputJson` from `tool_call`'s `rawInput`;
`ToolResultText`/`ToolIsError` from `tool_call_update`'s `content` text-blocks or `rawOutput`; terminal =
`completed`/`failed`. `AcpEventTranslator` maps per §2.2. **If the observed shapes MATCH, change nothing.** If they
DEVIATE, make the MINIMAL fix to `Reduce()` and/or `AcpEventTranslator` to match the REAL data, keep the existing
unit tests green (update them only where the real shape genuinely differs), and document the deviation in the
findings doc + your report. Do NOT redesign — just align to reality.

## Step 3 — the gated live E2E (C#)
Add a gated (`KCAP_ACP_LIVE=1`, else skipped — mirror `AcpHostedAgentRuntimeFactoryLiveTests`'s skip mechanism)
test that drives the REAL path: construct the real `AcpHostedAgentRuntimeFactory` (`connectionSource: null` → real
`cursor-agent acp`) with a tool-forcing prompt + model `claude-sonnet-4-5`, a `requestInteraction` stub that
AUTO-APPROVES any permission request (so the tool turn completes — and record that it was invoked), then read the
runtime's `IAcpTranscriptSource.Envelopes` channel for up to ~60s and assert the transcript pipeline produced, at
minimum, a `UserMessage` and an `AssistantText` envelope — and, IF the turn used a tool, a `ToolCall` (and
`ToolResult` if a terminal update with content arrived). Keep assertions RESILIENT to model non-determinism (this
is a manual/gated E2E, not a CI gate): assert the pipeline surfaced the envelopes that the turn actually produced,
and log the full envelope sequence. Dispose the runtime at the end.

Run it: `KCAP_ACP_LIVE=1 dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -c Debug
--treenode-filter "/*/*/<YourTestClass>/*"`. Also run WITHOUT the gate to confirm it SKIPS (no cursor-agent spawned).
Paste the observed envelope sequence in your report.

## Definition of done
- Step 1 observations recorded in the findings doc (verbatim shapes + permission-path + fs/terminal answers).
- Step 2 translation aligned to reality (or explicitly "no change needed — shapes matched").
- Step 3 live E2E written, RUN with the gate on (report the real envelope sequence + whether ToolCall/ToolResult
  surfaced + whether the permission path fired), and confirmed to SKIP when ungated.
- Full unit suite green (`dotnet run --project test/Capacitor.Cli.Tests.Unit/...`, gated live test skipped): report
  pass/total. AOT gate empty
  (`dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`).

## Report contract
Write detail to `.sdd/task5-live-e2e-report.md`. Return only STATUS, commit sha, the REAL observed shapes
(tool_call/tool_result/thought), whether the permission path fired, whether fs/terminal was requested (AI-687
signal), any translation adjustments you made (with why), the live envelope sequence, the skip-when-ungated
confirmation, unit pass/total, AOT result, and concerns. Commit as
`AI-688 Option B task 5: live tool-using E2E + real wire-shape confirmation`.

## HARD RULES
- Read/Edit/Bash yourself only; NO Agent/Task delegation; NO git push.
- Worktree-only. `await` every TUnit assertion. AOT/trim-safe.
- MINIMAL translation changes, only to match observed reality; keep tasks 1–4's structure intact. The live E2E must
  be gated so CI never runs it or spends a Cursor turn.
