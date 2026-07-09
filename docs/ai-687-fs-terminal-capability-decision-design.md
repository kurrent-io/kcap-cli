# AI-687 — ACP terminal & file capability integration: design

Parent epic **AI-682** (ACP support). Builds on AI-684 (ACP foundation), AI-685 (server mapper),
AI-686 (permission/elicitation bridge), all merged; independent of AI-688 (#301, canonical
surfacing, unmerged). This work lives entirely in **kcap-cli** (the daemon).

## 1. Purpose (from the issue)

> Decide and implement only the ACP client capabilities Kapacitor can safely provide for terminal
> and file operations. Start from the smallest advertised capability set: no `fs` and no `terminal`
> unless Cursor requires them for useful operation.

The issue is **decision-first**: the acceptance criteria are about *not advertising what we can't
enforce* and *covering capability behavior with tests* — not about shipping an fs/terminal
implementation. Whether we implement anything is contingent on the empirical answer to one question:

> **Does Cursor-via-ACP require the client (us) to serve `fs/*` or `terminal/*` requests to do
> useful file/shell work, or does it perform those operations itself?**

## 2. Evidence — the capability probe

A live probe drove the real `cursor-agent acp` agent (Team tier, authed) through a turn that forces
both a file **write** and a file **read**, with the client capabilities advertised exactly as the
daemon advertises them today (`fs.readTextFile=false`, `fs.writeTextFile=false`, `terminal=false`),
and a fresh temp directory as the session `cwd`. The prompt: *"create a file named kcap687.txt whose
exact contents are the single line HELLO687, then read that file back and report its exact
contents."*

Observed (probe script archived in the session scratchpad; reproducible against any Team-tier
`cursor-agent`):

- **Zero agent→client requests.** Cursor issued **no** `fs/*` and **no** `terminal/*` requests. The
  recorded `client requests (agent→client)` list was empty.
- **Tool calls ran locally, in our `cwd`.** Cursor emitted `tool_call`/`tool_call_update`
  notifications of kinds `edit` (title "Edit File"), `read` (title "Read File"), and `execute`
  (`ls -la`). The `edit` result arrived as a `content:[{type:"diff", path, oldText, newText}]` block
  whose `path` was the file **inside our temp `cwd`**; the `execute` result arrived as
  `rawOutput:{exitCode,stdout,stderr}` whose `ls -la` listing showed the file on local disk.
- **The file really landed in our `cwd`.** After the turn, `kcap687.txt` existed in the temp
  directory we passed and contained `HELLO687`.

This corroborates the AI-688 E2E finding (execute-kind tools ran server-side via `rawOutput`,
requesting no client capabilities) and extends it to `edit`/`read`: **`cursor-agent`, which runs as
a local child process of the daemon, performs all file and shell operations itself, directly against
the `cwd` we give it.** It never delegates them to the ACP client.

### 2.1 Coverage and limits of the evidence

The probe covers the `edit`, `read`, and `execute` tool kinds against a plain directory. It does not
prove Cursor will *never*, in *any* future version or scenario (a permission-denied fallback, a
sandboxed mode, a capability it gates on our advertisement), issue an `fs/*` or `terminal/*` request.
That residual uncertainty is exactly why the design does not merely "advertise nothing and stop" — it
also **makes the decline correct** (§4), so an unobserved future request is declined safely instead
of silently mis-answered.

## 3. Decision

**Advertise no client `fs` and no client `terminal` capability.** This is already what
`AcpHostedAgentRuntime.StartAsync` sends (`ClientCapabilities(Fs(false,false), Terminal:false)`), so
the advertisement is **unchanged**. The evidence shows this empty set is *sufficient* — Cursor does
useful file/shell work without any client capability — and it is the *smallest* set, satisfying the
issue's "start from the smallest set unless required" directive.

Mapping to the acceptance criteria:

| Acceptance criterion | How this design meets it |
|---|---|
| Never advertise a capability we can't enforce safely | We advertise none. §4 additionally makes an unadvertised capability request *fail closed* (declined), so even a request we never advertised can't be mis-served. |
| File and terminal operations bounded to the hosted worktree | Cursor runs them in the `cwd` we pass, which is the hosted worktree. We do not serve fs/terminal, so there is no client-side path we could fail to bound. The bounding that exists is Cursor's own (cwd + its permission model, bridged by AI-686) — see §6 for the honest limit. |
| Terminal output visible through the existing remote terminal path | N/A — we serve no `terminal/*`. Cursor's shell output surfaces as `execute` tool `rawOutput` in the canonical transcript (AI-688), not via `terminal:{agentId}`. |
| Capability behavior covered by tests, including path-escape rejection | §5. We reject the *whole method* (`-32601`), so there is no fs path to escape — the "path-escape rejection" case is satisfied at a coarser, stronger granularity (method refused before any path is parsed), and a test asserts that. |

## 4. The one code change — make the default-decline correct

### 4.1 The gap

`AcpConnection` routes inbound agent→client requests to `OnServerRequest`, which the daemon wires to
`AcpInteractionBridge.HandleAsync`. That handler is:

```csharp
request.Method switch {
    "session/request_permission" => await HandlePermissionAsync(...),
    "elicitation/create"         => await HandleElicitationAsync(...),
    _                            => null          // any other method, incl. fs/*, terminal/*
};
```

In `HandleServerRequestAsync`, a **wired** handler that returns `null` currently produces
`result = null, error = null`, and `SerializeRawIdResponse` writes:

```json
{"jsonrpc":"2.0","id":N,"result":null}
```

That is a JSON-RPC **success** response with a null result — semantically *"I performed your
`fs/write_text_file` and it returned nothing."* If a future or misbehaving Cursor ignores our
`fs:false`/`terminal:false` advertisement and calls one of those methods, we would **falsely
acknowledge** an operation we never performed. The correct answer for a method the client does not
implement is `-32601 Method not found` — which is exactly what the connection already returns on the
**no-handler** branch (`handler is null`). The bug exists *only because* a handler is wired.

### 4.2 The fix

Treat **handler-returned-`null`** identically to **no-handler**: a `null` return means *"this method
is not handled"* → respond `-32601 Method not found`, never a null-result success.

- In `AcpConnection.HandleServerRequestAsync`, when the handler ran and returned `null`, set the
  `-32601` error (same `AcpError` shape as the `handler is null` branch) instead of writing a null
  result. Emit one `logger.LogDebug` line noting the declined method (diagnostics: if Cursor ever
  *does* start requesting `fs/*`/`terminal/*`, it will show up in daemon logs and we revisit this
  decision).
- Update the `OnServerRequest` XML-doc contract: *"A handler returning `null` signals the method is
  unhandled; the connection answers `-32601 Method not found`. Handlers that intend a successful
  empty result must return an explicit `JsonElement` (e.g. an empty object), never `null`."*

### 4.3 Why this is safe (the invariant)

`AcpInteractionBridge.HandleAsync` returns `null` **iff** the method hit the `_ => null` default.
Both handled methods return non-null on every path:

- `HandlePermissionAsync` returns either `CancelledResult()` or `MapPermissionDecision(...)`.
- `HandleElicitationAsync` returns either `CancelledResult()` or `MapPermissionDecision(...)`.

Neither ever returns literal `null`. Therefore the fix changes behavior **only** for methods the
bridge does not handle (`fs/*`, `terminal/*`, and any other unadvertised agent→client method); it is
a no-op for `session/request_permission` and `elicitation/create`.

The PR #244 **"always answered"** guarantee (every inbound server request gets exactly one response
frame — a value, a `-32603` on throw, or now a `-32601` on unhandled — never an orphaned/wedged
request) is **preserved**; only the *shape* of the unhandled-method response changes from
null-result to `-32601`.

## 5. Test plan (kcap-cli daemon unit tests)

All in `test/Capacitor.Cli.Tests.Unit/Acp/`.

1. **Rewrite** `AcpConnectionTests.Inbound_server_request_handler_returning_null_writes_a_null_result_response`
   → `…_writes_method_not_found_error`: a wired handler returning `null` for `fs/read_text_file`
   yields a response with **no `result`** and `error.code == -32601`; assert the read loop is still
   alive afterward (mirror the throw-test's liveness check) so the "always answered" guarantee is
   re-locked at the new shape.
2. **New** `AcpConnectionTests`: an unadvertised `terminal/create` request against the **real wired
   bridge** (`OnServerRequest = bridge.HandleAsync`) yields `error.code == -32601` — proving the
   production wiring (not just a fake null handler) declines cleanly.
3. **Regression** (bridge unaffected): with the real bridge wired, `session/request_permission` and
   `elicitation/create` still round-trip to their `result` outcome — no `-32601`. (Extend/confirm
   existing `AcpInteractionBridgeTests` coverage rather than duplicate it.)
4. **Capability-advertisement lock** (`AcpHostedAgentRuntimeTests` or the factory tests): assert the
   `initialize` frame the runtime sends advertises `fs.readTextFile == false`,
   `fs.writeTextFile == false`, `terminal == false`. This is the executable form of the acceptance
   criterion "never advertises a capability it cannot enforce" and will fail loudly if a future
   change flips one on without revisiting this design.

No live/gated test is added — the probe (archived) is the empirical basis; the decision it drives is
locked by the deterministic unit tests above. NativeAOT publish must stay warning-free (existing
gate).

## 6. Security note / limitations (feeds AI-689)

Documenting the honest boundary, per the issue's "document which capabilities are advertised and
why":

- Because `cursor-agent` runs as a **local child process of the daemon**, its file and shell
  operations execute with the **daemon's own filesystem and process privileges**. Kapacitor does
  **not** sandbox them. "Bounded to the hosted worktree" holds in the normal case (Cursor uses `cwd`
  as its working directory), but it is **not a hard boundary we enforce** — Cursor could, in
  principle, touch paths outside `cwd`. The guardrails that exist are (a) the `cwd` we set and (b)
  Cursor's own permission model, whose prompts we surface and gate through the AI-686 permission
  bridge.
- We advertise no `fs`/`terminal`, so we add **no** new client-served attack surface; the `-32601`
  decline (§4) ensures we never perform an operation on Cursor's behalf.
- Hardening beyond this — e.g. launching `cursor-agent` under an OS sandbox, or constraining its
  filesystem view to the worktree — is explicitly **out of scope** and belongs to **AI-689**
  (hardening). This design records the limitation rather than silently implying a boundary we don't
  enforce.

## 7. Out of scope / deferred

- Implementing client `fs/*` or `terminal/*` handlers (worktree path resolution, symlink/escape
  rejection, terminal streaming through `terminal:{agentId}`). Not built — the evidence shows Cursor
  does not request them. If a future Cursor version *does* start requesting them (visible via the
  §4.2 debug log), reopen this decision; the code change here guarantees the interim behavior is a
  safe decline, not a silent false-success.
- OS-level sandboxing of `cursor-agent` (AI-689).
- Attaching terminal/file protocol IDs to canonical extensions "for UI/debug" — no client-served
  fs/terminal means no such IDs exist on our side; the `execute`/`edit`/`read` tool calls already
  carry their `toolCallId` in the AI-688 transcript.

## 8. Risks & open questions

- **R1 — Cursor's reaction to `-32601` is unobserved.** In the probe Cursor never sent `fs/*`, so we
  have no direct evidence of how it degrades if we decline one. Accepted: a declined turn (worst
  case, an errored turn) is strictly safer than a falsely-succeeded file operation. The debug log
  makes any real occurrence visible.
- **R2 — Behavior change to merged foundation code.** The fix alters an intentionally-tested contract
  (the null-result test). Mitigated by §4.3 (invariant: no handled method returns null) + the
  rewritten/added tests, which keep the "always answered" guarantee and add the production-wiring and
  advertisement-lock coverage.
- **R3 — The empty capability set is a *current-Cursor* fact, not a protocol guarantee.** Recorded as
  such; the advertisement-lock test + the decline + the debug log together make a future change
  observable and safe rather than silent.
