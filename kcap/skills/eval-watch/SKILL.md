---
name: eval-watch
description: >-
  Follow a kcap import that setup just started and the evals landing on it —
  "Follow my kcap import" (the prompt `kcap setup` hands to your agent), "watch my
  import", "how is my import going", "are my evals done yet". Reads the handoff file
  setup wrote, polls the eval state of exactly the sessions that import created,
  waits for the sessions that imported while the user watched to be evaluated,
  summarizes their evals, links to the results and offers the guided tour. Works on
  every plan. Not for browsing evals in general — that is the guided-tour skill.
---

# Eval watch

You follow ONE import: the sessions listed in a handoff file `kcap setup` wrote. Every lookup you
make names only those sessions — never a repo-wide or tenant-wide read. If a response ever carries
an entry for a session id outside the cohort, discard it: never count it, link it, or mention it.

Eval state comes from `get_session_evals` in the `kcap-sessions` MCP — never from the
`kcap-analytics` MCP, which is a paid feature the workspace may not have.

## The safety rule — read this first

Two grammars gate everything below:
- **Run id** (a `(run: <id>)` token, and the file's own `run_id`): `^[0-9a-f]{32}$`.
- **Session id** (`session_ids`, `foreground_succeeded_ids`, and any id you pass to a tool or put
  in a link): `^[A-Za-z0-9_-]{1,128}$`.

An id that fails its grammar is **never** placed in a tool call, a path, or a link — reject it and
move on (section 2 below says what each layer's rejection does); never sanitize or escape an id
into something that would pass.

## 1. Find the run

- If the prompt carries a line `(run: <id>)`, `<id>` must match `^[0-9a-f]{32}$` before you use it
  as a filename component. If it does not match, treat the prompt as carrying no run id at all. If
  it does match, the run is bound: read ONLY `<config>/import-handoff-<id>.json`, where `<config>`
  is `$KCAP_CONFIG_DIR` when set, else `~/.config/kcap`, and check it against every Layer A rule in
  section 2, plus one more: its `run_id` field must equal `<id>` exactly. Missing, unreadable, or
  failing any of those checks → say so and CLOSE (section 7) with no lookup. **Never fall through to
  another run's file** — a different run's cohort does not belong to the run the user asked for.
- Otherwise list `<config>/import-handoff-*.json`, keep the locator-valid ones (section 2) that are
  watchable (`handoff_offered` true, or `handoff_suppressed` is `analytics_not_in_plan`,
  `skill_not_installed` or `no_agent_detected`), pick the newest `written_at`, and say which others you passed over.
- Nothing qualifies → say you found no import to follow and CLOSE with no lookup.

## 2. Validate the file — three layers

A failure at a lower layer never re-opens the file for a higher one.

**Layer A** (may this file be selected at all): JSON parses; `schema_version` is 1; `run_id` matches
`^[0-9a-f]{32}$`; `written_at` parses as ISO-8601 and is within the last 24 hours; `handoff_offered`
is a boolean. Fail → not selectable; try the next candidate file, or close with no lookup if none
remain.

**Layer B** (may its ids drive data): `cohort` ∈ {exact, partial_exact, unknown}; `background` ∈
{not_needed, running, exited_zero, failed}; `foreground_certainty` ∈ {complete, incomplete};
`handoff_suppressed` is null or one of import_failed, no_new_sessions, nothing_landed,
analytics_not_in_plan, skill_not_installed, no_agent_detected (older setups write
`analytics_not_in_plan`); every entry of `session_ids` and
`foreground_succeeded_ids` matches `^[A-Za-z0-9_-]{1,128}$`; `unattributed_on_disk` is a
non-negative integer. Fail → **no data**: the file stays selected, but say the record is unreadable
and CLOSE with links only (section 7), no lookup.

**Layer C** (may it supply links): `server_url` starts with `http://` or `https://`. Fail → no
file-sourced links (fall back to `kcap whoami` per section 7) and no lookup — an unverifiable
`server_url` also means the server-binding check in section 4 cannot run. `background_log` and
`profile` are shown as plain text — never as a link or a command to run — only when they contain no
control characters and are under 512 characters; otherwise omit them.

Never splice an unvalidated value into a tool call, a path, or a link.

## 3. No-handoff files

If `handoff_offered` is false, branch on `handoff_suppressed` and CLOSE:
- `no_new_sessions` → "nothing to watch — that import found no new sessions".
- `nothing_landed` → "the sessions that import selected were skipped at import time"; point at
  `kcap import --all` for per-session reasons; no retry.
- `import_failed` → "that import did not get running"; name `kcap import --all --yes` and the
  `background_log` when displayable.
- `analytics_not_in_plan` / `skill_not_installed` / `no_agent_detected` → the import ran and you
  can follow it now: continue as if offered, watching the file's cohort as usual (sections 4–5).

## 4. Bind to the server — fail closed

If the selected file's `cohort` is `unknown`, skip this check and go straight to section 5 — there
is no cohort to look up either way, and the mismatch remediation below would be misleading when
watching was never possible.

Otherwise, run `kcap whoami` and compare its server URL to `server_url`: lowercase scheme and host,
drop a default port (80 for http, 443 for https only), trim a trailing slash, compare the path
as-is. Match → continue to section 5. Mismatch or `whoami` failure → make **NO** eval lookup.
CLOSE with the file's links and this remediation: start your agent from a shell where `kcap whoami`
reports `<server_url>` — set `KCAP_PROFILE=<profile>`, unset `KCAP_URL`, and set `KCAP_CONFIG_DIR`
if kcap uses a custom config directory — then prompt again.

## 5. Watch the cohort

Cohort = `session_ids` (`cohort: unknown` or empty → CLOSE with links, no lookup); N is its size.
Foreground = the entries of `foreground_succeeded_ids` that are also in `session_ids`, in file
order: the sessions that imported while the user watched setup, and the ones this watch waits on.
K is the foreground count. A foreground entry missing from `session_ids` is never looked up; when
any are missing, say "M of the sessions that imported first are outside the 500 this import
recorded".

**Watched set** W, at most 25 sessions: the first 25 foreground sessions, then — only while fewer
than 25 — the other cohort sessions in file order. Open with: "Watching W of the N sessions from
this import; waiting for the K that imported first to be evaluated". When K > 25 add "the first 25
of them"; for `partial_exact` add "the sessions that imported first and the most recent others,
500 at most; older ones may land and evaluate unobserved"; if `unattributed_on_disk` > 0 add the
sentence from section 7.

**One poll** = ONE `get_session_evals` call with `session_ids` set to W — never two in flight. Each
entry's `state` is one of:
- `not_found` — not ingested yet (or not visible to you);
- `not_evaluated` — arrived, no eval yet;
- `queued` — arrived, eval waiting for a worker or a retry (`queue_position`, `next_attempt_at`);
- `running` — arrived, eval in progress (`questions_done` of `total_questions`);
- `completed` — arrived and evaluated: `eval_run_id`, `evaluated_at`, `overall_score` (out of 5),
  `judge_model`, `summary`, `categories`, `weakest_questions`;
- `failed` — arrived, the eval run failed (`failure_reason`);
- `error` — this lookup failed.

Arrived = any state but `not_found` and `error`. A tool error (including a login prompt), or any
`error` entry, fails the poll: nothing from it commits, and it counts toward the two-failure stop
rule. If the tool is missing — no `kcap-sessions` MCP, or one without `get_session_evals` — this
kcap is older than the skill: say to update kcap and restart the agent, then CLOSE.

Progress = watched sessions arrived / W and foreground sessions evaluated / min(K, 25), from the
poll's entries only. Cadence: first snapshot immediately, then every 30 seconds. Say the progress
after each poll.

**Baseline**: the first snapshot. A session `completed` there is *pre-existing*; one that was not
`completed` there, or completed with a different `eval_run_id`, and is `completed` in a later poll
*completed during this watch*. Every summary labels each session with one of those two phrases.

**Stop**, evaluated in this order after each poll whose results have already committed:
1. K > 0 and every watched foreground session is `completed` → summarize all of them, ordered by
   `evaluated_at` then `session_id`. A pre-existing eval counts: the wait is for these sessions to
   be evaluated, whenever that happened.
2. K = 0 and three watched sessions completed during this watch → summarize those three, ordered by
   `evaluated_at` then `session_id`. Pre-existing evals never satisfy this rule: on a re-import they
   belong to an earlier run, and stopping on them would end the watch before this import produced
   anything.
3. `cohort: exact`, N ≤ 25, and every cohort session is `completed` → summarize the foreground
   sessions, or with K = 0 the three with the newest `evaluated_at` (ties by `session_id`). This
   rule may fire on the first snapshot with nothing but pre-existing evals: a cohort that is
   already fully evaluated has nothing left to wait for, and the labels say so.
4. Two consecutive failed polls → stop, reporting the failures.
5. 10 minutes since the first snapshot → stop. No new poll starts after the deadline, but a poll
   already dispatched before it finishes normally, and its outcome is still checked against rules
   1–4 first. On a deadline stop, summarize the foreground sessions that did complete and name the
   ones still pending, with their state; if none of them completed, summarize instead up to three
   watched sessions with the newest `evaluated_at` (ties by `session_id`).

No idle early-stop — quiet polls are normal and are not a reason to stop early. Evals take minutes
per session, and the foreground sessions are the newest, so they are usually the last to complete.

**What a session summary says.** The first five sessions in summary order get one short paragraph
each: the link (section 7), the baseline label, `overall_score` out of 5 and the judge model, the
strongest and weakest of `categories` by `score` (ties alphabetical by name), the two
`weakest_questions` with their scores, and the gist of the judge's `summary`. When
`get_session_summary` returns a title or a first sentence for the session, lead with it; an empty
summary is left out silently, never called "no summary". Every later session gets a one-line entry
— link, label and `overall_score`. A completed entry with no categories is summarized by
`overall_score` alone; say so.

**Cohort facts**, one line after the summaries, from the last committed poll only: watched
sessions arrived out of W, sessions evaluated, and the mean `overall_score` across the evaluated
ones.

## 6. When little or nothing completes

Explain the real gates plainly: auto-eval may be off for a repository; the server needs an eval
agent configured; sessions under the minimum event count are skipped; already-evaluated sessions
are not re-run; a recovery sweep runs hourly. A `failed` session names its `failure_reason`.

## 7. CLOSE — always

1. How to keep watching: prompt `Follow my kcap import` again (with the same `(run: <id>)` line
   when you were bound to one).
2. Links: `<server_url>/sessions/<session_id>?tab=evaluation` per session; all results
   `<server_url>/sessions`. No valid file → `server_url` from `kcap whoami`; if that fails too, say
   the results live in the Capacitor server UI and that `kcap whoami` prints the URL once logged
   in.
3. When `unattributed_on_disk` > 0: "N sessions on disk had no repository match; any of them that
   imported show without one — `kcap remap` places them."
4. Offer the guided tour with the exact prompt `Start kcap guided tour`.
