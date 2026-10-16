"""Dependency-free client for NetworkGuardian's stateless loopback MCP server."""
import argparse
import json
import sys
import time
import urllib.error
import urllib.parse
import urllib.request


class Client:
    def __init__(self, url, timeout=30):
        parsed = urllib.parse.urlsplit(url)
        if (parsed.scheme != "http" or parsed.hostname != "127.0.0.1" or parsed.username
                or parsed.password or parsed.path not in ("/mcp", "/mcp/") or parsed.query or parsed.fragment):
            raise ValueError("Use http://127.0.0.1:<configured-port>/mcp")
        self.url, self.timeout, self.sequence = url, timeout, 0
        # Loopback traffic must not be sent through an environment-configured proxy.
        self.http = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def rpc(self, method, params=None):
        self.sequence += 1
        payload = {"jsonrpc": "2.0", "id": self.sequence, "method": method}
        if params is not None:
            payload["params"] = params
        request = urllib.request.Request(self.url, json.dumps(payload, allow_nan=False).encode("utf-8"),
            {"Content-Type": "application/json", "Accept": "application/json, text/event-stream",
             "MCP-Protocol-Version": "2025-11-25"}, method="POST")
        with self.http.open(request, timeout=self.timeout) as response:
            message = json.load(response)
        if message.get("id") != self.sequence:
            raise RuntimeError("MCP response ID mismatch")
        if "error" in message:
            raise RuntimeError(json.dumps(message["error"], ensure_ascii=False))
        return message["result"]

    def initialize(self):
        return self.rpc("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
            "clientInfo": {"name": "networkguardian-skill", "version": "1.0"}})

    def call(self, name, arguments=None):
        result = self.rpc("tools/call", {"name": name, "arguments": arguments or {}})
        data = result.get("structuredContent")
        if data is None:
            content = result.get("content", [])
            text = "\n".join(item.get("text", "") for item in content if item.get("type") == "text")
            if result.get("isError"):
                raise RuntimeError(text)
            data = json.loads(text)
        if result.get("isError") or data.get("success") is False:
            raise RuntimeError(json.dumps(data, ensure_ascii=False))
        return data


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", default="http://127.0.0.1:18765/mcp")
    parser.add_argument("--timeout", type=float, default=30)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("tools")
    commands.add_parser("status")
    call = commands.add_parser("call")
    call.add_argument("tool")
    arguments = call.add_mutually_exclusive_group()
    arguments.add_argument("--args-json")
    arguments.add_argument("--args-file")
    wait = commands.add_parser("wait")
    wait.add_argument("job_id")
    wait.add_argument("--seconds", type=float, default=60)
    wait.add_argument("--poll", type=float, default=5)
    args = parser.parse_args()
    if not 0 < args.timeout <= 180:
        parser.error("timeout must be within 0..180 seconds")
    client = Client(args.url, args.timeout)
    client.initialize()
    if args.command == "tools":
        output = client.rpc("tools/list")
    elif args.command == "status":
        output = client.call("training_status")
    elif args.command == "call":
        if args.args_file:
            with open(args.args_file, encoding="utf-8-sig") as stream:
                values = json.load(stream)
        else:
            values = json.loads(args.args_json or "{}")
        if not isinstance(values, dict):
            parser.error("arguments must be a JSON object")
        output = client.call(args.tool, values)
    else:
        if not 0 < args.seconds <= 3600 or not 1 <= args.poll <= 60:
            parser.error("seconds must be within 0..3600; poll must be within 1..60")
        deadline = time.monotonic() + args.seconds
        while True:
            output = client.call("training_status", {"jobId": args.job_id})
            if output.get("state") != "running" or time.monotonic() >= deadline:
                break
            time.sleep(min(args.poll, max(0, deadline - time.monotonic())))
    print(json.dumps(output, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, RuntimeError, KeyError) as error:
        print(json.dumps({"success": False, "error": str(error)}, ensure_ascii=False), file=sys.stderr)
        sys.exit(1)
