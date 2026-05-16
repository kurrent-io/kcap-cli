# @kurrent/kapacitor

CLI companion for [Kurrent Capacitor](https://github.com/kurrent-io/kapacitor) — records and visualizes Claude Code sessions.

## Install

```bash
npm install -g @kurrent/kapacitor
```

## Setup

```bash
kapacitor setup
```

This walks you through: server URL, authentication, Claude Code plugin installation, and verification.

## Commands

```
kapacitor setup                  Configure server, login, and install plugin
kapacitor status                 Show server, auth, and daemon status
kapacitor daemon start [-d]      Start the daemon
kapacitor daemon stop            Stop the daemon
kapacitor review <pr>            Launch Claude Code with PR review context
kapacitor update                 Check for updates
kapacitor --version              Show version
kapacitor --help                 Show all commands
```

## PR Review

Start a Claude Code session with tools to query implementation context for a PR:

```bash
kapacitor review https://github.com/owner/repo/pull/123
# or
kapacitor review owner/repo#123
```

Claude gets MCP tools to search session transcripts, understand per-file rationale, and explain design decisions made during implementation.

## Upgrading from v1

The v1 config format stored `server_url` as a bare host name without a
scheme. If `kapacitor` crashes with `An invalid request URI was provided`
after upgrading, your config still has the old format. Fix it with one
command:

    kapacitor config set server_url https://your-server.example.com

Or remove the config file and re-run setup:

    rm ~/.config/kapacitor/config.json
    kapacitor setup
