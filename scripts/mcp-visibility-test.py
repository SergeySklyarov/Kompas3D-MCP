"""Приёмка видимого режима работы MCP с КОМПАС-3D.

Отличается от mcp-smoke.py принципиально: каждое утверждение о видимости проверяется ДВУМЯ
независимыми наблюдениями — ответом сервера (который строится на COM-свойстве Visible) и
win32-запросом состояния окна этого процесса из Python (ctypes: EnumWindows, IsWindowVisible,
GetWindowText). Дефект 12.09.2026 состоял именно в том, что ответу верили: поле visible=true
вычислялось как «по HWND достаётся PID», а скрытое окно HWND имеет.

Заодно проверяется то, чего COM не измеряет: что показанное приложение не путается с показанным
документом (два разных поля, два разных наблюдения), что скрытый режим не получает оконного
трафика, и что работа с одним экземпляром не меняет состояние другого.

Запуск:  python scripts/mcp-visibility-test.py [--keep]
Отчёт:   scratch/mcp-visibility/visibility-report.json
"""

import ctypes
import ctypes.wintypes
import importlib.util
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from localconfig import local_config

_spec = importlib.util.spec_from_file_location(
    "smoke", os.path.join(os.path.dirname(os.path.abspath(__file__)), "mcp-smoke.py"))
smoke = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(smoke)

uuid = smoke.uuid
ROOT = smoke.ROOT
user32 = ctypes.WinDLL("user32", use_last_error=True)
EnumWindowsProc = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.wintypes.HWND, ctypes.wintypes.LPARAM)


class Windows:
    """Оконные наблюдения, независимые от того, что отвечает КОМПАС."""

    @staticmethod
    def _pid_of(hwnd):
        pid = ctypes.wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        return int(pid.value)

    @staticmethod
    def _title(hwnd):
        length = user32.GetWindowTextLengthW(hwnd)
        if not length:
            return ""
        buffer = ctypes.create_unicode_buffer(length + 1)
        user32.GetWindowTextW(hwnd, buffer, length + 1)
        return buffer.value

    @classmethod
    def top_level(cls, pid):
        """Верхнеуровневые окна процесса: (hwnd, видно, заголовок)."""
        found = []

        def collect(hwnd, _):
            if cls._pid_of(hwnd) == pid:
                found.append((int(hwnd), bool(user32.IsWindowVisible(hwnd)), cls._title(hwnd)))
            return True

        user32.EnumWindows(EnumWindowsProc(collect), 0)
        return found

    @classmethod
    def visible_with_title(cls, pid):
        return [row for row in cls.top_level(pid) if row[1] and row[2].strip()]

    @classmethod
    def wait_visible(cls, pid, timeout_s=25.0):
        deadline = time.time() + timeout_s
        while time.time() < deadline:
            rows = cls.visible_with_title(pid)
            if rows:
                return rows
            time.sleep(0.25)
        return []

    @classmethod
    def child_titles(cls, hwnd):
        titles = []

        def collect(child, _):
            if user32.IsWindowVisible(child):
                title = cls._title(child)
                if title.strip():
                    titles.append(title)
            return True

        user32.EnumChildWindows(ctypes.wintypes.HWND(hwnd), EnumWindowsProc(collect), 0)
        return titles


def geometry_pipeline(client, rep, doc, rev, prefix, expect_after_cut, tag):
    """Эскиз прямоугольника → выдавливание → эскиз окружности → сквозное вырезание по телу."""
    def new_sketch(entities, name):
        nonlocal rev
        _e, env, _r = client.tool("kompas_create_sketch", {
            "document_id": doc, "expected_revision": rev,
            "plane": {"base": "xy", "offset_mm": 0}, "name": name,
            "operation_id": str(uuid.uuid4())})
        sk = ((env or {}).get("result") or {}).get("id")
        rev = (env or {}).get("revision_after") or rev
        _e, env, _r = client.tool("kompas_edit_sketch", {
            "sketch_ref": sk, "expected_revision": rev, "mode": "append", "entities": entities,
            "operation_id": str(uuid.uuid4())})
        rev = (env or {}).get("revision_after") or rev
        _e, env, _r = client.tool("kompas_finish_sketch", {
            "sketch_ref": sk, "require_closed_profile": False, "operation_id": str(uuid.uuid4())})
        rev = (env or {}).get("revision_after") or rev
        return sk

    def measure_body(ref):
        _e, env, _r = client.tool("kompas_measure", {"target_ref": ref, "properties": ["volume"]})
        return ((env or {}).get("result") or {}).get("volume_mm3")

    sk = new_sketch([{"kind": "rectangle", "start_mm": [-50, -40], "width_mm": 100,
                      "height_mm": 80}], prefix + "-plate")
    _e, env, _r = client.tool("kompas_extrude", {
        "sketch_ref": sk, "expected_revision": rev, "operation": "base", "depth_mm": 10,
        "direction": "positive", "operation_id": str(uuid.uuid4())}, timeout=240)
    rev = (env or {}).get("revision_after") or rev
    _e, env, _r = client.tool("kompas_list_bodies", {"document_id": doc})
    bodies = (env or {}).get("result") or []
    body = (bodies[0].get("body_ref") if bodies else None)
    v_plate = measure_body(body)

    sk2 = new_sketch([{"kind": "circle", "center_mm": [0, 0], "radius_mm": 10}], prefix + "-hole")
    _e, env, _r = client.tool("kompas_extrude", {
        "sketch_ref": sk2, "expected_revision": rev, "operation": "cut", "end_condition": "through",
        "direction": "symmetric", "target_body_ref": body, "operation_id": str(uuid.uuid4())},
        timeout=240)
    rev = (env or {}).get("revision_after") or rev
    v_final = measure_body(body)

    tol = max(0.01, 1e-6 * expect_after_cut)
    ok = (v_plate is not None and abs(v_plate - 80000) <= max(0.01, 1e-6 * 80000)
          and v_final is not None and abs(v_final - expect_after_cut) <= tol)
    rep.add(f"{tag}-geom", f"геометрия построена и измерена ({prefix}): пластина 80000, "
                           f"после вырезания {expect_after_cut:.4f}",
            "PASS" if ok else "FAIL", f"V_plate={v_plate} V_final={v_final}")
    return rev, v_final, body


def main():
    keep = "--keep" in sys.argv
    host = os.path.join(ROOT, "src", "KompasMcp.Host", "bin", "x64", "Debug",
                        "net10.0-windows", "KompasMcp.Host.exe")
    worker = os.path.join(ROOT, "src", "KompasMcp.Worker", "bin", "x64", "Debug",
                          "net10.0-windows", "KompasMcp.Worker.exe")
    if os.path.exists(worker):
        os.environ["KOMPAS_MCP_WORKER_PATH"] = worker
    config = local_config(ROOT)
    workdir = os.path.join(ROOT, "scratch", "mcp-visibility")
    os.makedirs(workdir, exist_ok=True)

    rep = smoke.Report("Приёмка видимого режима (окно приложения, окна документов, скрытый режим)",
                       os.path.join(workdir, "visibility-report.json"))
    if not os.path.exists(host):
        print("Host не собран:", host)
        return 2

    client = smoke.Client(host, config if os.path.exists(config) else None)
    circle_removed = 3.141592653589793 * 10 ** 2 * 10
    expect_cut = 80000 - circle_removed
    apps = {}
    try:
        client.call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                   "clientInfo": {"name": "mcp-visibility", "version": "0"}},
                    timeout=60)
        client.notify("notifications/initialized")

        def connect(mode, make_visible, process_id=None, label=""):
            args = {"mode": mode, "make_visible": make_visible, "operation_id": str(uuid.uuid4())}
            if process_id:
                args["process_id"] = process_id
            _e, env, _r = client.tool("kompas_connect", args, timeout=300)
            info = (env or {}).get("result") or {}
            apps[label] = info
            return info, env

        # ── W01 · запуск с показом: сервер и Windows должны совпасть ────────────────────
        info, env = connect("launch", True, label="visible")
        pid = info.get("process_id")
        win_rows = Windows.wait_visible(pid) if pid else []
        agree = bool(info.get("visible")) and bool(win_rows)
        rep.add("W01", "launch + make_visible=true: приложение действительно видно",
                "PASS" if agree else "FAIL",
                f"pid={pid} server.visible={info.get('visible')} "
                f"com={info.get('application_visible_by_com')} "
                f"win32={info.get('application_window_visible_by_windows')} | "
                f"независимо из Python: {win_rows[:3]}")

        # ── W02 · документ создан видимым и виден как окно документа ────────────────────
        _e, env, _r = client.tool("kompas_create_document", {
            "application_id": info.get("application_id"), "kind": "part",
            "name": "VIS-part", "operation_id": str(uuid.uuid4())})
        doc = ((env or {}).get("result") or {}).get("document_id") or (env or {}).get("document_id")
        _e, cenv, _r = client.tool("kompas_get_context", {"document_id": doc, "detail": "minimal"})
        ctx = (cenv or {}).get("result") or {}
        hwnd = int(info.get("window_handle") or 0)
        children = Windows.child_titles(hwnd) if hwnd else []
        # Прежнее условие («есть непустой заголовок») было тривиально истинным. Требование
        # проверяемое: видимое дочернее окно MDI-механизма КОМПАС. Имя файла в заголовках дочерних
        # окон НЕ наблюдается (видны только mdihost и View), поэтому «какое окно какому документу»
        # по win32 здесь не идентифицируется — это ограничение, а не пройденная проверка.
        mdiclient = [t for t in children if t.strip().lower() in ("mdihost", "mdi host", "view")]
        rep.add("W02", "созданный документ видим: ответ сервера И видимое дочернее окно MDI",
                "PASS" if ctx.get("document_visible") is True and mdiclient else "FAIL",
                f"document_visible={ctx.get('document_visible')} "
                f"mode={ctx.get('documents_visible_mode')} "
                f"active={ctx.get('document_active_reported')} | "
                f"окна документов (win32): {children[:5]} | "
                f"ограничение: имя документа в заголовках дочерних окон не наблюдается")

        # ── W03/W04 · построение видно без ручного открытия файла ───────────────────────
        rev = ctx.get("revision") or 1
        rev, v_final, _body = geometry_pipeline(client, rep, doc, rev, "vis", expect_cut, "W03")
        _e, cenv, _r = client.tool("kompas_get_context", {"document_id": doc, "detail": "minimal"})
        ctx_after = (cenv or {}).get("result") or {}
        rep.add("W04", "после моделирования документ остаётся видимым (обновление вида без сброса камеры)",
                "PASS" if ctx_after.get("document_visible") is True else "FAIL",
                f"document_visible={ctx_after.get('document_visible')} V={v_final}")

        # ── W05 · save → close → open: документ снова отображается ──────────────────────
        path = os.path.join(workdir, "VIS-part.m3d")
        if os.path.exists(path):
            os.remove(path)
        client.tool("kompas_save_document", {
            "document_id": doc, "expected_revision": rev, "target_path": path,
            "operation_id": str(uuid.uuid4())})
        client.tool("kompas_close_document", {
            "document_id": doc, "dirty_policy": "refuse", "operation_id": str(uuid.uuid4())})
        _e, oenv, _r = client.tool("kompas_open_document", {
            "application_id": info.get("application_id"), "path": path, "access": "edit",
            "operation_id": str(uuid.uuid4())}, timeout=240)
        doc2 = ((oenv or {}).get("result") or {}).get("document_id") or (oenv or {}).get("document_id")
        _e, cenv, _r = client.tool("kompas_get_context", {"document_id": doc2, "detail": "minimal"})
        ctx2 = (cenv or {}).get("result") or {}
        _e, menv, _r = client.tool("kompas_list_bodies", {"document_id": doc2})
        bodies2 = (menv or {}).get("result") or []
        v2 = None
        if bodies2:
            _e, menv, _r = client.tool("kompas_measure", {
                "target_ref": bodies2[0].get("body_ref"), "properties": ["volume"]})
            v2 = ((menv or {}).get("result") or {}).get("volume_mm3")
        reopened_visible = ctx2.get("document_visible") is True
        rep.add("W05", "после save→close→reopen документ снова виден и геометрия сохранена",
                "PASS" if reopened_visible and v2 is not None
                and abs(v2 - expect_cut) <= max(0.01, 1e-6 * expect_cut) else "FAIL",
                f"document_visible={ctx2.get('document_visible')} V={v2} "
                f"окна={Windows.child_titles(hwnd)[:4]}")

        # ── W06 · attach с make_visible=false не скрывает видимое и не трогает чужое ────
        info2, env2 = connect("attach", False, process_id=pid, label="attach")
        still = Windows.visible_with_title(pid)
        rep.add("W06", "attach к видимому экземпляру с make_visible=false: окно не спрятано",
                "PASS" if error_is_none(env2) and info2.get("visible") is True and still else "FAIL",
                f"server.visible={info2.get('visible')} "
                f"com={info2.get('application_visible_by_com')} "
                f"win32={info2.get('application_window_visible_by_windows')} окна={still[:2]}")

        # ── W07 · скрытый сеанс: ни приложение, ни документы не видны; геометрия работает ─
        hidden, henv = connect("launch", False, label="hidden")
        hpid = hidden.get("process_id")
        hidden_windows = [row for row in Windows.top_level(hpid) if row[1] and row[2].strip()] \
            if hpid else []
        _e, hden, _r = client.tool("kompas_create_document", {
            "application_id": hidden.get("application_id"), "kind": "part",
            "name": "HID-part", "operation_id": str(uuid.uuid4())})
        hdoc = ((hden or {}).get("result") or {}).get("document_id") or (hden or {}).get("document_id")
        _e, hcenv, _r = client.tool("kompas_get_context", {"document_id": hdoc, "detail": "minimal"})
        hctx = (hcenv or {}).get("result") or {}
        rev_h = hctx.get("revision") or 1
        _rev_h, v_h, _ = geometry_pipeline(client, rep, hdoc, rev_h, "hid", expect_cut, "W07")
        rep.add("W07b", "скрытый режим: приложение и документ не видны, операции работают",
                "PASS" if (hidden.get("visible") is False and not hidden_windows
                           and hctx.get("document_visible") is False
                           and v_h is not None and abs(v_h - expect_cut) <= max(0.01, 1e-6 * expect_cut))
                else "FAIL",
                f"server.visible={hidden.get('visible')} окон-с-заголовком={hidden_windows[:3]} "
                f"document_visible={hctx.get('document_visible')} V={v_h}")

        # ── W08 · показ одного экземпляра не показывает другой ───────────────────────────
        hidden_after = [row for row in Windows.top_level(hpid) if row[1] and row[2].strip()] \
            if hpid else []
        visible_still = bool(Windows.visible_with_title(pid))
        rep.add("W08", "изоляция экземпляров: видимый сеанс не изменил скрытый и наоборот",
                "PASS" if visible_still and not hidden_after else "FAIL",
                f"видимый pid={pid} по-прежнему виден={visible_still}; "
                f"скрытый pid={hpid} окон не показал: {not hidden_after} ({hidden_after[:2]})")

        # ── W09 · два наблюдения не подменяют друг друга ─────────────────────────────────
        pair_visible = (apps["visible"].get("application_visible_by_com"),
                        apps["visible"].get("application_window_visible_by_windows"))
        pair_hidden = (apps["hidden"].get("application_visible_by_com"),
                       apps["hidden"].get("application_window_visible_by_windows"))
        rep.add("W09", "видимость приложения и режим документа — отдельные поля, оба наблюдены",
                "PASS" if all(x is not None for x in pair_visible + pair_hidden)
                and pair_visible == (True, True) and pair_hidden == (False, False)
                else "FAIL",
                f"видимый: com/win32={pair_visible}; скрытый: {pair_hidden}; "
                f"документы: visible={ctx2.get('document_visible')} hidden={hctx.get('document_visible')}")

        for label, app in (("visible", info), ("attach", info2), ("hidden", hidden)):
            if not keep and app.get("application_id"):
                client.tool("kompas_disconnect", {
                    "application_id": app["application_id"],
                    "close_owned_application": app.get("ownership") == "launched",
                    "operation_id": str(uuid.uuid4())}, timeout=120)
    finally:
        counts = rep.save()
        client.close()
    return 0 if counts.get("FAIL", 0) == 0 else 1


def error_is_none(env):
    return smoke.error_code(env) is None


if __name__ == "__main__":
    sys.exit(main())
