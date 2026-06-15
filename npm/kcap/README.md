# @kurrent/kcap

CLI companion for [Kurrent Capacitor](https://github.com/kurrent-io/kcap-cli) — records and visualizes Claude Code sessions.

## Install

```bash
npm install -g @kurrent/kcap
```

## Setup

```bash
kcap setup
```

This walks you through: server URL, authentication, Claude Code plugin installation, and verification.

## Commands

```
kcap setup                  Configure server, login, and install plugin
kcap status                 Show server, auth, and daemon status
kcap daemon start [-d]      Start the daemon
kcap daemon stop            Stop the daemon
kcap review <pr>            Launch Claude Code with PR review context
kcap update                 Upgrade the CLI and refresh agent plugins
kcap --version              Show version
kcap --help                 Show all commands
```

## PR Review

Start a Claude Code session with tools to query implementation context for a PR:

```bash
kcap review https://github.com/owner/repo/pull/123
# or
kcap review owner/repo#123
```

Claude gets MCP tools to search session transcripts, understand per-file rationale, and explain design decisions made during implementation.

## Upgrading from v1

The v1 config format stored `server_url` as a bare host name without a
scheme. If `kcap` crashes with `An invalid request URI was provided`
after upgrading, your config still has the old format. Fix it with one
command:

    kcap config set server_url https://your-server.example.com

Or remove the config file and re-run setup:

    rm ~/.config/kcap/config.json
    kcap setup
