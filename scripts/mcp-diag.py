"""Focused diagnostic: print the full envelope for a few tool calls.

The smoke reporter truncates details; when a call fails this prints the whole structured content
so the diagnosis is based on the real error text.
"""

import json
import os
import subprocess
import sys
import threading
import time
import uuid

for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from localconfig import local_config
HOST = os.path.join(ROOT, "src", "KompasMcp.Host", "bin", "Debug", "net10.0-windows", "KompasMcp.Host.exe")
CONFIG = local_config(ROOT)


class Client:
    def __init__(self, exe, config):
        args = [exe] + (["--config", config] if config else [])
        self.p = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=subprocess.PIPE, text=True, encoding="utf-8")
        self.n = 0
        self.err = []
        threading.Thread(target=self._pump, daemon=True).start()

    def _pump(self):
        while True:
            line = self.p.stderr.readline()
            if not line:
                return
            self.err.append(line.rstrip())

    def send(self, msg):
        self.p.stdin.write(json.dumps(msg, ensure_ascii=False) + "\n")
        self.p.stdin.flush()

    def read(self, want, timeout=240):
        deadline = time.time() + timeout
        while time.time() < deadline:
            line = self.p.stdout.readline()
            if not line:
                if self.p.poll() is not None:
                    raise RuntimeError("host exited:\n" + "\n".join(self.err[-30:]))
                time.sleep(0.02)
                continue
            line = line.strip()
            if not line:
                continue
            msg = json.loads(line)
            if msg.get("id") == want:
                return msg
        raise TimeoutError(f"id={want}")

    def call(self, method, params=None, timeout=240):
        self.n += 1
        msg = {"jsonrpc": "2.0", "id": self.n, "method": method}
        if params is not None:
            msg["params"] = params
        self.send(msg)
        reply = self.read(self.n, timeout)
        if "error" in reply:
            raise RuntimeError(f"{method}: {reply['error']}")
        return reply["result"]

    def tool(self, name, arguments, timeout=240):
        result = self.call("tools/call", {"name": name, "arguments": arguments}, timeout)
        return result.get("structuredContent"), result

    def close(self):
        try:
            self.p.stdin.close()
            self.p.wait(timeout=10)
        except Exception:
            self.p.kill()


def main():
    c = Client(HOST, CONFIG)
    try:
        c.call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                              "clientInfo": {"name": "diag", "version": "0"}}, timeout=60)
        c.notify = None  # noqa - keep the attribute list explicit below
        c.send({"jsonrpc": "2.0", "method": "notifications/initialized"})

        tools = c.call("tools/list", {}).get("tools", [])
        for tool in tools:
            schema = tool.get("inputSchema") or {}
            if schema.get("additionalProperties") is not False or (schema.get("properties") and "required" not in schema):
                print("СХЕМА БЕЗ ЗАКРЫТИЯ:", tool["name"], json.dumps(schema, ensure_ascii=False)[:300])

        for name, args in [
            ("kompas_health", {"detail": "minimal"}),
            ("kompas_capabilities", {}),
            ("kompas_connect", {"mode": "launch", "make_visible": False,
                                "operation_id": str(uuid.uuid4())}),
        ]:
            try:
                env, raw = c.tool(name, args)
            except Exception as ex:
                print(f"\n### {name} → ИСКЛЮЧЕНИЕ {ex}")
                continue
            print(f"\n### {name}")
            print(json.dumps(env, ensure_ascii=False, indent=1)[:2000])
            if env is None:
                print("raw:", json.dumps(raw, ensure_ascii=False)[:1500])

        print("\n### stderr Host (хвост):")
        print("\n".join(c.err[-25:]))
    finally:
        c.close()


if __name__ == "__main__":
    main()
