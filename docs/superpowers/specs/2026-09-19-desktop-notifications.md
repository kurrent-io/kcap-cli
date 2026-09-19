# Desktop notifications

Implements GitHub #1047 / AI-2997. Notify users when an agent needs a permission, asks a question, or finishes a turn. The user chose notifications only while the application is in the background (no active app window).

Use an app-lifetime coordinator over the existing merged permission cache and agent directory. Native notification delivery is a separate adapter. Permission actions reuse IPermissionService and must validate that the request is still pending; never turn a notification click into a broad permission outside the existing service. Claude offers Allow, Always, Decline; ACP offers the equivalent actions only when supplied by its request. Questions offer Respond in app; idle offers Open agent. A body click opens the corresponding agent.

Deduplicate pending request replays and local/server twins. Remove delivered notifications when requests settle, agents leave, a category is disabled, or the app shuts down. Foreground events are consumed without later replay. Idle means a known working agent transitions to awaiting input, with no pending request and no running subagents; initial snapshots and unknown states do not generate idle alerts. The server registry does not carry a remote idle verdict, so remote idle cannot be inferred from its Running status.

Keep remote permission subscriptions alive independently of visible workspaces, using SessionAccessService leases for current server sessions. Existing authorization checks and reconciliation remain authoritative.

Persist three independent preferences, all defaulting true, in app-owned notification settings. Settings has Daemon and Notifications tabs at its current fixed size. Changes apply immediately, persist across reopening/restart, and expose write failures. Use the Kcap palette and existing control styling.

Native delivery must work in the shipped macOS application bundle (macOS 15+, Apple silicon). Optional Windows/Linux backends do not extend the application's supported-platform promise. Denied permission, unavailable desktop services and unbundled development launches must not crash the app. Notification callbacks return to the UI scheduler. Platform permission prompts remain OS-owned.

Verify defaults and persistence, category/foreground suppression, edge-triggered idle, request replay and settlement, exact response routing, stale actions, remote lease lifetime, settings bindings, plus app rebuild and publish. Native OS banners require packaged-platform smoke testing in addition to unit tests.
