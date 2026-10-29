import json, sys, os, time
TAG, OUT = sys.argv[1], sys.argv[2]
open(os.path.join(OUT, f"started-{TAG}-{os.getpid()}"), "w").write(json.dumps({"argv": sys.argv, "GROK_SESSION_ID": os.environ.get("GROK_SESSION_ID"), "t": time.time()}))
for line in sys.stdin:
    try: m = json.loads(line)
    except Exception: continue
    mid, meth = m.get("id"), m.get("method")
    if mid is None: continue
    if meth == "initialize":
        r = {"protocolVersion": m["params"].get("protocolVersion", "2024-11-05"), "capabilities": {"tools": {}}, "serverInfo": {"name": TAG, "version": "1"}}
    elif meth == "tools/list":
        r = {"tools": [{"name": f"echo_{TAG.replace('-','_')}", "description": "probe", "inputSchema": {"type": "object", "properties": {}}}]}
    else:
        r = {}
    sys.stdout.write(json.dumps({"jsonrpc": "2.0", "id": mid, "result": r}) + "\n"); sys.stdout.flush()
