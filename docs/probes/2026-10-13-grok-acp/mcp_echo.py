import json, sys
NONCE = sys.argv[1]
for line in sys.stdin:
    try: m = json.loads(line)
    except Exception: continue
    mid, meth = m.get("id"), m.get("method")
    if mid is None: continue
    if meth == "initialize":
        r = {"protocolVersion": m["params"].get("protocolVersion", "2024-11-05"), "capabilities": {"tools": {}}, "serverInfo": {"name": "kcap-probe", "version": "1"}}
    elif meth == "tools/list":
        r = {"tools": [{"name": "kcap_probe_echo", "description": "Returns the probe token.", "inputSchema": {"type": "object", "properties": {}}}]}
    elif meth == "tools/call":
        r = {"content": [{"type": "text", "text": NONCE}]}
    else:
        r = {}
    sys.stdout.write(json.dumps({"jsonrpc": "2.0", "id": mid, "result": r}) + "\n"); sys.stdout.flush()
