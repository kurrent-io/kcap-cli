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
NONCE = "nonce-7f3a91c2"
a = Agent("mcp")
a.call("initialize", init, 60)
new = a.call("session/new", {"cwd": WS, "mcpServers": [{"name": "kcap-probe", "command": sys.executable, "args": ["-I", os.path.join(os.path.dirname(os.path.abspath(__file__)), "mcp_echo.py"), NONCE], "env": []}], "_meta": {"yoloMode": True}}, 90)
sid = new.get("result", {}).get("sessionId")
r = a.call("session/prompt", {"sessionId": sid, "prompt": [{"type": "text", "text": "Call the MCP tool kcap_probe_echo and reply with exactly what it returned."}]}, 240)
print("mcp-prompt", json.dumps(r.get("result", {}).get("_meta", {}).get("modelId")))
sc = a.call("session/set_config_option", {"sessionId": sid, "configId": "model", "value": "grok-4.6"}, 60)
print("set", json.dumps(sc)[:400])
r2 = a.call("session/prompt", {"sessionId": sid, "prompt": [{"type": "text", "text": "Reply with the single word ok."}]}, 240)
print("after-set modelId", json.dumps(r2.get("result", {}).get("_meta", {}).get("modelId")), json.dumps(list((r2.get("result", {}).get("_meta", {}).get("usage") or {}).get("modelUsage", {}).keys())))
a.p.kill()
