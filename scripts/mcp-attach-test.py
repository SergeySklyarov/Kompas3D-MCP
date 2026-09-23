"""Attach test against a КОМПАС instance started by the user.

Why this is a separate script rather than a flag on the smoke test: every earlier attach
observation was taken while КОМПАС was started by COM activation, which is the confounder itself —
P0.11 showed an empty ROT for exactly that kind of instance. This run requires a visible,
user-launched КОМПАС and refuses to run otherwise, so its verdict cannot be produced by an
unrelated environment.

Read-only by construction: capabilities, connect, list_documents, disconnect. Nothing creates,
opens, edits or saves a document, and disconnect is called with close_owned_application=false so
the user's instance is never shut down. The script asserts that afterwards.
"""

import json
import os
import subprocess
import sys
import time
import uuid

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from localconfig import local_config
import importlib.util

spec = importlib.util.spec_from_file_location(
    "smoke", os.path.join(os.path.dirname(os.path.abspath(__file__)), "mcp-smoke.py"))
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)

ROOT = smoke.ROOT
HOST = os.path.join(ROOT, "src", "KompasMcp.Host", "bin", "Debug", "net10.0-windows", "KompasMcp.Host.exe")
CONFIG = local_config(ROOT)


def kompas_visible_pids():
    """PIDs of running KOMPAS processes that own a window, read from the OS."""
    out = subprocess.run(
        ["powershell", "-NoProfile", "-Command",
         "Get-Process KOMPAS -ErrorAction SilentlyContinue | ForEach-Object { \"$($_.Id);$($_.MainWindowHandle)\" }"],
        capture_output=True, text=True, timeout=60)
    pids = []
    for line in out.stdout.splitlines():
        line = line.strip()
        if not line or ";" not in line:
            continue
        pid_s, _, hwnd_s = line.partition(";")
        try:
            if int(hwnd_s) != 0:
                pids.append(int(pid_s))
        except ValueError:
            continue
    return pids


def call(client, tool, args, timeout=240):
    """Return (envelope_or_None, raw_result, protocol_error_text).

    Three distinct outcomes again: a protocol-level failure has no structuredContent at all, an
    error envelope does — conflating them is what made the last investigation look at КОМПАС.
    """
    is_err, structured, raw = client.tool(tool, args, timeout=timeout)
    if not isinstance(structured, dict):
        content = raw.get("content") if isinstance(raw, dict) else None
        text = "; ".join(c.get("text", "") for c in content if c.get("type") == "text") if content else ""
        return None, raw, text or "нет structuredContent"
    return structured, raw, None


def show(label, env, note=None):
    print(f"\n--- {label}")
    if env is None:
        print("  ПРОТОКОЛЬНАЯ ОШИБКА:", (note or "")[:500])
        return
    print("  status   :", env.get("status"))
    err = env.get("error")
    if isinstance(err, dict):
        print("  error    :", err.get("code"), "-", (err.get("message") or "")[:300])
        if err.get("details"):
            print("  details  :", json.dumps(err["details"], ensure_ascii=False)[:600])
    result = env.get("result")
    if result is not None:
        print("  result   :", json.dumps(result, ensure_ascii=False)[:900])
    if env.get("application_id"):
        print("  app_id   :", env.get("application_id"))
    if note:
        print("  note     :", note[:300])


def main():
    visible = kompas_visible_pids()
    print("KOMPAS с окном (visible PID):", visible)
    if not visible:
        print("КОМПАС, запущенный пользователем, не найден — тест attach не проводится.")
        print("COM-активированный экземпляр не подходит: пустой ROT для него и есть проверяемое свойство.")
        return 3

    c = smoke.Client(HOST, CONFIG if os.path.exists(CONFIG) else None)
    verdicts = {}
    app_id = None
    try:
        c.call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                              "clientInfo": {"name": "attach-test", "version": "0"}}, timeout=90)
        c.notify("notifications/initialized")

        env, raw, note = call(c, "kompas_capabilities", {}, 90)
        show("kompas_capabilities — что видит Host", env, note)
        caps = (env or {}).get("result") or {}
        print("  running_instances по данным Host:", caps.get("running_instances"),
              "| interop:", caps.get("interop_directory"))

        # 1) attach with no explicit selector. With one instance the server may resolve it
        #    unambiguously — but it must never pick silently when it cannot identify the process.
        env1, raw1, note1 = call(c, "kompas_connect", {
            "mode": "attach", "process_id": None, "make_visible": False,
            "operation_id": str(uuid.uuid4())})
        show("connect(attach) без process_id", env1, note1)
        if env1 is None:
            verdicts["attach_implicit"] = "protocol_error"
        elif env1.get("status") == "succeeded":
            verdicts["attach_implicit"] = "succeeded"
            app_id = (env1.get("result") or {}).get("application_id") or env1.get("application_id")
        else:
            verdicts["attach_implicit"] = ((env1.get("error") or {}).get("code")) or "failed"

        # 2) attach with the explicit PID — the path the contract recommends.
        if app_id is None:
            env2, raw2, note2 = call(c, "kompas_connect", {
                "mode": "attach", "process_id": visible[0], "make_visible": False,
                "operation_id": str(uuid.uuid4())})
            show(f"connect(attach) с process_id={visible[0]}", env2, note2)
            if env2 is None:
                verdicts["attach_explicit"] = "protocol_error"
            elif env2.get("status") == "succeeded":
                verdicts["attach_explicit"] = "succeeded"
                app_id = (env2.get("result") or {}).get("application_id") or env2.get("application_id")
            else:
                verdicts["attach_explicit"] = ((env2.get("error") or {}).get("code")) or "failed"
        else:
            verdicts["attach_explicit"] = "не потребовался"

        # 3) the session must be usable for a read, or "attached" would be a lie.
        if app_id:
            env3, raw3, note3 = call(c, "kompas_list_documents", {"application_id": app_id}, 120)
            show("list_documents через attached-сеанс (только чтение)", env3, note3)
            docs = (env3 or {}).get("result")
            if isinstance(docs, list):
                verdicts["read_through_session"] = f"ok, документов: {len(docs)}"
                for d in docs[:5]:
                    print("   •", json.dumps({k: d.get(k) for k in
                                              ("id", "kind", "path", "dirty", "revision")},
                                             ensure_ascii=False))
            else:
                verdicts["read_through_session"] = "нет данных"

            env4, raw4, note4 = call(c, "kompas_disconnect", {
                "application_id": app_id, "close_owned_application": False,
                "operation_id": str(uuid.uuid4())}, 120)
            show("disconnect (close_owned_application=false)", env4, note4)
            verdicts["disconnect"] = (env4 or {}).get("status") or note4 or "?"
        # 4) the safety-critical case: with TWO instances running an implicit attach must refuse
        #    rather than pick one. The second instance here is one this script started itself and
        #    will close itself; the user's instance is never a candidate for shutdown.
        env5, raw5, note5 = call(c, "kompas_connect", {
            "mode": "launch", "process_id": None, "make_visible": False,
            "operation_id": str(uuid.uuid4())}, 300)
        own_id = (env5 or {}).get("application_id") or ((env5 or {}).get("result") or {}).get("application_id")
        own_pid = ((env5 or {}).get("result") or {}).get("process_id")
        print(f"\n--- второй экземпляр запущен нами: app={own_id} pid={own_pid}")
        if own_id:
            # Probe how many ROT entries the server actually sees while two instances run: attach
            # with a PID that cannot exist, and read the observed list from the refusal details.
            probe, _, probe_note = call(c, "kompas_connect", {
                "mode": "attach", "process_id": 999999, "make_visible": False,
                "operation_id": str(uuid.uuid4())}, 120)
            perr = (probe or {}).get("error") or {}
            pdet = perr.get("details") or {}
            observed = pdet.get("observed_process_ids")
            print("\n--- сколько экземпляров видит сервер при двух запущенных")
            print("  observed_process_ids:", json.dumps(observed, ensure_ascii=False),
                  "| rot_entries_matching_kompas:", pdet.get("rot_entries_matching_kompas"),
                  "| rot_total_entries:", pdet.get("rot_total_entries"),
                  "| note:", (probe_note or "")[:200])
            verdicts["rot_visible_instances_with_two_running"] = (
                f"{len(observed) if isinstance(observed, list) else '?'} записей: {json.dumps(observed, ensure_ascii=False)}")

            env6, raw6, note6 = call(c, "kompas_connect", {
                "mode": "attach", "process_id": None, "make_visible": False,
                "operation_id": str(uuid.uuid4())}, 120)
            show("connect(attach) без process_id при ДВУХ экземплярах", env6, note6)
            code = ((env6 or {}).get("error") or {}).get("code")
            entries_seen = len(observed) if isinstance(observed, list) else None

            # The refusal is required only when more than one candidate is actually visible in the
            # ROT. A headless instance launched via COM does not register there at all, so with one
            # interactive КОМПАС running, attaching to it is a determinate choice, not a guess —
            # demanding AMBIGUOUS_APPLICATION here would assert a defect that the evidence does not
            # support.
            if entries_seen is not None and entries_seen > 1:
                verdicts["attach_two_instances"] = (
                    "correctly refused" if code == "AMBIGUOUS_APPLICATION"
                    else f"{code or (env6 or {}).get('status')} — при {entries_seen} кандидатах обязан отказываться")
            else:
                verdicts["attach_two_instances"] = (
                    f"не проверимо: в ROT {entries_seen} запись (безоконный экземпляр сервера там не появляется);"
                    " выбор при единственном кандидате правомерен")
                if (env6 or {}).get("status") == "succeeded":
                    stray = (env6.get("result") or {}).get("application_id") or env6.get("application_id")
                    if stray and stray != own_id:
                        call(c, "kompas_disconnect", {
                            "application_id": stray, "close_owned_application": False,
                            "operation_id": str(uuid.uuid4())}, 120)

            env7, _, _ = call(c, "kompas_disconnect", {
                "application_id": own_id, "close_owned_application": True,
                "operation_id": str(uuid.uuid4())}, 180)
            verdicts["own_instance_closed"] = (env7 or {}).get("status") or "?"
    finally:
        c.close()
        time.sleep(1)
        still = kompas_visible_pids()
        alive = bool(visible) and all(p in still for p in visible)
        print("\nПользовательский КОМПАС после теста:", still,
              "— жив, как и должно быть" if alive else "— ВНИМАНИЕ: экземпляр пользователя исчез!")
        verdicts["user_instance_survived"] = str(alive)

    print("\n=== ИТОГ ===")
    for k, v in verdicts.items():
        print(f"  {k:26} = {v}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
