"""Step-by-step diagnostic of the geometry path: print the full envelope per call.

The smoke test asserts; this one explains. Used when a mutation returns an error whose text is
truncated in the reporter.
"""

import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from localconfig import local_config

import importlib.util
spec = importlib.util.spec_from_file_location("smoke", os.path.join(os.path.dirname(os.path.abspath(__file__)), "mcp-smoke.py"))
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)

uuid = smoke.uuid
ROOT = smoke.ROOT


def show(label, env):
    print(f"\n### {label}")
    print(json.dumps(env, ensure_ascii=False, indent=1)[:1800])


def main():
    host = os.path.join(ROOT, "src", "KompasMcp.Host", "bin", "Debug", "net10.0-windows", "KompasMcp.Host.exe")
    config = local_config(ROOT)
    workdir = os.path.join(ROOT, "scratch", "mcp-diag-geom")
    os.makedirs(workdir, exist_ok=True)

    c = smoke.Client(host, config)
    try:
        c.call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                              "clientInfo": {"name": "diag", "version": "0"}}, timeout=60)
        c.notify("notifications/initialized")

        _, env, _ = c.tool("kompas_connect", {"mode": "launch", "make_visible": False, "operation_id": str(uuid.uuid4())}, timeout=300)
        show("connect", env)
        app = (env or {}).get("application_id")

        _, env, _ = c.tool("kompas_create_document", {"application_id": app, "kind": "part",
                                                     "operation_id": str(uuid.uuid4())})
        show("create_document", env)
        doc = (env or {}).get("document_id")

        _, env, _ = c.tool("kompas_create_sketch", {"document_id": doc, "expected_revision": 1,
                                                   "plane": {"base": "xy", "offset_mm": 0},
                                                   "operation_id": str(uuid.uuid4())})
        show("create_sketch", env)
        sketch = ((env or {}).get("result") or {}).get("id")
        rev = (env or {}).get("revision_after") or 1

        _, env, _ = c.tool("kompas_edit_sketch", {
            "sketch_ref": sketch, "expected_revision": rev, "mode": "replace",
            "entities": [{"kind": "rectangle", "start_mm": [0, 0], "width_mm": 100, "height_mm": 80}],
            "operation_id": str(uuid.uuid4())}, timeout=240)
        show("edit_sketch", env)
        rev = (env or {}).get("revision_after") or rev

        _, env, _ = c.tool("kompas_finish_sketch", {"sketch_ref": sketch, "require_closed_profile": False,
                                                   "operation_id": str(uuid.uuid4())})
        show("finish_sketch", env)

        _, env, _ = c.tool("kompas_extrude", {"sketch_ref": sketch, "expected_revision": rev,
                                             "operation": "base", "depth_mm": 10,
                                             "direction": "positive",
                                             "operation_id": str(uuid.uuid4())}, timeout=240)
        show("extrude", env)

        _, env, _ = c.tool("kompas_list_bodies", {"document_id": doc})
        show("list_bodies", env)

        _, env, _ = c.tool("kompas_get_context", {"document_id": doc, "detail": "full"})
        show("get_context", env)

        print("\n### stderr Host:")
        print("\n".join(c.err_lines[-40:]))
    finally:
        c.close()


if __name__ == "__main__":
    main()
