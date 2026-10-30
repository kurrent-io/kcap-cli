# Update release validation

Use an isolated machine or a disposable Tart clone for each starting version. Record executable versions, not only the bundle or registry labels. Preserve the original installer/package: a current build renamed to an old version does not exercise the old updater

The CLI/app and local-server changes ship separately. A candidate must include both. `/healthz/ready` retains its serving-readiness HTTP status; its additive `updatePending` field says that startup, online cutover, or a planned restart is still outstanding. New local start/restart/upgrade callers wait for a healthy replacement boot with that field false. Older bundles omit it and keep their existing readiness behavior. Setup's fenced readiness still checks serving health: waiting for a deferred rebuild before setup commits would deadlock

## Historical installations

| Starting point | Exercise | Required result |
| --- | --- | --- |
| Official 1.3.1 `Capacitor.app` | Install the candidate from its disk image and through the offered in-app route | One active `kcap.app`; correct CLI shim, daemon path and relaunch; saved configuration and local records survive |
| Official 1.4.0 stranded in the old bundle name | Install the candidate and relaunch again | Branding repair completes without a second stale copy taking ownership |
| Post-branding beta before the finish/retry fixes | In-app update with a running local server | App, local server and daemon show their own phases; early **Open kcap** carries progress into the launcher |
| Official 1.4.0-beta.7 | Update while the predecessor is running, then repeat by direct install | Record predecessor exit time and updater log; do not count only the replacement window appearing as success |
| Official local server 1.4.1-beta.1 | Upgrade a disposable database requiring online rebuild | Cutover requests exit 10; launchd/systemd starts the replacement; command completion follows replacement readiness |
| Current app with an older local server | Relaunch; repeat after a missing bundle becomes available | Local upgrade is retried; a missing artifact is not recorded as a completed upgrade |
| Current script/npm CLI with an older local server | Run `kcap update`, then `kcap update --check` | Ordinary update retries local upgrade without reinstalling the CLI; check-only performs no install |

The 1.4.0 branding and beta.1 local-restart defects were in published artifacts at the audit snapshot. A PR does not repair those artifacts: publish a new immutable stable/beta candidate through the normal release workflow, then run this matrix against those exact bytes. Never replace a published tag or archive

An old beta.7 process executes its own shutdown code. The candidate cannot retroactively change it; retain this real historical fixture and verify the direct-installer recovery route as well as the in-app route. Any old-parent timeout or crash remains a release acceptance failure, even when the candidate subsequently works

## Feedback and recovery

| Scenario | Required result |
| --- | --- |
| Slow manual feed check | A visible checking state appears immediately; no second concurrent check |
| Manual check during a background download | Current download progress appears; hiding it leaves the download running; another manual check reveals it again |
| Download, verification and extraction | Archive percentage ends at the archive boundary; later work is labelled separately |
| Search-model download | Model percentage uses model bytes, not the completed archive percentage |
| Search-model failure after a successful server upgrade | Upgrade remains successful with a keyword-search note and Settings → Search recovery; successful search configuration clears the note |
| Missing matching server bundle | Existing server stays selected; retry remains available |
| Invalid signature or archive checksum | Old server keeps running; no switch or migration occurs |
| Long network error, including a long URL | Concise recovery text; selectable details; **Open kcap** stays reachable on a small display |
| Concurrent local lifecycle command | Status says the operation is in progress while its lease is held; an abandoned journal is reported as interrupted |
| Failure before switch | Existing version remains selected; retry is offered only for retryable failures |
| Failure after switch | New version is retained and stopped; **Start** finishes recovery; no automatic data downgrade |
| Previously stopped local server | Candidate is briefly started to validate migration, then restored to stopped |
| Slow and empty online rebuilds | Readiness can stay HTTP 200 with `updatePending: true`; CLI waits through cutover and restart; no startup-cancellation fatal log |
| Several planned restarts | Exit 10 is not treated as a crash loop; the overall start deadline still applies |
| Connected desktop/daemon reconnecting during server shutdown | Late hub connections are aborted; shutdown does not spend its full timeout on a newly admitted websocket |
| Unexpected repeated exits | Existing crash-loop failure and actionable logs remain |
| Active daemon turns, auto-resume off, terminal daemon | Copy describes bounded waiting and eligible auto-resume accurately; a terminal daemon receives manual-restart guidance |
| Beta choice differs from installed version | Configured channel wins; explicit `--beta` / `--stable` guidance is available; server-capped advice still works for older clients |

Record check, download, verify/extract, service stop, migration/cutover, replacement start and daemon reconnect separately. Closing the finish window early is not update completion. Capture both the finish window and the launched-app progress banner from the running candidate, including a failure state on the smallest supported screen
