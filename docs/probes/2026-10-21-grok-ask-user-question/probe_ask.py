import json, subprocess, sys, threading, queue, time
cwd = sys.argv[1]
p = subprocess.Popen(["grok", "agent", "--no-leader", "stdio"], cwd=cwd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, bufsize=1)
q = queue.Queue()
threading.Thread(target=lambda: [q.put(l) for l in p.stdout], daemon=True).start()
def write(o): p.stdin.write(json.dumps(o) + "\n"); p.stdin.flush()
def send(i, method, params): write({"jsonrpc": "2.0", "id": i, "method": method, "params": params})
def wait(i, on_other=None, timeout=240):
    end = time.time() + timeout
    while time.time() < end:
        o = json.loads(q.get(timeout=end - time.time()))
        if o.get("id") == i and ("result" in o or "error" in o) and "method" not in o:
            return o
        if on_other: on_other(o)
    raise SystemExit("timeout")
send(1, "initialize", {"protocolVersion": 1, "clientCapabilities": {"fs": {"readTextFile": False, "writeTextFile": False}, "terminal": False}})
print("INIT", json.dumps(wait(1).get("result"))[:1500])
send(2, "session/new", {"cwd": cwd, "mcpServers": [], "_meta": {"yoloMode": False}})
sid = wait(2)["result"]["sessionId"]
answered = {"n": 0}
def other(o):
    m = o.get("method")
    if m and m != "session/update" and not (m == "_x.ai/session_notification"):
        print("REQ", json.dumps(o)[:3000])
    if m == "_x.ai/ask_user_question" and "id" in o and answered["n"] == 0:
        answered["n"] += 1
        print("ANSWERING")
        write({"jsonrpc": "2.0", "id": o["id"], "result": json.loads(sys.argv[2]) if len(sys.argv) > 2 else {"outcome": "cancelled"}})
    elif m == "session/request_permission" and "id" in o:
        write({"jsonrpc": "2.0", "id": o["id"], "result": {"outcome": {"outcome": "selected", "optionId": o["params"]["options"][0]["optionId"]}}})
    elif m == "session/update":
        u = o["params"]["update"]
        if u.get("sessionUpdate") in ("agent_message_chunk",):
            print("MSG", json.dumps(u.get("content", {}).get("text", ""))[:300])
        elif u.get("sessionUpdate") in ("tool_call", "tool_call_update"):
            print("TOOL", u.get("sessionUpdate"), u.get("title"), u.get("status"), json.dumps(u.get("rawOutput") or u.get("content") or "")[:400])
send(3, "session/prompt", {"sessionId": sid, "prompt": [{"type": "text", "text": "Use your ask_user_question tool to ask me exactly one question: 'Which colour?' with two options, 'Red' and 'Blue'. After I answer, reply with one line: you picked <answer>. Do nothing else."}]})
r = wait(3, other)
print("PROMPT RESULT", json.dumps(r)[:500])
p.terminate()
