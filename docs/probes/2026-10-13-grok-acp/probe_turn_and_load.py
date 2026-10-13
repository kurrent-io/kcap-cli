import json, subprocess, sys, threading, time, queue, os
WS, OUT = sys.argv[1], sys.argv[2]
log = open(os.path.join(OUT, "frames.jsonl"), "a")

class Agent:
    def __init__(self, tag):
        self.tag = tag
        self.p = subprocess.Popen(["grok", "agent", "--no-leader", "stdio"], cwd=WS,
                                  stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=open(os.path.join(OUT, f"stderr-{tag}.txt"), "w"), text=True, bufsize=1)
        self.q, self.nid = queue.Queue(), 0
        threading.Thread(target=self.read, daemon=True).start()
    def read(self):
        for line in self.p.stdout:
            line = line.strip()
            if not line: continue
            log.write(json.dumps({"t": time.time(), "dir": "in", "tag": self.tag, "frame": json.loads(line) if line.startswith("{") else line}) + "\n"); log.flush()
            try: self.q.put(json.loads(line))
            except Exception: pass
    def send(self, obj):
        log.write(json.dumps({"t": time.time(), "dir": "out", "tag": self.tag, "frame": obj}) + "\n"); log.flush()
        self.p.stdin.write(json.dumps(obj) + "\n"); self.p.stdin.flush()
    def call(self, method, params, timeout=180, on_request=None):
        self.nid += 1; rid = self.nid
        self.send({"jsonrpc": "2.0", "id": rid, "method": method, "params": params})
        end = time.time() + timeout
        while time.time() < end:
            try: m = self.q.get(timeout=1)
            except queue.Empty: continue
            if m.get("id") == rid and "method" not in m: return m
            if "method" in m and "id" in m and on_request: self.send({"jsonrpc": "2.0", "id": m["id"], "result": on_request(m)})
        return {"timeout": method}

def approve(m):
    opts = m.get("params", {}).get("options", [])
    pick = next((o for o in opts if o.get("kind") == "allow_once"), None)
    return {"outcome": {"outcome": "selected", "optionId": pick["optionId"]}} if pick else {"outcome": {"outcome": "cancelled"}}

init = {"protocolVersion": 1, "clientCapabilities": {"fs": {"readTextFile": False, "writeTextFile": False}, "terminal": False}}
a = Agent("first")
print("init", json.dumps(a.call("initialize", init, 60))[:1500])
new = a.call("session/new", {"cwd": WS, "mcpServers": []}, 60)
print("new", json.dumps(new)[:3000])
sid = new.get("result", {}).get("sessionId")
r = a.call("session/prompt", {"sessionId": sid, "prompt": [{"type": "text", "text": "Create a file named probe.txt in the current directory containing exactly the word hi. Then reply with the single word done."}]}, 240, approve)
print("prompt", json.dumps(r)[:800])
a.p.kill(); a.p.wait()
time.sleep(2)
b = Agent("second")
print("init2", json.dumps(b.call("initialize", init, 60))[:300])
ld = b.call("session/load", {"sessionId": sid, "cwd": WS, "mcpServers": []}, 90)
print("load", json.dumps(ld)[:800])
r2 = b.call("session/prompt", {"sessionId": sid, "prompt": [{"type": "text", "text": "What file did you create? Reply with just its name."}]}, 180, approve)
print("prompt2", json.dumps(r2)[:500])
b.p.kill()
