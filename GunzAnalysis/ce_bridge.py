"""Direct client for the Cheat Engine Lua bridge pipe (no MCP framing).

Usage:  python ce_bridge.py <method> [json-params]
Example: python ce_bridge.py get_thread_list "{\"limit\": 200}"
"""
import json
import struct
import sys
import time

PIPE_NAME = r"\\.\pipe\CE_MCP_Bridge_v99"


def call(method, params=None, timeout=30):
    request = {
        "jsonrpc": "2.0",
        "method": method,
        "params": params or {},
        "id": int(time.time() * 1000),
    }
    payload = json.dumps(request).encode("utf-8")
    with open(PIPE_NAME, "r+b", buffering=0) as pipe:
        pipe.write(struct.pack("<I", len(payload)))
        pipe.write(payload)
        header = pipe.read(4)
        if len(header) < 4:
            raise ConnectionError("Incomplete response header from CE bridge")
        resp_len = struct.unpack("<I", header)[0]
        body = b""
        while len(body) < resp_len:
            chunk = pipe.read(resp_len - len(body))
            if not chunk:
                raise ConnectionError("Pipe closed mid-response")
            body += chunk
    response = json.loads(body.decode("utf-8"))
    if "error" in response:
        raise RuntimeError(str(response["error"]))
    return response.get("result", response)


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    method = sys.argv[1]
    params = json.loads(sys.argv[2]) if len(sys.argv) > 2 else {}
    result = call(method, params)
    if isinstance(result, (dict, list)):
        print(json.dumps(result, indent=2))
    else:
        print(result)
