"""Размер `tools/list` у собранного Хоста - настоящим запросом по stdio.

  python scripts/tools-list-size.py [--host <KompasMcp.Host.exe>] [--json <out.json>] [--dump <tools.json>]

Печатает итоги и пять крупнейших инструментов. `--json` - итоги и строки по инструментам (так снято
`tools-list-size.json` в доказательствах приёмки), `--dump` - сам массив `tools`, вход для
`scripts/contract-diff.py`.

Мера - символы массива `tools` в `json.dumps(..., ensure_ascii=False)` с разделителями по умолчанию;
`tools_array_compact` - без пробелов, это мера стража `ToolListSizeTests`.
History: docs/decisions/host.md#tools-list-size-guard"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import threading
import time

for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "scripts"))
from localconfig import isolate_config, local_config  # noqa: E402

DEFAULT_HOST = os.path.join(
    ROOT, "src", "KompasMcp.Host", "bin", "x64", "Release", "net10.0-windows", "KompasMcp.Host.exe")

# INVARIANT: host logs go to a scratch directory, never next to the script and never to the shared
# client log: the instrument must not leave files in scripts/ or noise in a client session's log.
LOG_DIR = os.path.join(ROOT, "scratch", "tools-list-size")


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

    def read(self, want, timeout=120):
        deadline = time.time() + timeout
        while time.time() < deadline:
            line = self.p.stdout.readline()
            if not line:
                if self.p.poll() is not None:
                    raise RuntimeError("Хост завершился:\n" + "\n".join(self.err[-30:]))
                time.sleep(0.02)
                continue
            line = line.strip()
            if not line:
                continue
            msg = json.loads(line)
            if msg.get("id") == want:
                return msg
        raise TimeoutError(f"id={want}")

    def call(self, method, params=None, timeout=120):
        self.n += 1
        msg = {"jsonrpc": "2.0", "id": self.n, "method": method}
        if params is not None:
            msg["params"] = params
        self.send(msg)
        reply = self.read(self.n, timeout)
        if "error" in reply:
            raise RuntimeError(f"{method}: {reply['error']}")
        return reply["result"]

    def close(self):
        try:
            self.p.stdin.close()
            self.p.wait(timeout=15)
        except Exception:
            self.p.kill()


def dumps(node, ascii_escape):
    return json.dumps(node, ensure_ascii=ascii_escape)


def description_chars(node):
    """Total length of every `description` string inside a schema node, nested objects included."""
    total = 0
    if isinstance(node, dict):
        for key, value in node.items():
            if key == "description" and isinstance(value, str):
                total += len(value)
            else:
                total += description_chars(value)
    elif isinstance(node, list):
        for item in node:
            total += description_chars(item)
    return total


def measure(tools):
    rows = []
    for tool in tools:
        schema = tool.get("inputSchema") or {}
        defs = schema.get("$defs") or {}
        rows.append({
            "name": tool["name"],
            "total": len(dumps(tool, False)),
            "description": len(tool.get("description") or ""),
            "schema": len(dumps(schema, False)),
            "defs": len(dumps(defs, False)),
            "defs_count": len(defs),
            "field_descriptions": description_chars(schema),
        })

    totals = {
        "tools": len(tools),
        "tools_array": len(dumps(tools, False)),
        "tools_array_ascii": len(dumps(tools, True)),
        "tools_array_compact": len(json.dumps(tools, ensure_ascii=False, separators=(",", ":"))),
        "defs": sum(row["defs"] for row in rows),
        "tool_descriptions": sum(row["description"] for row in rows),
        "field_descriptions": sum(row["field_descriptions"] for row in rows),
        "schemas": sum(row["schema"] for row in rows),
    }
    return totals, rows


def main():
    parser = argparse.ArgumentParser(description="Размер tools/list собранного Хоста.")
    parser.add_argument("--host", default=DEFAULT_HOST, help="KompasMcp.Host.exe (по умолчанию Release x64)")
    parser.add_argument("--json", default=None, help="записать итоги и строки по инструментам")
    parser.add_argument("--dump", default=None, help="записать массив tools - вход contract-diff.py")
    args = parser.parse_args()

    if not os.path.isfile(args.host):
        raise SystemExit(f"нет сборки Хоста: {args.host}")

    os.makedirs(LOG_DIR, exist_ok=True)
    config = isolate_config(local_config(ROOT), LOG_DIR, "catsize")
    client = Client(args.host, config)
    try:
        client.call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                   "clientInfo": {"name": "tools-list-size", "version": "0"}}, timeout=60)
        client.send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        tools = client.call("tools/list", {}).get("tools", [])
    finally:
        client.close()

    totals, rows = measure(tools)
    rows.sort(key=lambda row: -row["total"])

    print(f"host={args.host}")
    for key, value in totals.items():
        print(f"{key:>24} = {value}")
    print("top 5 tools by serialized size:")
    for row in rows[:5]:
        print("  {name:<34} total={total:>7} description={description:>6} defs={defs:>7} "
              "defs_count={defs_count:>2} field_descriptions={field_descriptions:>7}".format(**row))

    if args.dump:
        with open(args.dump, "w", encoding="utf-8") as handle:
            json.dump(tools, handle, ensure_ascii=False)
        print(f"dump={args.dump} bytes={os.path.getsize(args.dump)}")

    if args.json:
        with open(args.json, "w", encoding="utf-8") as handle:
            json.dump({"totals": totals, "tools": rows}, handle, ensure_ascii=False, indent=1)
        print(f"json={args.json}")


if __name__ == "__main__":
    main()
