# Catalogue-carried flow guidance

## Goal

A flow published to the server catalogue is offered and driven by coding agents exactly the way
`spec-review` and `code-review` are today. The knowledge of *when* to offer a flow and *how* to
drive it moves out of CLI skills and instructions and into the flow definition itself.

This is the first of three sub-projects toward a CLI that knows nothing about any specific flow:

| | Sub-project | Depends on |
|---|---|---|
| **A** | Flows describe themselves in the catalogue (this spec) | — |
| B | Each definition sets its participants' contract: outcome vocabulary, sign-off outcome, system prompt | — |
| C | Retire the review surface: `*_review_*` alias tools, `review-flows` skill, built-in id strings | A, B |

## Current state

- `GET /api/flows/definitions` returns `id`, `version`, `description`, `is_single_participant` and
  participants (`role`, `vendor`, `model`). `description` is the only text a driving agent sees.
- The engine implies the driving loop (submit, fix, resubmit until `clean`, then close), but nothing
  authors it. The CLI skills `review-flows`, `agent-flows` and `suggest-review-flow` carry it, along
  with the mapping from spec → `spec-review` and code → `code-review`, and the per-flow advice
  (commit range, "do not ask the reviewer to run tests").
- Proactive offering comes from `KcapAgentInstructions.cs` and the `suggest-review-flow` skill, both
  review-only. A flow an operator publishes is never offered, and the skills tell agents not to touch
  the flows tools unless the user names a flow.

## Design

### Server: definition schema

A new optional top-level `guidance` block:

```yaml
guidance:
  offer: proactive        # proactive | on_request; default on_request
  when_to_use: >-         # plain text, at most 300 characters
    ...
  driver_guide: |         # markdown, at most 8 KB
    ...
```

- `guidance` is catalogue metadata. It is not projected into `FlowDefinition` and never reaches the
  launch path; the run engine is unchanged.
- Publish rejects `offer: proactive` without a `when_to_use`, a field over its cap, and unknown
  keys inside `guidance` (the parser's existing strict snake_case rule).
- `flow-definition.schema.json` (admin Monaco editor) gains `guidance` and the missing
  `initial_prompt_context_only` / `follow_up_prompt_context_only` keys, so it validates both built-ins.

### Server: API

- `RunnableFlowDefinition` gains `offer` and `when_to_use` (additive JSON fields).
- New `GET /api/flows/definitions/{id}`: the listing entry plus `driver_guide`. Unknown, disabled
  and deleted ids return 404, matching what `start_flow` refuses.

### Server: built-ins

`spec-review.yaml` and `code-review.yaml` gain `guidance` blocks with `offer: proactive`. Their
`when_to_use` and `driver_guide` carry what the CLI skills say about each today: when to offer, what
context to submit (spec path / branch name and explicit commit range, reviewer works offline), how to
iterate, when to close, and for `code-review` that tests are out of scope. `SyncBuiltIns` re-seeds
them at a new version unless an admin has published over them.

`SyncBuiltIns` never rewrites an admin-published override of a built-in id, so the guidance reaches an
override at read time instead. For a built-in id whose current definition has no `guidance` block,
the listing and `/definitions/{id}` serve the guidance from the embedded built-in YAML. An override
that authors any `guidance` block is served as written; `guidance: { offer: on_request }` is how an
admin stops agents offering it. Admin-authored YAML is never modified and no version is appended.

### CLI: session-start flows lane

- A third lane in `SessionStartCompositeContextProvider`, beside memory and guidelines, plus the
  equivalent emitter on Claude's SessionStart path.
- It calls `GET /api/flows/definitions`, keeps `offer: proactive` entries, and renders:

  ```
  Flows you may offer (ask first; never start unprompted):
  - code-review: <when_to_use>
  - spec-review: <when_to_use>
  Before starting one, call get_flow_definition(<id>) and follow its guide.
  ```

- At most 10 flows with their `when_to_use`, about 2 KB in total; further entries are listed by id only.
- Fails open like the other lanes: an error, timeout, or a server without the fields injects nothing.
- Skipped when the `kcap-flows` MCP server is not registered for the harness: offering a flow the
  agent cannot start is worse than not offering it.

### CLI: flows MCP server

- `list_flow_definitions` renders `offer` and `when_to_use` per entry.
- New tool `get_flow_definition(definition_id)`, annotated read-only: description, participants and
  `driver_guide`. A 404 from an old server that lacks the endpoint, told apart from an unknown id by
  the error body, gets a "this server does not publish flow guides" notice.

### CLI: skills and instructions

- `agent-flows` becomes the one skill for driving any flow.
  - Triggers: the user names a flow, accepts an offered one, or the agent is about to offer one listed
    in its session context.
  - Workflow: `get_flow_definition` → prepare context per the guide → `start_flow` → rounds via
    `send_to_participant` → `close_flow`.
  - The `spec-review` / `code-review` specifics are removed; they now come from the guides.
  - The description stays within the 1024-character skill cap.
- `suggest-review-flow` is deleted.
- In `KcapAgentInstructions.cs`, the review-offer paragraph becomes one generic line: offer flows
  listed in the session context when their *when to use* applies, and ask before starting.
- `review-flows` stays until sub-project C because the alias tools it documents still exist. Its
  description points to `agent-flows`.
- `help-mcp.txt` and `README.md` document `get_flow_definition` and the lane.

## Compatibility

| CLI | Server | Behaviour |
|---|---|---|
| old | new | New fields ignored; unchanged. |
| new | old | The lane injects nothing, so there are no proactive offers. `get_flow_definition` returns the "not published" notice. Running a named flow is unaffected. |
| new | new | Catalogue-driven offers and guides. |

The new-CLI/old-server regression is accepted: the server ships first.

## Delivery

1. Server PR: schema, parser, admin JSON schema, listing fields, `/definitions/{id}`, built-in guidance.
2. CLI PR: lane and Claude emitter, MCP tool changes, skills, instructions, help text, README. The
   listing keeps being parsed as `JsonNode`, so there is no DTO coupling and no submodule bump.

## Testing

**Server**
- Parser: a valid `guidance` block, the length caps, `when_to_use` required when proactive, unknown keys rejected.
- Seed: both built-ins carry guidance and re-version; an admin override is left alone.
- Guidance fallback for a built-in id:
  - an override with no `guidance` serves the embedded guidance
  - an override with its own `guidance` serves it as written
  - an override with `offer: on_request` is not proactive
  - a custom (non-built-in) id with no `guidance` gets no fallback
- Endpoints: listing fields; `/{id}` returns 404 for disabled and deleted definitions.
- `flow-definition.schema.json` validates both built-in YAML files.

**CLI**
- Lane:
  - renders proactive flows only
  - applies the flow-count and size caps
  - fails open on 5xx, timeout and missing fields
  - is skipped without `kcap-flows`
- `get_flow_definition`: rendering, unknown id, and an old server (WireMock).
- Schema conformance and annotation pins for the new tool.
- Skill conformance: `agent-flows` description cap; `suggest-review-flow` absent.

## Out of scope

- The outcome vocabulary, sign-off outcome and participant system prompt (sub-project B).
- Removing the alias tools, the `review-flows` skill and the old-server string naming the built-ins (sub-project C).
- Input parameters for flow definitions.
