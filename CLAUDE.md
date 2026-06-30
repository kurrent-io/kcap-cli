# Kurrent Capacitor CLI

**File paths:** CLI source at `src/Capacitor.Cli/`, shared core at `src/Capacitor.Cli.Core/`, daemon at `src/Capacitor.Cli.Daemon/`, npm packages at `npm/`, Claude Code plugin at `kcap/`, unit tests at `test/Capacitor.Cli.Tests.Unit/`, integration tests at `test/Capacitor.Cli.Tests.Integration/`.

## What this project does

The `kcap` CLI records Claude Code sessions by forwarding hook payloads and transcript data to a Kurrent Capacitor server. It also hosts an agent daemon for remote Claude CLI management and provides PR review context via MCP tools.

## Tech stack

- .NET 10, NativeAOT compiled
- SignalR client for real-time server communication
- TUnit for testing, WireMock.Net for HTTP mocking

## Building

```bash
dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj
```

## Running tests

Tests use TUnit on Microsoft Testing Platform. Run directly as executables:

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj
dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj
```

## Publishing

AOT publish for the current platform:

```bash
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release
```

Always verify no IL3050/IL2026 AOT warnings after changes:

```bash
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```

## Issues and pull requests

This is a public repository — we develop in the open.

- **Open issues in GitHub Issues**, not Linear. Linear auto-imports GitHub issues, so there is no need to create the issue in Linear by hand.
- **PRs must reference both the Linear issue and the GitHub issue.** Put these references in the PR *description*, not the title (the title stays clean and human-readable). Reference the GitHub issue with a closing keyword (e.g. `Closes #123`) and include the Linear issue (e.g. `AI-774`) so Linear links the PR back to the imported issue.

## Common mistakes to avoid

- **AOT warnings only show on publish** — `dotnet build` does NOT surface IL3050/IL2026 trimming warnings. Run `dotnet publish -c Release` after changes.
- **JsonArray collection expressions** — `[item1, item2]` compiles to `Add<T>()` which requires dynamic code. Use `new JsonArray(item1, item2)` constructor instead.
- **TUnit test filtering** — Use `--treenode-filter` with glob syntax, NOT `--filter`.
- **macOS AOT binary code signing** — After copying an AOT binary, run `codesign --force --sign -` to re-sign.
- **README sync on CLI changes** — Any change to user-facing CLI surface (new command, new/renamed/removed flag, changed default behavior, new prerequisite) must update `README.md` in the *same* PR. Check both the quick-start (`## Getting started`) and the per-command section under `## CLI commands`. Updating only `src/Capacitor.Cli.Core/Resources/help-*.txt` is not enough — the README is the public-facing docs. This has been missed repeatedly and has required follow-up doc-only PRs (#60, #61).
