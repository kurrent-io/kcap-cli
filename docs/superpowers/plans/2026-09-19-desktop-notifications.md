# Desktop Notifications Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans for the coordinator and integration. Independent settings and native-adapter investigations may run under superpowers:dispatching-parallel-agents.

**Goal:** Deliver actionable background desktop notifications and per-category settings for #1047.

**Architecture:** A coordinator consumes IPermissionService and IAgentDirectory, delegates native presentation to an adapter, and navigates through MainWindowCoordinator. A shared notification preferences service owns persistence and immediate updates. Session leases keep remote requests available with the main window hidden.

**Tech Stack:** .NET 10, Avalonia 12.1.2, ReactiveUI, DynamicData, TUnit, UserNotifications on macOS and Avalonia.Labs.Notifications 12.0.2 for optional platforms.

**Spec:** ../specs/2026-09-19-desktop-notifications.md

## Global Constraints

- Background only means no active application window.
- All three categories default on, including missing settings files/fields.
- Do not fabricate ACP permission actions or remote idle state.
- Preserve the current 540 × 580 settings window and daemon behavior.
- Keep all permission decisions on IPermissionService.

## Review Focus

- Reconnects and local/server twins must not duplicate an alert.
- A settled or disabled notification cannot authorize an old request.
- Unknown/startup idle states must not generate a burst of alerts.
- Hiding the window must not release notification subscriptions.
- Notification failures must not interrupt agent control or application startup.

### Task 1: Native platform investigation

- [x] Verify the chosen package's current APIs, compatibility, action and dismissal behavior from upstream source.

### Task 2: Preferences and settings

Files: NotificationPreferences.cs, NotificationSettingsService.cs, SettingsViewModel.cs, SettingsWindow.axaml, settings tests.

Interface: NotificationPreferences(bool Permissions = true, bool Questions = true, bool Idle = true). NotificationSettingsService exposes Current, Changes, and Task<bool> SaveAsync(NotificationPreferences).

- [x] Add failing tests for old/missing data defaults, persisted independent switches and write failures.
- [x] Implement atomic persistence in notifications.json and shared observable state.
- [x] Bind three controls through SettingsViewModel; split existing daemon content and notifications into themed tabs.
- [x] Run settings and persistence tests, including switching tabs and reopening the window.

### Task 3: Notification coordination and native adapter

Files: Services/Notifications/*, App.axaml.cs, Program.cs, package references, notification tests.

Interface: IDesktopNotificationSink.Show(DesktopNotification, Action<string?>), Close(string id). DesktopNotification has Id, Title, Body, Actions (id/label pairs). A null action means body activation; action ids are opaque adapter data.

- [x] Test new pending requests produce actionable notifications only in background with category enabled.
- [x] Test duplicate replay, removal, current-action validation and transport failure behavior.
- [x] Test question/open-agent actions and working-to-idle transitions; suppress initial idle, pending permissions, protected agents and live subagents.
- [x] Implement coordinator and native adapter with safe callback marshalling.
- [x] Add lifetime-owned remote permission session leases and test disposal/removal behavior.
- [x] Advertise optional ACP grant-scope capabilities over local IPC and preserve server fallback atomically.
- [x] Cover directory-before-permission handover, delayed twin correlation and foreign-server isolation.
- [x] Wire shared settings, native backend and coordinator into app startup/shutdown and navigation.

### Task 4: Verification and review

- [x] Run the full desktop test assembly and inspect failures.
- [x] Rebuild the desktop app and publish affected shipping projects, clearing warnings.
- [x] Finish the user-requested Claude review flow after addressing findings and rerunning affected tests.
- [x] Record platform smoke-test coverage and any limitations in the delivery report.

## Verification results

- Full desktop assembly after PR feedback: 2,636 tests passed with `LC_ALL=en_US.UTF-8 LANG=en_US.UTF-8`; the default Norwegian locale exposed an existing decimal-separator expectation in AttachmentTrayTests.
- Focused IPC/wire tests: 27 passed. Focused daemon permission tests after initial review fixes: 116 passed; after stricter ACP capability checks, all 62 affected daemon tests passed. All 41 focused coordinator/native category/block tests passed after PR feedback.
- Release desktop rebuild: zero warnings and errors. macOS self-contained app publish passed.
- CLI and daemon NativeAOT publishes passed without trimming warnings; both published binaries passed their version command. Clean local AOT checks used `AppleMinOSVersion=26.0` to match installed Homebrew libraries; repository deployment targets are unchanged.
- Settings smoke tests exercise mouse clicks, dragging, keyboard toggling/focus, selected-tab styling, persistence and reopening at 540 × 580.
- Review findings for delayed directory rows, reconnect handover, shutdown cleanup, duplicate ACP grant scopes and stale native action tokens have regression coverage.
- Signed isolated macOS smoke confirmed authorization, managed background delivery, withdrawal to zero delivered notifications, and disposal. The bundle must live outside `/tmp` for macOS notification registration.
- Actual OS notification button/body clicks and foreground presentation remain unverified: the automation could not access Notification Center notifications, and this captured-display environment did not invoke the isolated foreground callback. Those paths have unit/source coverage; no global OS settings were changed.
- Claude round one found six issues: switch dragging, reconnect alert loss, missing Codex actions, theme placement/accent, ambiguous ACP standing scopes, and persistent save-error text. All are corrected with regressions; Windows action labels now also escape XML. Round two reviewed commit `fa97c18e` and returned clean in flow `7ff25ce97d0145c2ac99d5aa83afaf4d`; the flow is closed.
- PR #1055 feedback identified ambiguous/unaddressable ACP options on both lanes, stale pending requests failing to suppress idle, and dynamic macOS categories retained until shutdown. These now have regression coverage and fixes. The separate mutation-lane recommendation is inapplicable: permission replies share the broker's atomic settlement path, consistent with the maintainer's decision on PR #770.
