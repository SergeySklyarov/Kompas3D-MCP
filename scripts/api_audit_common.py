"""Shared helpers of the API compliance audit instruments (work order `API_AUDIT_TOOLS`).

NOT an instrument of its own: no `--self-test`, no output of its own. It exists so that the four
measurements (wire inventory, COM routes, documentation pages, matrix claims) and the generator that
joins them cannot drift apart on the things they must agree about: where the repository root is, how
the audited build is named and hashed, where evidence is written, and how the MCP Host is driven.

INVARIANT: the audited build is named by its FILES and HASHES, never by a bare path. Two builds of
one tree differ in PE bytes while the source commit is the same, so a path alone cannot say what was
measured. Every instrument writes the same `build_identity()` block into its output.
"""

from __future__ import annotations

import datetime
import hashlib
import json
import os
import subprocess
import sys
import threading
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# The audited build is the Release build of the clean commit: the work order audits BEFORE the 0.8.0
# delivery exists, so there is no published package to measure yet.
RELEASE_DIR = os.path.join(ROOT, "src", "KompasMcp.Host", "bin", "x64", "Release", "net10.0-windows")
RELEASE_HOST = os.path.join(RELEASE_DIR, "KompasMcp.Host.exe")
RELEASE_WORKER = os.path.join(ROOT, "src", "KompasMcp.Worker", "bin", "x64", "Release",
                              "net10.0-windows", "KompasMcp.Worker.exe")
MATRIX = os.path.join(ROOT, "coverage", "solid-v24", "matrix.json")
CATALOG = os.path.join(ROOT, "coverage", "solid-v24", "catalog.json")
PROFILES_DIR = os.path.join(ROOT, "coverage", "solid-v24", "release-profiles")
SCHEMAS_DIR = os.path.join(ROOT, "schemas")
ADAPTER_SRC = os.path.join(ROOT, "src", "KompasMcp.Api5Adapter")

for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass


def load_json(path):
    """Project JSON carries a BOM; reading it as plain utf-8 makes the first key unusable."""
    with open(path, encoding="utf-8-sig") as handle:
        return json.load(handle)


def save_json(path, payload, indent=1):
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=indent)
    return path


def file_sha256(path):
    """Hash of a file, or None when it cannot be read.

    None is returned rather than a fabricated digest: a hash that silently stands for a missing file
    is worse than a named absence.
    """
    try:
        with open(path, "rb") as handle:
            return hashlib.sha256(handle.read()).hexdigest()
    except OSError:
        return None


def git_output(*args):
    try:
        done = subprocess.run(["git"] + list(args), cwd=ROOT, capture_output=True, text=True,
                              timeout=30)
    except (OSError, subprocess.SubprocessError):
        return ""
    return done.stdout.strip() if done.returncode == 0 else ""


def source_commit():
    return git_output("rev-parse", "--short", "HEAD") or "nocommit"


def tree_dirty():
    return bool(git_output("status", "--porcelain"))


def machine_zone():
    """Machine zone as an offset, printed next to every date: a date without a zone is not a stamp."""
    offset = datetime.datetime.now().astimezone().utcoffset() or datetime.timedelta(0)
    total = int(offset.total_seconds())
    sign = "+" if total >= 0 else "-"
    total = abs(total)
    return f"{sign}{total // 3600:02d}:{(total % 3600) // 60:02d}"


def stamp():
    """Build stamp of the audit: the moment of THIS run on the machine's clock, with its zone."""
    return (datetime.datetime.now().astimezone().isoformat(timespec="seconds")
            + f", часы машины {machine_zone()}")


def build_identity(host=None, worker=None):
    """What was measured: files and hashes of the audited build, plus the source tree state."""
    host = os.path.abspath(host or RELEASE_HOST)
    worker = os.path.abspath(worker or RELEASE_WORKER)
    return {
        "kind": "release_build",
        "dir": os.path.dirname(host),
        "host_path": host,
        "host_sha256": file_sha256(host),
        "host_dll_sha256": file_sha256(os.path.join(os.path.dirname(host), "KompasMcp.Host.dll")),
        "worker_path": worker,
        "worker_sha256": file_sha256(worker),
        "worker_dll_sha256": file_sha256(os.path.join(os.path.dirname(worker),
                                                      "KompasMcp.Worker.dll")),
        "adapter_sha256": file_sha256(os.path.join(os.path.dirname(worker),
                                                   "KompasMcp.Api5Adapter.dll")),
        "source_commit": source_commit(),
        "source_tree_dirty": tree_dirty(),
    }


def evidence_dir(name=None):
    """Directory of THIS audit's evidence: `docs/acceptance/evidence/<date>-<name>-<commit>/`.

    Evidence never goes to `scratch/` (AGENTS.md): `scratch/` is cleaned, and a cleaned evidence
    directory reads as "the measurement was never made".
    """
    day = datetime.datetime.now().strftime("%Y%m%d")
    name = name or "api-audit"
    path = os.path.join(ROOT, "docs", "acceptance", "evidence", f"{day}-{name}-{source_commit()}")
    os.makedirs(path, exist_ok=True)
    return path


def default_out(name):
    return os.path.join(evidence_dir(), name + ".json")


def isolated_config(tag):
    """A copy of the local configuration with its logs inside this run's own directory.

    The shared `config/kompas-mcp.local.json` points its journal at `scratch/logs/host.jsonl`, which
    a CLIENT-launched server also writes; measuring through it would mix this instrument's evidence
    with somebody else's session. Raises SystemExit with the instruction when no local config exists.
    """
    sys.path.insert(0, os.path.join(ROOT, "scripts"))
    from localconfig import isolate_config, local_config  # noqa: E402

    base = local_config(ROOT)
    logs = os.path.join(ROOT, "scratch", "api-audit", "logs")
    os.makedirs(logs, exist_ok=True)
    return isolate_config(base, logs, tag)


class HostClient:
    """Minimal MCP stdio client: newline-delimited JSON-RPC 2.0, no client library.

    Deliberately hand-rolled, like the acceptance harness: the audit must read the WIRE, not what a
    library chose to hide. Only `initialize`, `notifications/initialized` and `tools/list` are used -
    the audit does not run a CAD session.
    """

    def __init__(self, host_exe, config):
        args = [host_exe] + (["--config", config] if config else [])
        self.p = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=subprocess.PIPE, text=True, encoding="utf-8")
        self.n = 0
        self.err = []
        threading.Thread(target=self._pump, daemon=True).start()

    def _pump(self):
        while True:
            line = self.p.stderr.readline() if self.p.stderr else ""
            if not line:
                return
            self.err.append(line.rstrip())

    def send(self, message):
        self.p.stdin.write(json.dumps(message, ensure_ascii=False) + "\n")
        self.p.stdin.flush()

    def read(self, want, timeout=120):
        deadline = time.time() + timeout
        while time.time() < deadline:
            line = self.p.stdout.readline()
            if not line:
                if self.p.poll() is not None:
                    raise RuntimeError("Host завершился:\n" + "\n".join(self.err[-20:]))
                time.sleep(0.02)
                continue
            line = line.strip()
            if not line:
                continue
            message = json.loads(line)
            if message.get("id") == want:
                return message
        raise TimeoutError(f"id={want}")

    def call(self, method, params=None, timeout=120):
        self.n += 1
        message = {"jsonrpc": "2.0", "id": self.n, "method": method}
        if params is not None:
            message["params"] = params
        self.send(message)
        reply = self.read(self.n, timeout)
        if "error" in reply:
            raise RuntimeError(f"{method}: {reply['error']}")
        return reply["result"]

    def close(self):
        try:
            if self.p.stdin:
                self.p.stdin.close()
            self.p.wait(timeout=15)
        except Exception:
            self.p.kill()


def read_wire(host=None, worker=None, timeout=120):
    """Open the audited Host over stdio and read `serverInfo` and `tools/list`."""
    host = os.path.abspath(host or RELEASE_HOST)
    if not os.path.isfile(host):
        raise SystemExit(f"нет сборки Хоста: {host}")
    if worker:
        os.environ["KOMPAS_MCP_WORKER_PATH"] = os.path.abspath(worker)
    config = isolated_config("api-audit")
    client = HostClient(host, config)
    try:
        init = client.call("initialize", {
            "protocolVersion": "2025-06-18", "capabilities": {},
            "clientInfo": {"name": "api-audit", "version": "0"}}, timeout=timeout)
        client.send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        tools = client.call("tools/list", {}, timeout=timeout).get("tools", [])
        return init.get("serverInfo") or {}, tools
    finally:
        client.close()


# Keys that are not contract structure. `description` is published prose; `$schema`/`$id` name the
# JSON-Schema dialect and are present in `schemas/*.json` while the wire's `inputSchema` object does
# not carry them - comparing them would report all 90 schemas as diverging, which is a property of
# the comparison, not of the contract. `$defs`/`$ref` ARE structure and are kept.
SHAPE_SKIP = ("description", "$schema", "$id")


def schema_shape(node):
    """Schema shape with prose and the dialect declaration removed: structure only."""
    if isinstance(node, dict):
        return {key: schema_shape(value) for key, value in node.items() if key not in SHAPE_SKIP}
    if isinstance(node, list):
        return [schema_shape(item) for item in node]
    return node


def property_names(node):
    return sorted(((node or {}).get("properties") or {}).keys())


def route_tool_names(value, found=None):
    """Tool names declared by a route value, in either of its two shapes.

    A route is a STRING (`adapter_route`: "API5 o3d_baseExtrusion(24) + ...") or a DICT
    (`read_route`: {"mode": ..., "routes": [{"tool": "kompas_get_feature", ...}]}). Reading only one
    shape silently finds nothing, and "found nothing" then reads as "no route was declared".
    """
    found = found if found is not None else set()
    if isinstance(value, str):
        for token in value.replace(",", " ").replace(";", " ").replace("(", " ").split():
            token = token.strip(".:")
            if token.startswith("kompas_"):
                found.add(token)
    elif isinstance(value, dict):
        for key, item in value.items():
            if key == "tool" and isinstance(item, str) and item.startswith("kompas_"):
                found.add(item)
            else:
                route_tool_names(item, found)
    elif isinstance(value, list):
        for item in value:
            route_tool_names(item, found)
    return found


def is_route_key(key):
    """A declared route travels under a NAME PATTERN, not under a hand-written list.

    A hand list has already dropped a needed field twice on this project (defects #19 and #23): a
    newly added `*_route` was silently ignored and a row whose route was both declared and executed
    was reported as having none.
    """
    return key.endswith("_route") and key != "adapter_route"


def declared_route_tools(row):
    """Tool names a matrix row declares as its own route, structured fields first."""
    names = set()
    for key, value in row.items():
        if is_route_key(key) or key == "adapter_route":
            route_tool_names(value, names)
    return names
