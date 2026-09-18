---
name: eval-watch
description: >-
  Follow a kcap import that setup just started and the evals landing on it —
  "Follow my kcap import" (the prompt `kcap setup` hands to your agent), "watch my
  import", "how is my import going", "are my evals done yet". Reads the handoff file
  setup wrote, polls Capacitor analytics for exactly the sessions that import
  created, summarizes the first three completed evals, links to the results and
  offers the guided tour. Not for browsing evals in general — that is the analytics
  or guided-tour skill.
---

# Eval watch

You follow ONE import: the sessions listed in a handoff file `kcap setup` wrote. Every query you
issue names only those sessions — never a repo-wide or tenant-wide read, and never a query without
an `IN (...)` or `session_id = '...'` bound to this cohort. If a response ever includes a row for a
session id outside the cohort, discard that row: never count it, link it, or mention it.

## The safety rule — read this first

Three grammars gate everything below:
- **Run id** (a `(run: <id>)` token, and the file's own `run_id`): `^[0-9a-f]{32}$`.
- **Session id** (`session_ids`, `foreground_succeeded_ids`, and any id you place in a query):
  `^[A-Za-z0-9_-]{1,128}$`.
- **Repo hash** (the `repo_hash` field an analytics response returns, used only in the repo-scoped
  link form): `^[0-9a-f]{16}$`. A `repo_hash` that fails this never reaches a link — fall back to
  the no-repo-hash link form (section 7).

The `kcap-analytics` MCP takes raw SQL text with no parameter binding, so the session-id grammar is
the SQL-safety boundary, not just a format check. The class excludes quote, backslash, whitespace
and `;`, so a value that matches it can never close a string literal or open a new statement — a
matching id is emitted as a single-quoted literal (`'<id>'`) with **no escaping**, because none is
needed and none should be attempted. A run of hyphens such as `--` matches the grammar and is safe
to emit this way: inside a quoted literal it is data, not a SQL comment. An id that fails either
grammar is **never** placed in SQL, a path, or a link — reject it and move on (section 2 below says
what each layer's rejection does); never sanitize or escape an id into something that would pass.

## 1. Find the run

- If the prompt carries a line `(run: <id>)`, `<id>` must match `^[0-9a-f]{32}$` before you use it
  as a filename component. If it does not match, treat the prompt as carrying no run id at all. If
  it does match, the run is bound: read ONLY `<config>/import-handoff-<id>.json`, where `<config>`
  is `$KCAP_CONFIG_DIR` when set, else `~/.config/kcap`, and check it against every Layer A rule in
  section 2, plus one more: its `run_id` field must equal `<id>` exactly. Missing, unreadable, or
  failing any of those checks → say so and CLOSE (section 7) with no query. **Never fall through to
  another run's file** — a different run's cohort does not belong to the run the user asked for.
- Otherwise list `<config>/import-handoff-*.json`, keep the locator-valid ones (section 2) that are
  watchable (`handoff_offered` true, or `handoff_suppressed` is `skill_not_installed` or
  `no_agent_detected`), pick the newest `written_at`, and say which others you passed over.
- Nothing qualifies → say you found no import to follow and CLOSE with no query.

## 2. Validate the file — three layers

A failure at a lower layer never re-opens the file for a higher one.

**Layer A** (may this file be selected at all): JSON parses; `schema_version` is 1; `run_id` matches
`^[0-9a-f]{32}$`; `written_at` parses as ISO-8601 and is within the last 24 hours; `handoff_offered`
is a boolean. Fail → not selectable; try the next candidate file, or close with no query if none
remain.

**Layer B** (may its ids drive data): `cohort` ∈ {exact, partial_exact, unknown}; `background` ∈
{not_needed, running, exited_zero, failed}; `foreground_certainty` ∈ {complete, incomplete};
`handoff_suppressed` is null or one of import_failed, no_new_sessions, nothing_landed,
analytics_not_in_plan, skill_not_installed, no_agent_detected; every entry of `session_ids` and
`foreground_succeeded_ids` matches `^[A-Za-z0-9_-]{1,128}$`; `unattributed_on_disk` is a
non-negative integer. Fail → **no data**: the file stays selected, but say the record is unreadable
and CLOSE with links only (section 7), no query.

**Layer C** (may it supply links): `server_url` starts with `http://` or `https://`. Fail → no
file-sourced links (fall back to `kcap whoami` per section 7) and no query — an unverifiable
`server_url` also means the server-binding check in section 4 cannot run. `background_log` and
`profile` are shown as plain text — never as a link or a command to run — only when they contain no
control characters and are under 512 characters; otherwise omit them.

Never splice an unvalidated value into SQL, a path, or a link.

## 3. No-handoff files

If `handoff_offered` is false, branch on `handoff_suppressed` and CLOSE:
- `no_new_sessions` → "nothing to watch — that import found no new sessions".
- `nothing_landed` → "the sessions that import selected were skipped at import time"; point at
  `kcap import --all` for per-session reasons; no retry.
- `analytics_not_in_plan` → the plan sentence (section 6); the import ran; no retry.
- `import_failed` → "that import did not get running"; name `kcap import --all --yes` and the
  `background_log` when displayable.
- `skill_not_installed` / `no_agent_detected` → the import ran and you evidently have the skill
  now: continue as if offered, watching the file's cohort as usual (sections 4–5).

## 4. Bind to the server — fail closed

If the selected file's `cohort` is `unknown`, skip this check and go straight to section 5 — there
is no cohort to query either way, and the mismatch remediation below would be misleading when
watching was never possible.

Otherwise, run `kcap whoami` and compare its server URL to `server_url`: lowercase scheme and host,
drop a default port (80 for http, 443 for https only), trim a trailing slash, compare the path
as-is. Match → continue to section 5. Mismatch or `whoami` failure → issue **NO** analytics query.
CLOSE with the file's links and this remediation: start your agent from a shell where `kcap whoami`
reports `<server_url>` — set `KCAP_PROFILE=<profile>`, unset `KCAP_URL`, and set `KCAP_CONFIG_DIR`
if kcap uses a custom config directory — then prompt again.

## 5. Watch the cohort

Cohort = `session_ids` (`cohort: unknown` or empty → CLOSE with links, no query). Open with:
"Watching N sessions from this import (K landed before the handoff)" — K is
`foreground_succeeded_ids.length`; for `partial_exact` add "the 500 most recent; older ones may
land and evaluate unobserved"; if `unattributed_on_disk` > 0 add the sentence from section 7.

Every query uses `query_analytics` with `scope: 'global'` (the MCP defaults to the caller's cwd
repo and will not widen on its own), **ONE call at a time — never two in flight** — and names only
cohort ids, each already validated against the grammar above, as single-quoted literals in an
`IN (...)` list or `session_id = '...'`. If a response ever includes a row whose `session_id` is
not in `session_ids`, drop that row before using the response for anything.

**Request budget:** at most 20 cohort queries per poll. `floor = ceil(N / 20)`. The FIRST poll uses
batch size `floor` exactly — no probing at a smaller size first. Every batch passes `max_rows`
equal to its own size:

    SELECT s.session_id, s.repo_hash, e.eval_run_id, e.evaluated_at, e.overall_score, e.judge_model
    FROM v_an_sessions s LEFT JOIN v_an_eval_summaries e ON e.session_id = s.session_id
    WHERE s.session_id IN ('<id>', '<id>', ...)

A row = arrived; a non-null `eval_run_id` = completed. Each response body carries `max_rows`, the
server's effective cap. After the first poll: if any body reported `max_rows < floor`, fail closed
at once — say the server's row cap is below what watching this many sessions needs, and CLOSE,
having spent no more than the 20-query budget. Otherwise raise the batch to `min(100, max_rows)`
for later polls. Any batch that comes back `truncated: true`, in the first poll or any later one,
fails that poll — the cap decides, not the truncation flag alone.

One poll = every batch it needs, each non-truncated. A failed or truncated batch discards the whole
poll — nothing from it commits — and counts as one failure toward the two-failure stop rule.

A 429 fails the poll. If its text matches `retry after (\d+)s`, wait that many seconds before the
next poll; otherwise wait 60 seconds. Never poll sooner than the 30-second cadence either way. HTTP
403 with `analytics_not_in_plan` → CLOSE immediately with the plan sentence (section 6); no
polling, no retry, no wait.

Progress = cohort ids that arrived / cohort size, computed from the batch rows only — never a
separate count, never repo- or tenant-wide. Cadence: first snapshot immediately, then every 30
seconds.

**Stop**, evaluated in this order after each poll whose results have already committed:
1. Three distinct cohort sessions have a completed eval → summarize the first three, ordered by
   `evaluated_at` then `session_id` (the same tie-break applies to rows already present at the very
   first snapshot, so more than three arriving at once always picks the same three).
2. `cohort: exact`, non-empty, and every id in `session_ids` has a completed eval → summarize what
   completed. Never fires for `partial_exact` or an empty cohort.
3. Two consecutive failed polls → stop, reporting the failures.
4. 10 minutes since the first snapshot → stop. No new poll starts after the deadline, but a poll
   already dispatched before it finishes normally, and its outcome is still checked against rules
   1–3 first: a third distinct completion in that final poll wins over the deadline summary, and if
   that final poll is itself the second consecutive failure, report it as the deadline stop while
   still naming the failures.

No idle early-stop — quiet polls are normal and are not a reason to stop early.

**Per-session detail** (one session per query, at most 6 queries across the whole run — enough for
the three sessions rule 1 summarizes):

    SELECT category, AVG(score) AS mean FROM v_an_eval_scores WHERE session_id = '<id>' GROUP BY category
    SELECT question_id, score FROM v_an_eval_scores WHERE session_id = '<id>' ORDER BY score ASC, question_id ASC LIMIT 2

Strongest = highest mean, weakest = lowest, ties alphabetical by category. A truncated or failed
detail query, or a session with no score rows, → summarize that session by `overall_score` alone
and say so; this never affects poll success or the stop rules above. When the `kcap-sessions` MCP is
available, use it to enrich a session's title; otherwise the session id is enough.

## 6. When little or nothing completes

Explain the real gates plainly: auto-eval may be off for a repository; the server needs an eval
agent configured; sessions under the minimum event count are skipped; already-evaluated sessions
are not re-run; a recovery sweep runs hourly. If the analytics MCP is absent entirely, close
immediately with the block below.

Plan sentence (used here and from sections 3 and 5): "Insights isn't in this workspace's plan, so
evals can't be followed from here; your import continues and evals appear in the Capacitor UI."

## 7. CLOSE — always

1. How to keep watching: prompt `Follow my kcap import` again (with the same `(run: <id>)` line
   when you were bound to one).
2. Links: with a `repo_hash` matching `^[0-9a-f]{16}$`, `<server_url>/repo/<repo_hash>/sessions/<session_id>?tab=evaluation`;
   otherwise (no `repo_hash`, or one that fails the grammar) `<server_url>/sessions/<session_id>?tab=evaluation`;
   all results `<server_url>/sessions`. No valid file → `server_url` from `kcap whoami`; if that
   fails too, say the results live in the Capacitor server UI and that `kcap whoami` prints the URL
   once logged in.
3. When `unattributed_on_disk` > 0: "N sessions on disk had no repository match; any of them that
   imported show without one — `kcap remap` places them."
4. Offer the guided tour with the exact prompt `Start kcap guided tour`.
