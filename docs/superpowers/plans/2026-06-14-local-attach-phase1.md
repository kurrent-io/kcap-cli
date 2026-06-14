# Local Terminal Attach — Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user start a daemon-hosted coding agent from their own terminal (`kcap run-agent`), drive it live over a local socket, detach without killing it, and reattach — "tmux for your agent," entirely local, with no new server contract.

**Architecture:** The daemon already owns the agent PTY and streams it to one client (the web UI over SignalR). Phase 1 generalizes "one client" to "N local-socket clients": a new Unix-domain-socket (named-pipe on Windows) control channel in the daemon, a per-sink lossless terminal fan-out, a local-launch path that runs the agent **`PrivateLocal`** (no server calls) in a **borrowed cwd** or owned worktree, and a thin raw-mode CLI client. Sharing to the web (`Shared`) is Phase 2 and out of scope here.

**Tech Stack:** .NET 10 NativeAOT, `System.Net.Sockets.UnixDomainSocketEndPoint`, `System.Threading.Channels`, `LibraryImport` P/Invoke (termios), TUnit on Microsoft Testing Platform.

**Spec:** `docs/superpowers/specs/2026-06-13-local-attach-hosted-agent-design.md`

---

## Conventions used by every task

- **Run all unit tests:** `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`
- **Run one test class (TUnit uses `--treenode-filter`, NOT `--filter`):**
  `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/FrameCodecTests/*"`
- **AOT warning gate (run after each milestone):**
  `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` → expect **no output**.
- **AOT rule:** no reflection-based serializers in the framing; hand-roll bytes. `JsonArray` collection-expressions are banned (use `new JsonArray(...)`).
- **Commit** after each task with the message shown in its final step.

---

## File Structure

**New (shared — `Capacitor.Cli.Core`, referenced by both CLI and daemon):**
- `src/Capacitor.Cli.Core/LocalIpc/FrameType.cs` — the frame-type enum (wire contract).
- `src/Capacitor.Cli.Core/LocalIpc/LocalFrame.cs` — frame payload records.
- `src/Capacitor.Cli.Core/LocalIpc/FrameCodec.cs` — length-prefixed binary read/write.
- `src/Capacitor.Cli.Core/LocalIpc/LocalSocketPaths.cs` — per-daemon-name socket path.
- `src/Capacitor.Cli.Core/RunAgentArgs.cs` — parses `run-agent` kcap-flags vs `--` passthrough.

**New (daemon — `Capacitor.Cli.Daemon`):**
- `src/Capacitor.Cli.Daemon/Services/ITerminalSink.cs` — one output consumer.
- `src/Capacitor.Cli.Daemon/Services/LocalSocketSink.cs` — per-local-client lossless queue + force-detach.
- `src/Capacitor.Cli.Daemon/Services/TerminalFanout.cs` — N-sink registry per agent.
- `src/Capacitor.Cli.Daemon/Services/LocalControlServer.cs` — socket listener + connection handler.

**New (CLI — `Capacitor.Cli`):**
- `src/Capacitor.Cli/Local/TerminalRawMode.cs` — termios raw-mode set/restore (P/Invoke).
- `src/Capacitor.Cli/Local/LocalAgentClient.cs` — connect, pumps, resize, detach scan.
- `src/Capacitor.Cli/Commands/RunAgentCommand.cs` — `run-agent` / `attach` / `ls` dispatch + ensure-daemon.

**Modified (daemon):**
- `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` — `WorkLocationKind` + `IsPrivate` on `AgentInstance`; `SpawnLocalAgentAsync`; deny-all server guard; cleanup guard; fan-out wiring; conditional env.
- `src/Capacitor.Cli.Daemon/Services/IHostedAgentLauncher.cs` + `ClaudeLauncher.cs` + `CodexLauncher.cs` — passthrough `BuildArgs` path + borrowed-cwd `Prepare` skip.
- `src/Capacitor.Cli.Daemon/DaemonRunner.cs` — register `LocalControlServer` hosted service.

**Modified (CLI):**
- `src/Capacitor.Cli/Program.cs` — dispatch `run-agent`/`attach`/`ls`.
- `README.md` — quick-start + per-command docs (required by CLAUDE.md).

---

## Milestone A — Frame protocol (pure, fully testable)

### Task A1: Frame types

**Files:**
- Create: `src/Capacitor.Cli.Core/LocalIpc/FrameType.cs`
- Create: `src/Capacitor.Cli.Core/LocalIpc/LocalFrame.cs`

- [ ] **Step 1: Write the enum + payloads**

`FrameType.cs`:
```csharp
namespace Capacitor.Cli.Core.LocalIpc;

/// Wire contract for the daemon↔local-client socket. Values are explicit and
/// MUST be append-only — they are serialized as a single byte (see FrameCodec).
public enum FrameType : byte {
    // client → daemon
    Spawn   = 1,
    Attach  = 2,
    Stdin   = 3,
    Resize  = 4,
    Detach  = 5,
    List    = 6,   // request the daemon's agent list (for `kcap ls`)
    // daemon → client
    Attached  = 64,
    Stdout    = 65,
    Exited    = 66,
    Error     = 67,
    AgentList = 68, // UTF-8 table payload: one `id\tstatus\tcwd` line per agent
}
```

`LocalFrame.cs`:
```csharp
namespace Capacitor.Cli.Core.LocalIpc;

public enum WorkLocation : byte { BorrowedCwd = 0, OwnedWorktree = 1 }

/// A decoded frame. Exactly one of the payload properties is meaningful per Type;
/// the codec owns which. Bytes are raw (Stdin/Stdout); strings are UTF-8.
public sealed record LocalFrame(FrameType Type) {
    public byte[]      Bytes   { get; init; } = [];
    public string      Text    { get; init; } = "";
    public ushort      Cols    { get; init; }
    public ushort      Rows    { get; init; }
    public WorkLocation Work   { get; init; }
    public int         ExitCode { get; init; }

    public static LocalFrame Stdin(byte[] b)            => new(FrameType.Stdin)  { Bytes = b };
    public static LocalFrame Stdout(byte[] b)           => new(FrameType.Stdout) { Bytes = b };
    public static LocalFrame Resize(ushort c, ushort r) => new(FrameType.Resize) { Cols = c, Rows = r };
    public static LocalFrame Detach()                   => new(FrameType.Detach);
    public static LocalFrame Exited(int code)           => new(FrameType.Exited) { ExitCode = code };
    public static LocalFrame Error(string m)            => new(FrameType.Error)  { Text = m };
}
```

- [ ] **Step 2: Commit**

```bash
git add src/Capacitor.Cli.Core/LocalIpc/FrameType.cs src/Capacitor.Cli.Core/LocalIpc/LocalFrame.cs
git commit -m "feat(local-ipc): frame types for daemon-client socket"
```

### Task A2: Frame codec (length-prefixed, AOT-safe)

**Files:**
- Create: `src/Capacitor.Cli.Core/LocalIpc/FrameCodec.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/FrameCodecTests.cs`

**Wire format per frame:** `[1 byte Type][4 byte big-endian payload length N][N payload bytes]`. Payload layout by type: `Spawn` = `[1 byte WorkLocation][2-byte BE cols][2-byte BE rows][4-byte BE vendorLen][vendor utf8][4-byte BE cwdLen][cwd utf8][4-byte BE argCount]{[4-byte BE argLen][arg utf8]}*`; `Attach` = utf8 agentId; `Stdin`/`Stdout` = raw bytes; `Resize` = `[2-byte BE cols][2-byte BE rows]`; `Exited` = `[4-byte BE int]`; `Error`/`Attached` = utf8 (Attached also carries the replay snapshot as the payload tail — encoded as `[4-byte BE agentIdLen][agentId utf8][snapshot bytes...]`); `Detach` = empty.

- [ ] **Step 1: Write the failing round-trip test**

`FrameCodecTests.cs`:
```csharp
using System.IO;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Tests.Unit;

public class FrameCodecTests {
    static async Task<LocalFrame> RoundTrip(LocalFrame f) {
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, f, default);
        ms.Position = 0;
        var read = await FrameCodec.ReadAsync(ms, default);
        return read!;
    }

    [Test]
    public async Task Stdin_round_trips_raw_bytes() {
        var f = LocalFrame.Stdin([0x00, 0x1b, 0x5b, 0x41, 0xff]);
        var r = await RoundTrip(f);
        await Assert.That(r.Type).IsEqualTo(FrameType.Stdin);
        await Assert.That(r.Bytes).IsEquivalentTo(new byte[] { 0x00, 0x1b, 0x5b, 0x41, 0xff });
    }

    [Test]
    public async Task Resize_round_trips_dimensions() {
        var r = await RoundTrip(LocalFrame.Resize(203, 51));
        await Assert.That(r.Cols).IsEqualTo((ushort)203);
        await Assert.That(r.Rows).IsEqualTo((ushort)51);
    }

    [Test]
    public async Task Spawn_round_trips_vendor_cwd_args_and_worklocation() {
        var f = new LocalFrame(FrameType.Spawn) {
            Work = WorkLocation.BorrowedCwd, Cols = 120, Rows = 40,
            Text = "claude", Bytes = [],
        };
        // Args carried via a dedicated builder — see FrameCodec.Spawn(...)
        var built = FrameCodec.Spawn("codex", WorkLocation.OwnedWorktree, "/repo", ["--model", "opus", "fix it"], 100, 30);
        var r = await RoundTrip(built);
        await Assert.That(r.Type).IsEqualTo(FrameType.Spawn);
        await Assert.That(r.Text).IsEqualTo("codex");
        await Assert.That(r.Work).IsEqualTo(WorkLocation.OwnedWorktree);
        await Assert.That(FrameCodec.SpawnCwd(r)).IsEqualTo("/repo");
        await Assert.That(FrameCodec.SpawnArgs(r)).IsEquivalentTo(new[] { "--model", "opus", "fix it" });
    }

    [Test]
    public async Task ReadAsync_returns_null_on_clean_eof() {
        using var ms = new MemoryStream();
        await Assert.That(await FrameCodec.ReadAsync(ms, default)).IsNull();
    }

    [Test]
    public async Task ReadAsync_reassembles_across_short_reads() {
        var f = LocalFrame.Stdout(new byte[5000]); // > one socket read
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, f, default);
        ms.Position = 0;
        using var choppy = new ChoppyStream(ms.ToArray(), chunk: 7);
        var r = await FrameCodec.ReadAsync(choppy, default);
        await Assert.That(r!.Bytes.Length).IsEqualTo(5000);
    }
}

/// Stream that returns at most `chunk` bytes per ReadAsync to simulate partial socket reads.
sealed class ChoppyStream(byte[] data, int chunk) : Stream {
    int _pos;
    public override int Read(byte[] buffer, int offset, int count) {
        if (_pos >= data.Length) return 0;
        var n = Math.Min(Math.Min(chunk, count), data.Length - _pos);
        Array.Copy(data, _pos, buffer, offset, n); _pos += n; return n;
    }
    public override ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct = default) {
        if (_pos >= data.Length) return ValueTask.FromResult(0);
        var n = Math.Min(Math.Min(chunk, b.Length), data.Length - _pos);
        data.AsSpan(_pos, n).CopyTo(b.Span); _pos += n; return ValueTask.FromResult(n);
    }
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => data.Length; public override long Position { get => _pos; set => _pos = (int)value; }
    public override void Flush() { } public override long Seek(long o, SeekOrigin s) => 0;
    public override void SetLength(long v) { } public override void Write(byte[] b, int o, int c) { }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/FrameCodecTests/*"`
Expected: FAIL — `FrameCodec` does not exist.

- [ ] **Step 3: Implement `FrameCodec`**

`FrameCodec.cs`:
```csharp
using System.Buffers.Binary;
using System.Text;

namespace Capacitor.Cli.Core.LocalIpc;

/// Length-prefixed binary codec: [1B type][4B BE len][len payload]. No reflection /
/// JSON — AOT-safe and allocation-light. Spawn/Attached have structured payloads
/// (helpers below); other frames are trivial.
public static class FrameCodec {
    const int MaxPayload = 8 * 1024 * 1024; // hard cap; oversized => protocol error

    public static async Task WriteAsync(Stream s, LocalFrame f, CancellationToken ct) {
        var payload = Encode(f);
        var head = new byte[5];
        head[0] = (byte)f.Type;
        BinaryPrimitives.WriteInt32BigEndian(head.AsSpan(1), payload.Length);
        await s.WriteAsync(head, ct);
        if (payload.Length > 0) await s.WriteAsync(payload, ct);
        await s.FlushAsync(ct);
    }

    /// Returns null on clean EOF (peer closed between frames).
    public static async Task<LocalFrame?> ReadAsync(Stream s, CancellationToken ct) {
        var head = new byte[5];
        if (!await ReadExactlyOrEof(s, head, ct)) return null;
        var type = (FrameType)head[0];
        var len  = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(1));
        if (len is < 0 or > MaxPayload) throw new InvalidDataException($"frame len {len}");
        var payload = len == 0 ? [] : new byte[len];
        if (len > 0 && !await ReadExactlyOrEof(s, payload, ct))
            throw new EndOfStreamException("truncated frame payload");
        return Decode(type, payload);
    }

    static async Task<bool> ReadExactlyOrEof(Stream s, byte[] buf, CancellationToken ct) {
        var off = 0;
        while (off < buf.Length) {
            var n = await s.ReadAsync(buf.AsMemory(off), ct);
            if (n == 0) return off != 0 ? throw new EndOfStreamException("truncated frame header") : false;
            off += n;
        }
        return true;
    }

    static byte[] Encode(LocalFrame f) => f.Type switch {
        FrameType.Stdin or FrameType.Stdout => f.Bytes,
        FrameType.Resize                    => Dims(f.Cols, f.Rows),
        FrameType.Detach or FrameType.List  => [],
        FrameType.Exited                    => BeInt(f.ExitCode),
        FrameType.Error or FrameType.Attach or FrameType.AgentList => Encoding.UTF8.GetBytes(f.Text),
        FrameType.Attached                  => f.Bytes,           // pre-encoded by Attached(...)
        FrameType.Spawn                     => f.Bytes,           // pre-encoded by Spawn(...)
        _ => throw new InvalidDataException($"unencodable frame {f.Type}"),
    };

    static LocalFrame Decode(FrameType t, byte[] p) => t switch {
        FrameType.Stdin or FrameType.Stdout => new(t) { Bytes = p },
        FrameType.Resize  => new(t) { Cols = Be16(p, 0), Rows = Be16(p, 2) },
        FrameType.Detach or FrameType.List => new(t),
        FrameType.Exited  => new(t) { ExitCode = BinaryPrimitives.ReadInt32BigEndian(p) },
        FrameType.Error or FrameType.Attach or FrameType.AgentList => new(t) { Text = Encoding.UTF8.GetString(p) },
        FrameType.Attached or FrameType.Spawn => new(t) { Bytes = p },
        _ => throw new InvalidDataException($"undecodable frame {t}"),
    };

    // --- Spawn structured payload ---
    public static LocalFrame Spawn(string vendor, WorkLocation work, string cwd, IReadOnlyList<string> args, ushort cols, ushort rows) {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)work);
        WriteBe16(ms, cols); WriteBe16(ms, rows);
        WriteLp(ms, vendor); WriteLp(ms, cwd);
        WriteBe32(ms, args.Count);
        foreach (var a in args) WriteLp(ms, a);
        return new(FrameType.Spawn) { Bytes = ms.ToArray(), Text = vendor, Work = work, Cols = cols, Rows = rows };
    }
    public static string SpawnCwd(LocalFrame f) { var (s, _) = ParseSpawn(f.Bytes); return s.cwd; }
    public static string[] SpawnArgs(LocalFrame f) { var (s, _) = ParseSpawn(f.Bytes); return s.args; }
    public static (string vendor, WorkLocation work, string cwd, string[] args, ushort cols, ushort rows) Spawn(LocalFrame f)
        => ParseSpawn(f.Bytes).s;

    static ((string vendor, WorkLocation work, string cwd, string[] args, ushort cols, ushort rows) s, int end) ParseSpawn(byte[] p) {
        var o = 0;
        var work = (WorkLocation)p[o++];
        var cols = Be16(p, o); o += 2; var rows = Be16(p, o); o += 2;
        var vendor = ReadLp(p, ref o); var cwd = ReadLp(p, ref o);
        var n = BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(o)); o += 4;
        var args = new string[n];
        for (var i = 0; i < n; i++) args[i] = ReadLp(p, ref o);
        return ((vendor, work, cwd, args, cols, rows), o);
    }

    // --- Attached structured payload: [4B agentIdLen][agentId][snapshot...] ---
    public static LocalFrame Attached(string agentId, byte[] snapshot) {
        using var ms = new MemoryStream();
        WriteLp(ms, agentId);
        ms.Write(snapshot);
        return new(FrameType.Attached) { Bytes = ms.ToArray(), Text = agentId };
    }
    public static (string agentId, byte[] snapshot) Attached(LocalFrame f) {
        var o = 0; var id = ReadLp(f.Bytes, ref o);
        return (id, f.Bytes[o..]);
    }

    // --- byte helpers ---
    static byte[] Dims(ushort c, ushort r) { var b = new byte[4]; BinaryPrimitives.WriteUInt16BigEndian(b, c); BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), r); return b; }
    static byte[] BeInt(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); return b; }
    static ushort Be16(byte[] p, int o) => BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(o));
    static void WriteBe16(Stream s, ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); s.Write(b); }
    static void WriteBe32(Stream s, int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); s.Write(b); }
    static void WriteLp(Stream s, string v) { var u = Encoding.UTF8.GetBytes(v); WriteBe32(s, u.Length); s.Write(u); }
    static string ReadLp(byte[] p, ref int o) { var n = BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(o)); o += 4; var v = Encoding.UTF8.GetString(p, o, n); o += n; return v; }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/FrameCodecTests/*"`
Expected: PASS (5 tests).

- [ ] **Step 5: AOT gate + commit**

```bash
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'   # expect no output
git add src/Capacitor.Cli.Core/LocalIpc/FrameCodec.cs test/Capacitor.Cli.Tests.Unit/FrameCodecTests.cs
git commit -m "feat(local-ipc): length-prefixed AOT-safe frame codec + tests"
```

---

## Milestone B — Daemon local socket listener

### Task B1: Socket path

**Files:**
- Create: `src/Capacitor.Cli.Core/LocalIpc/LocalSocketPaths.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/LocalSocketPathsTests.cs`

- [ ] **Step 1: Failing test**

```csharp
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Tests.Unit;

public class LocalSocketPathsTests {
    [Test]
    public async Task Socket_path_is_under_daemon_dir_and_name_sanitized() {
        var p = LocalSocketPaths.Socket("My Daemon!");
        await Assert.That(p).EndsWith("my-daemon.sock");
        await Assert.That(p).StartsWith(DaemonLockPaths.Directory);
    }
}
```

- [ ] **Step 2: Run, expect FAIL** (`LocalSocketPaths` missing).
  Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/LocalSocketPathsTests/*"`

- [ ] **Step 3: Implement**

```csharp
namespace Capacitor.Cli.Core.LocalIpc;

/// Per-daemon-name local control socket, colocated with the daemon's lock/pid files.
public static class LocalSocketPaths {
    public static string Socket(string daemonName)
        => Path.Combine(DaemonLockPaths.Directory, $"{DaemonLockPaths.Sanitize(daemonName)}.sock");
}
```

- [ ] **Step 4: Run, expect PASS.**

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/LocalIpc/LocalSocketPaths.cs test/Capacitor.Cli.Tests.Unit/LocalSocketPathsTests.cs
git commit -m "feat(local-ipc): per-daemon-name socket path"
```

### Task B2: `LocalControlServer` hosted service

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/LocalControlServer.cs`
- Modify: `src/Capacitor.Cli.Daemon/DaemonRunner.cs` (register hosted service — near the other `AddSingleton`/`AddHostedService` calls around `:161`).

> This task is I/O glue; its correctness is covered by the integration test in Task B3. Build-only verification here.

- [ ] **Step 1: Implement the listener**

`LocalControlServer.cs` — a `BackgroundService`. Binds a `Socket(AddressFamily.Unix, …)` to a `UnixDomainSocketEndPoint`, deletes any stale socket file first, sets `0600`, accept-loops, and hands each connection to a handler that decodes frames via `FrameCodec` and calls into `AgentOrchestrator` (Task D5/C2). Mirror the death-rattle/lifetime patterns already in the daemon.

```csharp
using System.Net.Sockets;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

internal sealed partial class LocalControlServer(
        DaemonConfig config, AgentOrchestrator orchestrator, ILogger<LocalControlServer> logger
    ) : BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken ct) {
        var path = LocalSocketPaths.Socket(config.Name);
        try { if (File.Exists(path)) File.Delete(path); } catch { /* stale; bind will fail loudly below */ }

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); // 0600
        listener.Listen(16);
        LogListening(path);

        try {
            while (!ct.IsCancellationRequested) {
                var conn = await listener.AcceptAsync(ct);
                _ = HandleConnectionAsync(conn, ct); // fire-and-forget; handler owns its lifetime
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* shutdown */ }
        finally { try { File.Delete(path); } catch { /* best-effort */ } }
    }

    async Task HandleConnectionAsync(Socket conn, CancellationToken ct) {
        using var _ = conn;
        using var stream = new NetworkStream(conn, ownsSocket: false);
        try {
            var first = await FrameCodec.ReadAsync(stream, ct);
            if (first is null) return;
            switch (first.Type) {
                case FrameType.Spawn:  await orchestrator.HandleLocalSpawnAsync(first, stream, ct); break;
                case FrameType.Attach: await orchestrator.HandleLocalAttachAsync(first.Text, stream, ct); break;
                case FrameType.List:   await orchestrator.HandleLocalListAsync(stream, ct); break;
                default: await FrameCodec.WriteAsync(stream, LocalFrame.Error($"expected Spawn/Attach/List, got {first.Type}"), ct); break;
            }
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            LogConnectionError(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Local control socket listening at {Path}")]
    partial void LogListening(string path);
    [LoggerMessage(Level = LogLevel.Warning, Message = "Local control connection faulted")]
    partial void LogConnectionError(Exception ex);
}
```

(`HandleLocalSpawnAsync` (D5), `HandleLocalAttachAsync` (C2), and `HandleLocalListAsync` (E4) are added later; until then, stub all three on `AgentOrchestrator` returning `Task.CompletedTask` so this compiles.)

- [ ] **Step 2: Register in DI**

In `DaemonRunner.cs`, beside the existing `AddSingleton`/`AddHostedService` block (~`:161`):
```csharp
builder.Services.AddSingleton<LocalControlServer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LocalControlServer>());
```

- [ ] **Step 3: Build + AOT gate**

Run: `dotnet build src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj` → expect success.
Run AOT gate (publish CLI) → expect no `IL30xx/IL20xx`.

- [ ] **Step 4: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/LocalControlServer.cs src/Capacitor.Cli.Daemon/DaemonRunner.cs
git commit -m "feat(daemon): local control socket listener (0600), routed to orchestrator"
```

### Task B3: End-to-end socket smoke test (echo PTY)

**Files:**
- Test: `test/Capacitor.Cli.Tests.Integration/LocalSocketSpawnTests.cs`

- [ ] **Step 1: Write the integration test** — start a real daemon host (or the `LocalControlServer` + a fake orchestrator) on a temp socket dir (`DaemonLockPaths.OverrideDirectoryForTesting`), connect a client `Socket`, send `Spawn("/bin/cat", BorrowedCwd, tmpdir, [], 80, 24)`, write a `Stdin` frame `"hi\n"`, and assert a `Stdout` frame containing `hi` comes back; then send `Detach` and assert the connection closes while the daemon-side agent stays registered. Use `[Test]` + `Skip` on Windows.

- [ ] **Step 2: Run, expect FAIL** until C2/D5 land. (This test is the acceptance gate for Milestones C+D; mark it `[Skip("enable after Task D5")]` initially, then un-skip in D5.)

- [ ] **Step 3: Commit**

```bash
git add test/Capacitor.Cli.Tests.Integration/LocalSocketSpawnTests.cs
git commit -m "test(local-ipc): end-to-end socket spawn/stdin/stdout smoke (skipped until D5)"
```

---

## Milestone C — N-client terminal fan-out

### Task C1: `ITerminalSink` + `LocalSocketSink` (lossless, force-detach on overflow)

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/ITerminalSink.cs`
- Create: `src/Capacitor.Cli.Daemon/Services/LocalSocketSink.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/LocalSocketSinkTests.cs`

**Design (from spec §b.2):** each sink owns a bounded channel. The fan-out enqueue is **non-blocking** (`TryWrite`); on overflow the sink marks itself **detached** (its run loop ends, the client is dropped and must reattach for a fresh replay) — it never drops mid-stream silently and never blocks the shared PTY loop.

- [ ] **Step 1: Failing test**

```csharp
using System.Threading.Channels;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

public class LocalSocketSinkTests {
    [Test]
    public async Task Delivers_chunks_in_order() {
        var got = new List<string>();
        var sink = new LocalSocketSink(capacity: 8, async (b, _) => got.Add(System.Text.Encoding.UTF8.GetString(b)));
        var run = sink.RunAsync(default);
        foreach (var s in new[] { "a", "b", "c" }) sink.TryEnqueue(System.Text.Encoding.UTF8.GetBytes(s));
        sink.Complete(); await run;
        await Assert.That(got).IsEquivalentTo(new[] { "a", "b", "c" });
    }

    [Test]
    public async Task Overflow_marks_detached_and_never_blocks_producer() {
        // writer never drains; tiny capacity → overflow
        var tcs = new TaskCompletionSource();
        var sink = new LocalSocketSink(capacity: 2, async (_, ct) => await tcs.Task.WaitAsync(ct));
        var run = sink.RunAsync(default);
        for (var i = 0; i < 100; i++) sink.TryEnqueue([1]); // must not block
        await Assert.That(sink.Detached).IsTrue();
        tcs.SetResult(); sink.Complete();
    }
}
```

- [ ] **Step 2: Run, expect FAIL.**
  `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/LocalSocketSinkTests/*"`

- [ ] **Step 3: Implement**

`ITerminalSink.cs`:
```csharp
namespace Capacitor.Cli.Daemon.Services;

/// One consumer of an agent's PTY output. The fan-out calls TryEnqueue (non-blocking);
/// each sink drains itself on its own loop so one slow sink can't stall the others.
internal interface ITerminalSink {
    void TryEnqueue(byte[] chunk);
    bool Detached { get; }
}
```

`LocalSocketSink.cs`:
```csharp
using System.Threading.Channels;

namespace Capacitor.Cli.Daemon.Services;

internal sealed class LocalSocketSink : ITerminalSink {
    readonly Channel<byte[]> _ch;
    readonly Func<byte[], CancellationToken, Task> _send;
    public bool Detached { get; private set; }

    public LocalSocketSink(int capacity, Func<byte[], CancellationToken, Task> send) {
        _send = send;
        _ch = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity) {
            FullMode = BoundedChannelFullMode.DropWrite, // we detect overflow ourselves; never silently lose mid-stream
            SingleReader = true,
        });
    }

    public void TryEnqueue(byte[] chunk) {
        if (Detached) return;
        if (!_ch.Writer.TryWrite(chunk)) { Detached = true; _ch.Writer.TryComplete(); } // overflow → drop this client
    }

    public void Complete() => _ch.Writer.TryComplete();

    public async Task RunAsync(CancellationToken ct) {
        try {
            await foreach (var chunk in _ch.Reader.ReadAllAsync(ct)) {
                try { await _send(chunk, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch { Detached = true; _ch.Writer.TryComplete(); return; } // socket write failed → drop client
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
```

> Note: `DropWrite` + our own `Detached` flag guarantees the *producer* never blocks; the moment a write would drop, we instead mark the whole client detached (forcing a clean reattach+replay) rather than silently losing one chunk and corrupting the stream.

- [ ] **Step 4: Run, expect PASS.**

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/ITerminalSink.cs src/Capacitor.Cli.Daemon/Services/LocalSocketSink.cs test/Capacitor.Cli.Tests.Unit/LocalSocketSinkTests.cs
git commit -m "feat(daemon): per-client lossless terminal sink with force-detach on overflow"
```

### Task C2: Fan-out wiring in the read loop

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` — add `List<ITerminalSink> LocalSinks` + a `Lock` to `AgentInstance`; in `ReadAgentOutputAsync` (`:431`), after `agent.OutputBuffer.Append(data)`, fan out to local sinks; keep the existing SignalR call **only when the agent is not private** (Task D3 adds `IsPrivate`). Add `HandleLocalAttachAsync(agentId, stream, ct)`: register a `LocalSocketSink` that writes `Stdout` frames to the stream, send an `Attached` replay of `OutputBuffer`, then run the sink and a stdin/resize read loop until the client detaches/disconnects.

- [ ] **Step 1: Add sink registry to `AgentInstance`** (in `AgentOrchestrator.cs`):
```csharp
public List<ITerminalSink> LocalSinks { get; } = [];
public Lock              SinksLock { get; } = new();
```

- [ ] **Step 2: Fan out in the read loop** — replace the body around `:449-455`:
```csharp
agent.OutputBuffer.Append(data);
ITerminalSink[] sinks;
lock (agent.SinksLock) sinks = agent.LocalSinks.ToArray();
foreach (var sink in sinks) sink.TryEnqueue(data);
if (!agent.IsPrivate)            // Phase 1 local agents are always private → no server stream
    await _server.SendTerminalOutputAsync(agent.Id, Convert.ToBase64String(data), sendCts.Token);
```

- [ ] **Step 3: Implement `HandleLocalAttachAsync`** — full method on `AgentOrchestrator`:
```csharp
public async Task HandleLocalAttachAsync(string agentId, Stream stream, CancellationToken ct) {
    if (!_agents.TryGetValue(agentId, out var agent)) {
        await FrameCodec.WriteAsync(stream, LocalFrame.Error($"no such agent {agentId}"), ct);
        return;
    }
    await AttachClientLoopAsync(agent, stream, ct);
}

// Shared by attach and spawn. Registers a sink, replays the buffer, then pumps client input.
async Task AttachClientLoopAsync(AgentInstance agent, Stream stream, CancellationToken ct) {
    var writeLock = new SemaphoreSlim(1, 1);
    async Task Send(LocalFrame f) { await writeLock.WaitAsync(ct); try { await FrameCodec.WriteAsync(stream, f, ct); } finally { writeLock.Release(); } }

    var sink = new LocalSocketSink(capacity: 4096, (chunk, c) => Send(LocalFrame.Stdout(chunk)));
    lock (agent.SinksLock) agent.LocalSinks.Add(sink);
    await Send(FrameCodec.Attached(agent.Id, agent.OutputBuffer.Snapshot())); // bounded replay before live chunks
    var pump = sink.RunAsync(ct);

    try {
        while (!ct.IsCancellationRequested) {
            var f = await FrameCodec.ReadAsync(stream, ct);
            if (f is null || f.Type == FrameType.Detach) break;
            switch (f.Type) {
                case FrameType.Stdin:  await agent.Process.WriteAsync(f.Bytes); break;
                case FrameType.Resize: ApplyResizeClamp(agent, sink, f.Cols, f.Rows); break;
            }
        }
    } catch (Exception ex) when (ex is EndOfStreamException or IOException or OperationCanceledException) { /* client gone */ }
    finally {
        lock (agent.SinksLock) { agent.LocalSinks.Remove(sink); agent.ClientDims.Remove(sink); }
        sink.Complete();
        await pump.ConfigureAwait(false);
        if (agent.HasExited) await Send(LocalFrame.Exited(agent.Process.ExitCode ?? 0));
    }
}
```

- [ ] **Step 4: Add `TerminalOutputBuffer.Snapshot()`** (in `AgentOrchestrator.cs`, on the `TerminalOutputBuffer` class near `:47`):
```csharp
public byte[] Snapshot() { lock (_chunks) { var ms = new MemoryStream(_totalBytes); foreach (var c in _chunks) ms.Write(c); return ms.ToArray(); } }
```
(Confirm the existing `_chunks`/`_totalBytes` fields; add a `lock (_chunks)` to `Append` too if not already synchronized.)

- [ ] **Step 5: Resize min-clamp helper** (the daemon owns the clamp). Add `public Dictionary<ITerminalSink, Dim> ClientDims { get; } = [];` and `public readonly record struct Dim(ushort Cols, ushort Rows);` to `AgentInstance`, then:
```csharp
// Min-clamp across all attached local clients (tmux semantics): the PTY is sized to the
// smallest cols × rows any attached client reports, so no client's redraw is corrupted.
void ApplyResizeClamp(AgentInstance agent, ITerminalSink sink, ushort cols, ushort rows) {
    lock (agent.SinksLock) {
        agent.ClientDims[sink] = new AgentInstance.Dim(cols, rows);
        if (agent.ClientDims.Count == 0) { agent.Process.Resize(cols, rows); return; }
        var c = agent.ClientDims.Values.Min(d => d.Cols);
        var r = agent.ClientDims.Values.Min(d => d.Rows);
        agent.Process.Resize(c, r);
    }
}
```

- [ ] **Step 6: Build, then run unit suite** → expect green; commit.

```bash
git add src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs
git commit -m "feat(daemon): N-client terminal fan-out + local attach loop + resize min-clamp"
```

---

## Milestone D — Local-launch semantics

### Task D1: Work-location kind + cleanup guard

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (`AgentInstance` + `CleanupAgentAsync` `:888`).
- Test: `test/Capacitor.Cli.Tests.Unit/LocalCleanupGuardTests.cs`

- [ ] **Step 1: Failing test** — construct an `AgentInstance` with `Work = BorrowedCwd` pointing at a temp git repo, call the cleanup, assert the directory and its branch still exist. (Use the existing orchestrator test seams; if `CleanupAgentAsync` is private, add an `internal` test hook mirroring `HandleLaunchAgentForTest`.)

```csharp
[Test]
public async Task Borrowed_cwd_cleanup_does_not_delete_user_dir_or_branch() {
    var dir = Directory.CreateTempSubdirectory("kcap-inplace-");
    // ... init a git repo + branch in `dir`, build an orchestrator with a fake server,
    // register a BorrowedCwd agent whose Worktree.Path == dir.FullName, run cleanup ...
    await Assert.That(Directory.Exists(dir.FullName)).IsTrue();
    // and the branch still exists (git branch --list)
}
```

- [ ] **Step 2: Run, expect FAIL** (cleanup currently deletes it).

- [ ] **Step 3: Add `Work` to `AgentInstance` + guard cleanup**

On `AgentInstance`: `public WorkLocation Work { get; init; } = WorkLocation.OwnedWorktree;`

In `CleanupAgentAsync` (`:900`), wrap the removal:
```csharp
if (agent.Work == WorkLocation.OwnedWorktree) {
    try { await WorktreeManager.RemoveAsync(agent.Worktree); } catch (Exception ex) { LogCleanupStepFailed(ex, "removing worktree", agentId); }
}
// BorrowedCwd: never remove the user's directory or branch.
```

- [ ] **Step 4: Run, expect PASS.**

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs test/Capacitor.Cli.Tests.Unit/LocalCleanupGuardTests.cs
git commit -m "feat(daemon): owned-vs-borrowed work location; never remove a borrowed cwd"
```

### Task D2: Borrowed-cwd `Prepare` skip

**Files:**
- Modify: `IHostedAgentLauncher.cs` (`LauncherContext` gains `WorkLocation Work`), `ClaudeLauncher.cs:21`, `CodexLauncher.cs:18`.
- Test: `test/Capacitor.Cli.Tests.Unit/InPlacePrepareTests.cs`

- [ ] **Step 1: Failing test** — call `ClaudeLauncher.Prepare(ctx)` with `Work = BorrowedCwd` and `Worktree.Path` = temp dir; assert no `.mcp.json` / `.claude/settings.local.json` were created in it.

- [ ] **Step 2: Run, expect FAIL.**

- [ ] **Step 3: Implement** — add `Work` to `LauncherContext`; at the top of each `Prepare`, early-return the repo-mutating work for borrowed cwd:
```csharp
public void Prepare(LauncherContext ctx) {
    if (ctx.Work == WorkLocation.BorrowedCwd) return; // user's own repo: already trusted/configured; touch nothing
    // ... existing worktree-mirroring logic unchanged ...
}
```
For `CodexLauncher`, keep **only** the hooks preflight (read-only check) for borrowed cwd; skip overlay + trust writes:
```csharp
public void Prepare(LauncherContext ctx) {
    if (ctx.Work == WorkLocation.OwnedWorktree) { /* existing overlay */ }
    RunHooksPreflight(ctx);   // read-only; throws CodexHooksNotInstalledException if missing
    if (ctx.Work == WorkLocation.OwnedWorktree) { /* existing trust write */ }
}
```

- [ ] **Step 4: Run, expect PASS.** Commit:
```bash
git add src/Capacitor.Cli.Daemon/Services/IHostedAgentLauncher.cs src/Capacitor.Cli.Daemon/Services/ClaudeLauncher.cs src/Capacitor.Cli.Daemon/Services/CodexLauncher.cs test/Capacitor.Cli.Tests.Unit/InPlacePrepareTests.cs
git commit -m "feat(daemon): borrowed-cwd launches skip repo-mutating Prepare steps"
```

### Task D3: `PrivateLocal` deny-all server guard

**Files:**
- Modify: `AgentOrchestrator.cs` — add `bool IsPrivate` to `AgentInstance`; route every per-agent `_server.*` call for a private agent through a no-op.
- Test: `test/Capacitor.Cli.Tests.Unit/PrivateLocalNoServerCallsTests.cs`

- [ ] **Step 1: Failing test** — a strict mock `ServerConnection` (or its interface seam) whose every per-agent method throws `Assert.Fail`. Drive a private agent through launch→exit→cleanup; assert no method fired.
  > If `AgentOrchestrator` takes a concrete `ServerConnection`, extract an `IAgentServer` interface for the per-agent calls (`AgentRegisteredAsync`, `AppendAgentRunEventAsync`, `DaemonUpdateRepoPaths`, `LaunchFailedAsync`, `AgentStatusChangedAsync`, `EndAgentSessionAsync`, `AgentUnregisteredAsync`) in this task so it can be mocked. This refactor is part of the task.

- [ ] **Step 2: Run, expect FAIL.**

- [ ] **Step 3: Implement** — the simplest correct form: a private agent skips the calls at each site, e.g.
```csharp
if (!agent.IsPrivate) await _server.AgentRegisteredAsync(...);
if (!agent.IsPrivate) _ = _server.AppendAgentRunEventAsync(...);
```
Apply at every site listed in the spec (`:327,:329,:334,:512,:516,:520,:534`, reconnect `:811`, heartbeat `:862`). Prefer a single private-aware wrapper (`AgentServerCalls` helper that no-ops when `IsPrivate`) so a future call can't forget the guard.

- [ ] **Step 4: Run, expect PASS.** Commit:
```bash
git add src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs test/Capacitor.Cli.Tests.Unit/PrivateLocalNoServerCallsTests.cs
git commit -m "feat(daemon): PrivateLocal agents make no per-agent server calls (deny-all + strict-mock test)"
```

### Task D4: Conditional launch env

**Files:**
- Modify: `AgentOrchestrator.cs` (`:294-309`) and `UnixPtyProcess.Spawn` (`:54-66`).
- Test: extend `PrivateLocalNoServerCallsTests.cs` with an env-assertion test (capture the env dict passed to a fake `IPtyProcessFactory`).

- [ ] **Step 1: Failing test** — fake `IPtyProcessFactory` records `extraEnv`; assert for a private local launch: `KCAP_URL` present, `KCAP_AGENT_ID` present, `KCAP_RENDERED_AGENT` **absent**, `KCAP_DAEMON_URL` **absent**; and that `ANTHROPIC_API_KEY` is **not** scrubbed (local-interactive keeps user auth).

- [ ] **Step 2: Run, expect FAIL.**

- [ ] **Step 3: Implement** — build the env conditionally:
```csharp
var env = new Dictionary<string, string> { ["KCAP_AGENT_ID"] = agentId };
if (!string.IsNullOrEmpty(_config.ServerUrl)) env["KCAP_URL"] = _config.ServerUrl;
if (!isLocalInteractive) {                       // headless hosted only
    env["KCAP_RENDERED_AGENT"] = "1";
    if (_permissionBridge.BaseUrl is { } u) env["KCAP_DAEMON_URL"] = u;
}
```
Thread `isLocalInteractive` from the spawn path (Task D5). In `UnixPtyProcess.Spawn`, gate the `unsetenv("ANTHROPIC_API_KEY")` on a `scrubProviderKeys` parameter (true for hosted, false for local-interactive).

- [ ] **Step 4: Run, expect PASS.** Commit:
```bash
git commit -am "feat(daemon): conditional hook env for local-interactive launches (native perms, keep auth)"
```

### Task D5: `HandleLocalSpawnAsync` (the local-launch entry)

**Files:**
- Modify: `AgentOrchestrator.cs` — new public method invoked by `LocalControlServer`.

- [ ] **Step 1: Implement** — decode the `Spawn` frame, build a `LauncherContext` with `Work` + passthrough args, run `Prepare`, `forkpty` via `_ptyFactory.Spawn` with the conditional env, create the `AgentInstance { IsPrivate = true, Work = ... }`, start `ReadAgentOutputAsync`, then call `AttachClientLoopAsync(agent, stream, ct)` (Task C2) so the spawning client is immediately attached. Mirror `HandleLaunchAgent` but: no server calls, passthrough `BuildArgs`, `isLocalInteractive: true`.

```csharp
public async Task HandleLocalSpawnAsync(LocalFrame spawn, Stream stream, CancellationToken ct) {
    var (vendor, work, cwd, args, cols, rows) = FrameCodec.Spawn(spawn);
    if (!_launchers.TryGetValue(vendor, out var launcher)) {
        await FrameCodec.WriteAsync(stream, LocalFrame.Error($"unknown vendor {vendor}"), ct); return;
    }
    var agentId = Guid.NewGuid().ToString("N");
    // OwnedWorktree: reuse the exact worktree-creation call HandleLaunchAgent already makes
    // (via WorktreeManager). BorrowedCwd: a new WorktreeInfo.Borrowed(cwd) factory whose
    // Work=BorrowedCwd guarantees Task D1's cleanup guard never removes it.
    var worktree = work == WorkLocation.OwnedWorktree
        ? await CreateOwnedWorktreeAsync(cwd, agentId)               // extract from HandleLaunchAgent's existing creation
        : WorktreeInfo.Borrowed(cwd);
    var ctx = new LauncherContext(agentId, cwd, worktree, Prompt: null, Model: "", Effort: null,
                                  Tools: null, IsReview: false, Review: null, ReviewLaunch: null) { Work = work };
    try { launcher.Prepare(ctx); } catch (Exception ex) { await FrameCodec.WriteAsync(stream, LocalFrame.Error(ex.Message), ct); return; }
    var built = launcher.BuildPassthrough(ctx, args);                // Task E5
    var env   = BuildLaunchEnv(agentId, isLocalInteractive: true);   // Task D4
    var proc  = _ptyFactory.Spawn(launcher.CliPath, built.Args, cwd, env, cols, rows, scrubProviderKeys: false);
    var agent = new AgentInstance(agentId, null, "", null, cwd, vendor, proc, worktree, new CancellationTokenSource()) {
        IsPrivate = true, Work = work,
    };
    _agents[agentId] = agent;
    _ = ReadAgentOutputAsync(agent);
    await AttachClientLoopAsync(agent, stream, ct);
}
```
> Add `WorktreeInfo.Borrowed(cwd)` (a borrowed-cwd `WorktreeInfo` with `IsStandalone = false` and a sentinel that `RemoveAsync` is never called on — guaranteed by Task D1's `Work` guard). Add `IPtyProcessFactory.Spawn` overload params `(cols, rows, scrubProviderKeys)` (default to current behavior for the server path).

- [ ] **Step 2: Un-skip the B3 integration test; run it.**
  Run: `dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj -- --treenode-filter "/*/*/LocalSocketSpawnTests/*"`
  Expected: PASS (spawn `/bin/cat`, echo round-trips, detach leaves it running).

- [ ] **Step 3: AOT gate + commit**

```bash
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'   # expect none
git commit -am "feat(daemon): local spawn entry — PrivateLocal, passthrough, attach spawning client"
```

---

## Milestone E — CLI client

### Task E1: termios raw mode

**Files:**
- Create: `src/Capacitor.Cli/Local/TerminalRawMode.cs`

- [ ] **Step 1: Implement** (P/Invoke mirroring `UnixPtyInterop` style; AOT-safe `LibraryImport`):
```csharp
using System.Runtime.InteropServices;
namespace Capacitor.Cli.Local;

internal static partial class TerminalRawMode {
    [LibraryImport("libc", SetLastError = true)] [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static partial int tcgetattr(int fd, out Termios t);
    [LibraryImport("libc", SetLastError = true)] [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static partial int tcsetattr(int fd, int optionalActions, ref Termios t);

    [StructLayout(LayoutKind.Sequential)]
    public struct Termios { public uint c_iflag, c_oflag, c_cflag, c_lflag; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)] public byte[] c_cc; public uint c_ispeed, c_ospeed; }
    // NOTE: termios layout differs Linux vs macOS; the implementer MUST verify field offsets
    // per-platform (use cfmakeraw if available to avoid hand-flag math).

    const int STDIN = 0, TCSANOW = 0;
    const uint ICANON = 0x2, ECHO = 0x8, ISIG = 0x1, IEXTEN = 0x400; // verify per-platform

    /// Returns a token whose Dispose restores the original mode. Idempotent + exception-safe.
    public static IDisposable Enable() {
        if (tcgetattr(STDIN, out var orig) != 0) return new Noop();
        var raw = orig;
        raw.c_lflag &= ~(ICANON | ECHO | ISIG | IEXTEN);
        tcsetattr(STDIN, TCSANOW, ref raw);
        return new Restore(orig);
    }
    sealed class Restore(Termios orig) : IDisposable { public void Dispose() { var t = orig; tcsetattr(STDIN, TCSANOW, ref t); } }
    sealed class Noop : IDisposable { public void Dispose() { } }
}
```
> Implementer task: prefer linking `cfmakeraw` if present; otherwise verify `Termios` layout + flag constants against `<termios.h>` for both Linux and macOS. Add a manual checklist note in the PR.

- [ ] **Step 2: Build** → success. Commit:
```bash
git add src/Capacitor.Cli/Local/TerminalRawMode.cs
git commit -m "feat(cli): termios raw-mode enable/restore (P/Invoke)"
```

### Task E2: `RunAgentArgs` parsing

**Files:**
- Create: `src/Capacitor.Cli.Core/RunAgentArgs.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/RunAgentArgsTests.cs`

- [ ] **Step 1: Failing test**
```csharp
using Capacitor.Cli.Core;
namespace Capacitor.Cli.Tests.Unit;
public class RunAgentArgsTests {
    [Test] public async Task Splits_kcap_flags_from_passthrough_at_double_dash() {
        var a = RunAgentArgs.Parse(["claude", "--worktree", "--name", "dev", "--", "--model", "opus", "fix"]);
        await Assert.That(a.Vendor).IsEqualTo("claude");
        await Assert.That(a.Worktree).IsTrue();
        await Assert.That(a.DaemonName).IsEqualTo("dev");
        await Assert.That(a.Passthrough).IsEquivalentTo(new[] { "--model", "opus", "fix" });
    }
    [Test] public async Task Default_is_in_place_no_passthrough() {
        var a = RunAgentArgs.Parse(["codex"]);
        await Assert.That(a.Worktree).IsFalse();
        await Assert.That(a.Passthrough).IsEmpty();
    }
    [Test] public async Task Missing_vendor_is_an_error() {
        await Assert.That(RunAgentArgs.Parse([]).Error).IsNotNull();
    }
}
```

- [ ] **Step 2: Run, expect FAIL.**

- [ ] **Step 3: Implement**
```csharp
namespace Capacitor.Cli.Core;
public sealed record RunAgentArgs {
    public string Vendor = ""; public bool Worktree; public string? DaemonName; public bool Detached;
    public string[] Passthrough = []; public string? Error;
    public static RunAgentArgs Parse(string[] args) {
        var r = new RunAgentArgs();
        if (args.Length == 0) { r.Error = "usage: kcap run-agent <vendor> [--worktree] [--name <id>] [--detached] [-- <agent args>]"; return r; }
        var dash = Array.IndexOf(args, "--");
        var kcap = dash < 0 ? args : args[..dash];
        r.Passthrough = dash < 0 ? [] : args[(dash + 1)..];
        r.Vendor = kcap[0];
        for (var i = 1; i < kcap.Length; i++) switch (kcap[i]) {
            case "--worktree": r.Worktree = true; break;
            case "--detached": r.Detached = true; break;
            case "--name": r.DaemonName = i + 1 < kcap.Length ? kcap[++i] : null; break;
            case "--share": r.Error = "--share is Phase 2 and not yet supported"; break;
            default: r.Error = $"unknown flag {kcap[i]} (did you mean to put it after `--`?)"; break;
        }
        return r;
    }
}
```

- [ ] **Step 4: Run, expect PASS.** Commit:
```bash
git add src/Capacitor.Cli.Core/RunAgentArgs.cs test/Capacitor.Cli.Tests.Unit/RunAgentArgsTests.cs
git commit -m "feat(cli): run-agent arg parsing (-- boundary, kcap flags)"
```

### Task E3: `LocalAgentClient`

**Files:**
- Create: `src/Capacitor.Cli/Local/LocalAgentClient.cs`

- [ ] **Step 1: Implement** — connect to the socket, send the opening `Spawn` (or `Attach`) frame, run two pumps: (a) stdout-frame → `Console.OpenStandardOutput()`, (b) raw stdin → scan for the detach prefix, else `Stdin` frame. Install a `PosixSignalRegistration` for `SIGWINCH` that sends a `Resize` frame with the current `Console.WindowWidth/Height`. Wrap the whole run in `using (TerminalRawMode.Enable())`. On `Exited`/EOF, return the exit code.

```csharp
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Capacitor.Cli.Core.LocalIpc;
namespace Capacitor.Cli.Local;

internal sealed class LocalAgentClient {
    // Detach prefix: Ctrl-Q (0x11) then 'd'. Intercepted before forwarding.
    static readonly byte[] DetachPrefix = [0x11];

    public static async Task<int> RunAsync(string socketPath, LocalFrame opening, CancellationToken ct) {
        using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await sock.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
        using var stream = new NetworkStream(sock, ownsSocket: false);
        await FrameCodec.WriteAsync(stream, opening, ct);

        using var raw = TerminalRawMode.Enable();
        using var _ = PosixSignalRegistration.Create((PosixSignal)28 /* SIGWINCH */, _ =>
            _ = FrameCodec.WriteAsync(stream, LocalFrame.Resize((ushort)Console.WindowWidth, (ushort)Console.WindowHeight), ct));

        var stdout = Console.OpenStandardOutput();
        var outPump = Task.Run(async () => {
            while (!ct.IsCancellationRequested) {
                var f = await FrameCodec.ReadAsync(stream, ct);
                if (f is null) return 0;
                switch (f.Type) {
                    case FrameType.Stdout: await stdout.WriteAsync(f.Bytes, ct); await stdout.FlushAsync(ct); break;
                    case FrameType.Attached: var (_, snap) = FrameCodec.Attached(f); await stdout.WriteAsync(snap, ct); await stdout.FlushAsync(ct);
                        await FrameCodec.WriteAsync(stream, LocalFrame.Resize((ushort)Console.WindowWidth, (ushort)Console.WindowHeight), ct); break; // nudge repaint
                    case FrameType.Exited: return f.ExitCode;
                    case FrameType.Error: await Console.Error.WriteLineAsync($"\r\n[kcap] {f.Text}"); return 1;
                }
            }
            return 0;
        }, ct);

        var stdin = Console.OpenStandardInput();
        var buf = new byte[4096];
        try {
            while (!ct.IsCancellationRequested && !outPump.IsCompleted) {
                var n = await stdin.ReadAsync(buf, ct);
                if (n == 0) break;
                if (n >= DetachPrefix.Length && buf[0] == DetachPrefix[0]) { // simplistic; real impl tracks prefix state across reads
                    await FrameCodec.WriteAsync(stream, LocalFrame.Detach(), ct); break;
                }
                await FrameCodec.WriteAsync(stream, LocalFrame.Stdin(buf[..n]), ct);
            }
        } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        return await outPump;
    }
}
```
> Implementer task: the detach-prefix scan must be a tiny state machine across reads (prefix byte `0x11` then `d` within the next read), matching the `FrameCodecTests`-style "prefix split across reads" unit test — add that unit test for the scanner extracted as a pure function `DetachScanner.Feed(byte) -> (forward, detach)`.

- [ ] **Step 2: Build** → success. Commit:
```bash
git add src/Capacitor.Cli/Local/LocalAgentClient.cs
git commit -m "feat(cli): local agent client — pumps, SIGWINCH resize, detach, replay repaint"
```

### Task E4: `run-agent` / `attach` / `ls` dispatch

**Files:**
- Create: `src/Capacitor.Cli/Commands/RunAgentCommand.cs`
- Modify: `src/Capacitor.Cli/Program.cs` (add `case "run-agent":`, `case "attach":`, `case "ls":` in the main switch; do **not** add them to `offlineCommands` — they need a configured server-backed daemon).

- [ ] **Step 1: Implement `RunAgentCommand.HandleAsync(args)`** — parse via `RunAgentArgs`; resolve daemon name (`DaemonNameResolver`); **ensure the daemon is running** (reuse `DaemonCommands` start path; if no live daemon, start one detached and poll for `LocalSocketPaths.Socket(name)` to appear, ~5s timeout); build the `Spawn` frame with cwd = `Environment.CurrentDirectory`, work = `--worktree ? OwnedWorktree : BorrowedCwd`, passthrough args, initial `Console.WindowWidth/Height`; call `LocalAgentClient.RunAsync(LocalSocketPaths.Socket(name), spawnFrame, ct)` and return its exit code.
  - `AttachAsync(args)` resolves the daemon + sends an `Attach` frame (agentId = `args[0]`) instead of `Spawn`.
  - `ListAsync(args)` connects, sends a `List` frame, reads one `AgentList` text frame, and prints its tab-separated `id\tstatus\tcwd` lines as an aligned table (no live attach; closes after printing).

- [ ] **Step 1b: Daemon side of `ls` — implement `HandleLocalListAsync` on `AgentOrchestrator`:**
```csharp
public async Task HandleLocalListAsync(Stream stream, CancellationToken ct) {
    var lines = _agents.Values.Select(a => $"{a.Id}\t{a.Status}\t{a.RepoPath}");
    await FrameCodec.WriteAsync(stream, new LocalFrame(FrameType.AgentList) { Text = string.Join('\n', lines) }, ct);
}
```

- [ ] **Step 2: Wire dispatch in `Program.cs`:**
```csharp
case "run-agent": return await RunAgentCommand.HandleAsync(args[1..]);
case "attach":    return await RunAgentCommand.AttachAsync(args[1..]);
case "ls":        return await RunAgentCommand.ListAsync(args[1..]);
```

- [ ] **Step 3: Manual smoke** (no automated TTY test):
```
# terminal 1
dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- run-agent claude -- --model sonnet
# type, see output; press Ctrl-Q then d to detach
dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- ls           # shows the running agent
dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- attach <id>  # reattach, screen repaints
```
Expected: interactive session works, Ctrl-C reaches Claude, detach leaves it running, attach repaints.

- [ ] **Step 4: AOT gate + commit**
```bash
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'   # expect none
git add src/Capacitor.Cli/Commands/RunAgentCommand.cs src/Capacitor.Cli/Program.cs
git commit -m "feat(cli): run-agent / attach / ls commands + ensure-daemon"
```

### Task E5: Launcher-agnostic passthrough `BuildArgs`

**Files:**
- Modify: `IHostedAgentLauncher.cs` (add `LaunchArgs BuildPassthrough(LauncherContext ctx, IReadOnlyList<string> userArgs)`), `ClaudeLauncher.cs`, `CodexLauncher.cs`.
- Test: `test/Capacitor.Cli.Tests.Unit/PassthroughBuildArgsTests.cs`

- [ ] **Step 1: Failing tests**
```csharp
[Test] public async Task Claude_passthrough_is_verbatim() {
    var a = new ClaudeLauncher(Cfg(), Log()).BuildPassthrough(Ctx(WorkLocation.BorrowedCwd, "/r"), ["--model", "opus", "hi"]);
    await Assert.That(a.Args).IsEquivalentTo(new[] { "--model", "opus", "hi" });
}
[Test] public async Task Codex_injects_mandatory_then_appends_user_args() {
    var a = new CodexLauncher(Cfg(), Log()).BuildPassthrough(Ctx(WorkLocation.BorrowedCwd, "/r"), ["-m", "gpt"]);
    await Assert.That(a.Args).Contains("--cd"); await Assert.That(a.Args).Contains("--no-alt-screen");
    await Assert.That(a.Args[^2]).IsEqualTo("-m"); await Assert.That(a.Args[^1]).IsEqualTo("gpt");
}
[Test] public async Task Codex_rejects_user_duplicate_of_mandatory_flag() {
    await Assert.That(() => new CodexLauncher(Cfg(), Log()).BuildPassthrough(Ctx(WorkLocation.BorrowedCwd, "/r"), ["--cd", "/elsewhere"]))
        .Throws<ArgumentException>();
}
```

- [ ] **Step 2: Run, expect FAIL.**

- [ ] **Step 3: Implement**
- Claude: `BuildPassthrough(ctx, userArgs) => new([.. userArgs], McpConfigPath: null);`
- Codex:
```csharp
public LaunchArgs BuildPassthrough(LauncherContext ctx, IReadOnlyList<string> userArgs) {
    string[] mandatory = ["--cd", "--no-alt-screen"];
    foreach (var m in mandatory)
        if (userArgs.Contains(m)) throw new ArgumentException($"{m} is set by kcap and cannot be overridden");
    var args = new List<string> { "--cd", ctx.Worktree.Path, "--sandbox", "workspace-write", "--ask-for-approval", "on-request", "--no-alt-screen" };
    args.AddRange(userArgs);
    return new([.. args], McpConfigPath: null);
}
```

- [ ] **Step 4: Run, expect PASS.** Commit:
```bash
git add src/Capacitor.Cli.Daemon/Services/IHostedAgentLauncher.cs src/Capacitor.Cli.Daemon/Services/ClaudeLauncher.cs src/Capacitor.Cli.Daemon/Services/CodexLauncher.cs test/Capacitor.Cli.Tests.Unit/PassthroughBuildArgsTests.cs
git commit -m "feat(daemon): launcher-agnostic passthrough BuildArgs (Codex mandatory-flag enforcement)"
```

---

## Milestone F — Docs & final verification

### Task F1: README

**Files:**
- Modify: `README.md` — add `run-agent`/`attach`/`ls` to `## Getting started` and a per-command section under `## CLI commands`, covering `--worktree` (default in-place), the `--` boundary, the detach key (Ctrl-Q d), and that it's local-only in this release (no web sharing yet).

- [ ] **Step 1: Write the docs. Step 2: Commit.**
```bash
git add README.md
git commit -m "docs: document run-agent / attach / ls (local terminal attach, Phase 1)"
```

### Task F2: Full suite + AOT gate

- [ ] **Step 1: Full unit + integration suites green**
```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj
dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj
```
- [ ] **Step 2: AOT publish clean**
```bash
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'   # expect no output
```
- [ ] **Step 3: macOS — re-sign the AOT binary if copied** (`codesign --force --sign - <binary>`).

---

## Notes for the implementer

- **Windows:** the socket uses `UnixDomainSocketEndPoint`, which works on Win10+; `File.SetUnixFileMode` is a no-op there (guarded by `OperatingSystem.IsWindows()`). `TerminalRawMode` is Unix-only — Phase 1 targets Unix; gate `run-agent` with a clear "not supported on Windows yet" message if `OperatingSystem.IsWindows()`.
- **Phase 2 (out of scope):** `kcap share` / `--share`, the daemon→server announce, `PrivateLocal`→`Shared` transition with one-time replay, session tag-and-link, and web-client resize aggregation. The `IsPrivate`/`Work`/sink abstractions added here are the seams Phase 2 builds on.
- **Detach key** default is Ctrl-Q then `d`; make it overridable later via config — not required for Phase 1.
