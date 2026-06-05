#!/usr/bin/env python3
"""Minimal MCP Streamable-HTTP client to exercise the research tools locally.

No dependencies beyond the Python standard library. Start the Functions host
first (see README), then run:

    # Fast path -> returns status "completed" inline
    python3 test-client.py start_research '{"topic": "Contoso"}'

    # Poll path (use a small budget so the workflow outlives the wait):
    #   ResearchWaitBudgetSeconds=2 ResearchSourceDelaySeconds=5 dotnet run
    python3 test-client.py start_research '{"topic": "Contoso"}'   # -> running + workflow_id
    python3 test-client.py get_research_result '{"workflow_id": "<id-from-above>"}'
"""
import json, sys, urllib.request, urllib.error

BASE = "http://localhost:7071/runtime/webhooks/mcp"
session_id = None

def post(payload):
    global session_id
    data = json.dumps(payload).encode()
    headers = {
        "Content-Type": "application/json",
        "Accept": "application/json, text/event-stream",
    }
    if session_id:
        headers["Mcp-Session-Id"] = session_id
    req = urllib.request.Request(BASE, data=data, headers=headers, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            sid = resp.headers.get("Mcp-Session-Id")
            if sid:
                session_id = sid
            ctype = resp.headers.get("Content-Type", "")
            body = resp.read().decode()
            if "text/event-stream" in ctype:
                # parse SSE: lines starting with 'data:'
                for line in body.splitlines():
                    if line.startswith("data:"):
                        return json.loads(line[5:].strip())
                return None
            if body.strip():
                return json.loads(body)
            return None
    except urllib.error.HTTPError as e:
        print(f"HTTP {e.code}: {e.read().decode()[:500]}")
        raise

def rpc(method, params=None, id=None):
    p = {"jsonrpc": "2.0", "method": method}
    if id is not None:
        p["id"] = id
    if params is not None:
        p["params"] = params
    return post(p)

# 1. initialize
init = rpc("initialize", {
    "protocolVersion": "2025-06-18",
    "capabilities": {},
    "clientInfo": {"name": "test-client", "version": "1.0"},
}, id=1)
print("== initialize ==")
print(json.dumps(init, indent=2)[:400])

# 2. initialized notification
rpc("notifications/initialized")

# 3. tools/list
tools = rpc("tools/list", {}, id=2)
print("\n== tools/list ==")
names = [t["name"] for t in tools["result"]["tools"]]
print("tools:", names)

# 4. call the tool given on argv
tool = sys.argv[1] if len(sys.argv) > 1 else "start_research"
args = json.loads(sys.argv[2]) if len(sys.argv) > 2 else {"topic": "Contoso"}
print(f"\n== tools/call {tool} {args} ==")
res = rpc("tools/call", {"name": tool, "arguments": args}, id=3)
print(json.dumps(res, indent=2)[:1500])
