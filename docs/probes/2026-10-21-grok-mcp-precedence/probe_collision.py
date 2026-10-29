import json, subprocess, sys, threading, queue, os, time
WS, OUT, TAG_SERVER = sys.argv[1], sys.argv[2], sys.argv[3]
env = dict(os.environ); env["KCAP_HOSTED_GROK"] = str(os.getpid())
p = subprocess.Popen(["grok", "agent", "--no-leader", "stdio"], cwd=WS, env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                     stderr=open(os.path.join(OUT, "stderr.txt"), "w"), text=True, bufsize=1)
q, notes, log = queue.Queue(), [], open(os.path.join(OUT, "frames.jsonl"), "w")

def read():
    for line in p.stdout:
        log.write(line); log.flush()
        try: q.put(json.loads(line))
        except Exception: pass
threading.Thread(target=read, daemon=True).start()

def call(i, method, params, timeout=90):
    p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": i, "method": method, "params": params}) + "\n"); p.stdin.flush()
    end = time.time() + timeout
    while time.time() < end:
        try: o = q.get(timeout=1)
        except queue.Empty: continue
        if o.get("id") == i and "method" not in o: return o
        if "method" in o: notes.append(o)
    return {"timeout": method}

def server(name, tag): return {"name": name, "command": sys.executable, "args": ["-I", TAG_SERVER, tag, OUT], "env": []}

call(1, "initialize", {"protocolVersion": 1, "clientCapabilities": {}})
r = call(2, "session/new", {"cwd": WS, "_meta": {"yoloMode": False}, "mcpServers": [
    server("kcap-workitems", "injected-workitems"), server("probe-dup", "injected-dup"), server("probe-unique", "injected-unique")]})
print("sessionId", r.get("result", {}).get("sessionId"))
end = time.time() + 40
while time.time() < end:
    try: notes.append(q.get(timeout=1))
    except queue.Empty: pass
for n in notes:
    if n.get("method") in ("_x.ai/mcp/server_status", "_x.ai/mcp/init_progress", "_x.ai/session/setup"):
        print(json.dumps(n)[:400])
p.terminate()
