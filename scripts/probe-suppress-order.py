"""Проба порядка подавления: состояние признака вокруг `excluded` и через сохранение/переоткрытие.

Зачем отдельным прибором. Наряд `DEBT_SUPPRESS_ORDER` (долг 6) требует не пересказать справку, а
НАЗВАТЬ измеренное состояние по трём семействам (выдавливание, вырез, скругление) вокруг трёх
событий: подавления, снятия подавления и сохранения с переоткрытием. Строки приёмки группы `suppress`
ведут это на своих постановках (B4M.09 и MANIA.20) и в двух местах НЕ ведут: (1) сохранение и
переоткрытие ПОСЛЕ подавления, (2) документированное следствие «исключённый из расчёта объект
считается не валидным» (DOC: ksentity_excluded.html, примечание 2).

Что измеряется на каждое семейство, в ОДНОМ документе:
  before      - состояние ДО подавления (объём, тела, состояние признака);
  suppressed  - сразу после подавления;
  restored    - после снятия подавления (обязано совпасть с before);
  suppressed2 - после ПОВТОРНОГО подавления;
  reopened    - после save → close → open (обязано совпасть с suppressed2);
  after_reopen_restore - попытка снять подавление в переоткрытом документе (названа отдельно:
                подавленный признак не показывается `kompas_list_features`, поэтому ссылку взять негде).

Границы. Проба не правит продукт и не судит его: она называет ответы. Объёмы сверяются с
аналитическими (пластина 100×80×10 = 80000; бобышка 30×30×10 = +9000; выемка 60×40×8 = −19200;
скругление R10 по вертикальному ребру L=10 = −(1−π/4)·r²·L).

Usage:
  python scripts/probe-suppress-order.py --host <KompasMcp.Host.exe> --label before --out <файл>

Журналы прибора изолированы: Host получает КОПИЮ конфигурации с перенаправленными путями
(`localconfig.isolate_config`), иначе проба затирала бы журнал клиентского сеанса.
"""

import datetime
import hashlib
import json
import math
import os
import queue
import subprocess
import sys
import threading
import uuid

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from localconfig import isolate_config  # noqa: E402

for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_CONFIG = os.path.join(ROOT, "config", "kompas-mcp.local.json")

PLATE_LO, PLATE_HI = (-50.0, -40.0, 0.0), (50.0, 40.0, 10.0)
PLATE_V = 100.0 * 80.0 * 10.0
BOSS_V = 30.0 * 30.0 * 10.0
CUT_V = 60.0 * 40.0 * 8.0
FILLET_R = 3.0
FILLET_L = 10.0
FILLET_V = (1.0 - math.pi / 4.0) * FILLET_R * FILLET_R * FILLET_L


def argument(name, default=None):
    if name in sys.argv:
        index = sys.argv.index(name)
        if index + 1 < len(sys.argv):
            return sys.argv[index + 1]
    return default


class Session:
    """Один Host по stdio. Читатель ровно один."""

    def __init__(self, host, config):
        self._proc = subprocess.Popen(
            [host, "--config", config],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8", errors="replace",
            cwd=os.path.expanduser("~"), bufsize=1,
        )
        self._lines = queue.Queue()
        threading.Thread(target=self._pump, daemon=True).start()
        self._next_id = 1

    def _pump(self):
        try:
            for line in self._proc.stdout:
                self._lines.put(line)
        except Exception:  # noqa: BLE001
            pass
        finally:
            self._lines.put(None)

    def _send(self, payload):
        self._proc.stdin.write(json.dumps(payload) + "\n")
        self._proc.stdin.flush()

    def call(self, method, params=None, timeout=300):
        request_id = self._next_id
        self._next_id += 1
        payload = {"jsonrpc": "2.0", "id": request_id, "method": method}
        if params is not None:
            payload["params"] = params
        self._send(payload)
        while True:
            try:
                line = self._lines.get(timeout=timeout)
            except queue.Empty:
                raise RuntimeError(f"timeout waiting for {method}")
            if line is None:
                raise RuntimeError(f"stdout closed while waiting for {method}")
            try:
                msg = json.loads(line)
            except json.JSONDecodeError:
                continue
            if msg.get("id") == request_id:
                if "error" in msg:
                    raise RuntimeError(f"{method}: {msg['error']}")
                return msg.get("result")

    def notify(self, method):
        self._send({"jsonrpc": "2.0", "method": method})

    def tool(self, name, args, timeout=300):
        result = self.call("tools/call", {"name": name, "arguments": args}, timeout=timeout)
        return result.get("structuredContent") or result

    def close(self):
        try:
            self._proc.stdin.close()
        except Exception:  # noqa: BLE001
            pass
        try:
            self._proc.wait(timeout=30)
        except Exception:  # noqa: BLE001
            self._proc.kill()


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def code_of(env):
    error = (env or {}).get("error")
    return error.get("code") if isinstance(error, dict) else None


def result_of(env):
    return (env or {}).get("result") or {}


def verification_of(env):
    return (env or {}).get("verification") or {}


def ref_id(value):
    """Ссылка приходит и строкой, и объектом `{id: ...}`: инструментам нужна строка."""
    if isinstance(value, dict):
        return value.get("id")
    return value


class Probe:
    def __init__(self, session, app_id):
        self.s = session
        self.app = app_id
        self._with_operation_id = self._read_operation_id_tools()

    def _read_operation_id_tools(self):
        """Какие инструменты ОБЪЯВЛЯЮТ operation_id: у остальных лишнее поле — отказ контракта."""
        listing = self.s.call("tools/list", {})
        names = set()
        for tool in (listing or {}).get("tools") or []:
            schema = tool.get("inputSchema") or {}
            if "operation_id" in (schema.get("properties") or {}):
                names.add(tool.get("name"))
        return names

    def call(self, tool, args, timeout=300):
        payload = dict(args)
        if tool in self._with_operation_id:
            payload.setdefault("operation_id", str(uuid.uuid4()))
        return self.s.tool(tool, payload, timeout=timeout)

    def revision(self, doc):
        env = self.call("kompas_get_context", {"document_id": doc, "detail": "minimal"})
        return result_of(env).get("revision") or 1

    def bodies(self, doc):
        env = self.call("kompas_list_bodies", {"document_id": doc})
        rows = result_of(env)
        return rows if isinstance(rows, list) else []

    def volume(self, ref):
        if not ref:
            return None
        env = self.call("kompas_measure", {"target_ref": ref, "properties": ["volume"]})
        return result_of(env).get("volume_mm3")

    def model(self, doc):
        """Объём каждого тела, сумма и число тел — состояние модели одним чтением."""
        per, total = [], 0.0
        for row in self.bodies(doc):
            v = self.volume(row.get("body_ref"))
            total += v or 0.0
            per.append({"ref": row.get("body_ref"), "volume_mm3": v, "bbox": row.get("bbox")})
        return {"bodies": len(per), "volume_total_mm3": total, "per_body": per}

    def features(self, doc):
        env = self.call("kompas_list_features", {"document_id": doc})
        rows = result_of(env)
        rows = rows if isinstance(rows, list) else []
        return [{"ref": ref_id(r.get("feature_ref")), "name": r.get("name"),
                 "type": str(r.get("type"))}
                for r in rows]

    def feature_state(self, fref):
        if not fref:
            return {"err": "ссылка не найдена"}
        env = self.call("kompas_get_feature", {"feature_ref": fref})
        r = result_of(env)
        return {"err": code_of(env), "name": r.get("name"), "family": r.get("family"),
                "excluded": r.get("excluded"), "is_valid": r.get("is_valid"),
                "object_error": r.get("object_error")}

    def find_feature(self, doc, want_name, want_types):
        """Признак после переоткрытия — ПО ИМЕНИ, а не по индексу: имя переживает переоткрытие."""
        rows = self.features(doc)
        for row in rows:
            if want_name and row["name"] == want_name:
                return row["ref"]
        for row in rows:
            if row["type"] in want_types:
                return row["ref"]
        return None

    def vertical_edge(self, doc, body):
        """Вертикальное ребро пластины: две вершины с общими X, Y и разными Z (ребро вдоль Z)."""
        env = self.call("kompas_read_topology", {"document_id": doc, "body_ref": body,
                                                 "include": "edges"})
        edges = result_of(env).get("edges") or []
        for edge in edges:
            verts = edge.get("vertices_mm") or []
            if len(verts) != 2:
                continue
            a, b = verts[0], verts[1]
            if (abs(a[0] - b[0]) <= 1e-6 and abs(a[1] - b[1]) <= 1e-6
                    and abs(abs(a[2] - b[2]) - PLATE_HI[2]) <= 1e-6):
                return edge.get("edge_ref"), {"curve_type": edge.get("curve_type"),
                                              "length_mm": edge.get("length_mm"),
                                              "vertices_mm": verts}
        return None, {"edges_seen": len(edges)}

    def snapshot(self, doc, fref, label):
        env = self.call("kompas_get_context", {"document_id": doc, "detail": "minimal"})
        return {
            "label": label,
            "revision": result_of(env).get("revision"),
            "model": self.model(doc),
            "features_listed": self.features(doc),
            "feature_state": self.feature_state(fref),
        }


def new_part(p, name):
    env = p.call("kompas_create_document", {"application_id": p.app, "kind": "part", "name": name})
    result = result_of(env)
    return env.get("document_id") or result.get("id"), (env.get("revision_after") or 1)


def draw(p, doc, rev, offset, entities, name):
    env = p.call("kompas_create_sketch", {
        "document_id": doc, "expected_revision": rev,
        "plane": {"base": "xy", "offset_mm": offset}, "name": name})
    sketch = result_of(env).get("id")
    rev = env.get("revision_after") or rev
    env = p.call("kompas_edit_sketch", {
        "sketch_ref": sketch, "expected_revision": rev, "mode": "append", "entities": entities})
    rev = env.get("revision_after") or rev
    env = p.call("kompas_finish_sketch", {
        "sketch_ref": sketch, "require_closed_profile": True})
    return sketch, (env.get("revision_after") or rev), code_of(env)


def plate(p, doc, rev, name):
    sketch, rev, err = draw(p, doc, rev, 0.0, [
        {"kind": "rectangle", "start_mm": [PLATE_LO[0], PLATE_LO[1]],
         "width_mm": PLATE_HI[0] - PLATE_LO[0], "height_mm": PLATE_HI[1] - PLATE_LO[1]}],
        name + "-plate")
    if err:
        return None, rev, err
    env = p.call("kompas_extrude", {
        "sketch_ref": sketch, "expected_revision": rev, "operation": "base", "depth_mm": 10.0,
        "direction": "positive", "end_condition": "blind"})
    return ref_id(env.get("feature_ref") or result_of(env).get("feature_ref")), \
        (env.get("revision_after") or rev), code_of(env)


def scenario(p, family, workdir):
    """Одно семейство: подавить → снять → подавить → save/close/open → попытка снять."""
    doc, rev = new_part(p, "SUP-" + family)
    rec = {"family": family, "document": doc, "steps": []}
    if not doc:
        rec["error"] = "документ не создан"
        return rec
    save_path = os.path.join(workdir, "sup-%s.m3d" % family)
    try:
        base_ref, rev, err = plate(p, doc, rev, family)
        if err or not base_ref:
            rec["error"] = "плита не построена: %s" % err
            return rec
        rec["plate_feature_ref"] = base_ref

        if family == "extrusion":
            sketch, rev, err = draw(p, doc, rev, 10.0, [
                {"kind": "rectangle", "start_mm": [-15.0, -15.0], "width_mm": 30.0,
                 "height_mm": 30.0}], "sup-boss")
            if err:
                rec["error"] = "эскиз бобышки: %s" % err
                return rec
            env = p.call("kompas_extrude", {
                "sketch_ref": sketch, "expected_revision": rev, "operation": "boss",
                "depth_mm": 10.0, "direction": "positive", "end_condition": "blind",
                "target_body_ref": p.bodies(doc)[0].get("body_ref")})
            rev = env.get("revision_after") or rev
            fref = ref_id(env.get("feature_ref") or result_of(env).get("feature_ref"))
            analytic = {"before": PLATE_V + BOSS_V, "suppressed": PLATE_V,
                        "feature_error": code_of(env)}
            want_types = {"45", "46", "47", "48", "49"}
        elif family == "cut":
            sketch, rev, err = draw(p, doc, rev, 10.0, [
                {"kind": "rectangle", "start_mm": [-30.0, -20.0], "width_mm": 60.0,
                 "height_mm": 40.0}], "sup-pocket")
            if err:
                rec["error"] = "эскиз выемки: %s" % err
                return rec
            env = p.call("kompas_extrude", {
                "sketch_ref": sketch, "expected_revision": rev, "operation": "cut",
                "depth_mm": 8.0, "direction": "positive", "end_condition": "blind",
                "target_body_ref": p.bodies(doc)[0].get("body_ref")})
            rev = env.get("revision_after") or rev
            fref = ref_id(env.get("feature_ref") or result_of(env).get("feature_ref"))
            analytic = {"before": PLATE_V - CUT_V, "suppressed": PLATE_V,
                        "feature_error": code_of(env)}
            want_types = {"45", "46", "47", "48", "49"}
        else:  # fillet
            body = p.bodies(doc)[0].get("body_ref")
            edge_ref, edge_info = p.vertical_edge(doc, body)
            if not edge_ref:
                rec["error"] = "вертикальное ребро не найдено: %s" % edge_info
                return rec
            env = p.call("kompas_fillet", {
                "edge_refs": [edge_ref], "expected_revision": rev, "radius_mm": FILLET_R})
            rev = env.get("revision_after") or rev
            fref = ref_id(env.get("feature_ref") or result_of(env).get("feature_ref"))
            analytic = {"before": PLATE_V - FILLET_V, "suppressed": PLATE_V,
                        "feature_error": code_of(env), "edge": edge_ref, "edge_info": edge_info}
            want_types = {"49", "50", "51", "52", "53"}
        rec["analytic"] = analytic
        rec["feature_ref"] = fref
        if not fref:
            rec["error"] = "признак не создан"
            return rec

        rec["steps"].append(p.snapshot(doc, fref, "before"))
        feature_name = rec["steps"][0]["feature_state"].get("name")

        env = p.call("kompas_set_feature_suppressed", {
            "feature_ref": fref, "expected_revision": rev, "suppressed": True})
        rev = env.get("revision_after") or rev
        rec["suppress_env"] = {"code": code_of(env), "level": verification_of(env).get("level"),
                               "checks": verification_of(env).get("checks"),
                               "unverified": verification_of(env).get("unverified_aspects")}
        rec["steps"].append(p.snapshot(doc, fref, "suppressed"))

        env = p.call("kompas_set_feature_suppressed", {
            "feature_ref": fref, "expected_revision": rev, "suppressed": False})
        rev = env.get("revision_after") or rev
        rec["restore_env"] = {"code": code_of(env), "level": verification_of(env).get("level"),
                              "checks": verification_of(env).get("checks"),
                              "unverified": verification_of(env).get("unverified_aspects")}
        rec["steps"].append(p.snapshot(doc, fref, "restored"))

        env = p.call("kompas_set_feature_suppressed", {
            "feature_ref": fref, "expected_revision": rev, "suppressed": True})
        rev = env.get("revision_after") or rev
        rec["suppress2_env"] = {"code": code_of(env)}
        rec["steps"].append(p.snapshot(doc, fref, "suppressed2"))

        save = p.call("kompas_save_document", {
            "document_id": doc, "target_path": save_path, "expected_revision": rev})
        rev = save.get("revision_after") or rev
        close = p.call("kompas_close_document", {"document_id": doc, "dirty_policy": "refuse"})
        open_env = p.call("kompas_open_document", {
            "application_id": p.app, "path": save_path, "access": "edit"})
        doc2 = open_env.get("document_id") or result_of(open_env).get("document_id")
        rec["save_close_open"] = {"save": code_of(save), "close": code_of(close),
                                  "open": code_of(open_env), "path": save_path,
                                  "reopened_document": doc2}
        if not doc2:
            rec["error"] = "документ не переоткрылся"
            return rec
        rec["steps"].append(p.snapshot(doc2, None, "reopened"))

        # Ссылка, взятая ДО переоткрытия: переживает ли она его? Измеряется КОД ответа тем же вызовом,
        # а не предположение «должна отсечься».
        rev_old = p.revision(doc2)
        env_old = p.call("kompas_set_feature_suppressed", {
            "feature_ref": fref, "expected_revision": rev_old, "suppressed": False})
        rec["restore_with_old_ref"] = {"code": code_of(env_old),
                                       "unverified": verification_of(env_old).get("unverified_aspects"),
                                       "volume_after_mm3": result_of(env_old).get("volume_after_mm3")}

        # Подавленный признак не показывается kompas_list_features: ссылку взять негде. Проба НАЗЫВАЕТ
        # это измерением, а не пробует угадать ссылку.
        fref_reopen = p.find_feature(doc2, feature_name, want_types)
        rec["feature_name"] = feature_name
        rec["feature_ref_after_reopen"] = fref_reopen
        if fref_reopen:
            rev2 = p.revision(doc2)
            env = p.call("kompas_set_feature_suppressed", {
                "feature_ref": fref_reopen, "expected_revision": rev2, "suppressed": False})
            rec["restore_after_reopen_env"] = {"code": code_of(env),
                                               "unverified": verification_of(env).get("unverified_aspects")}
            rec["steps"].append(p.snapshot(doc2, fref_reopen, "after_reopen_restore"))
        else:
            rec["restore_after_reopen_env"] = {
                "code": "FEATURE_NOT_LISTED",
                "note": "подавленный признак не показывается kompas_list_features — снять подавление "
                        "в переоткрытом документе нечем"}
    finally:
        p.call("kompas_close_document", {"document_id": doc, "dirty_policy": "discard"})
    return rec


def main():
    host = os.path.abspath(argument("--host"))
    label = argument("--label") or "run"
    out = os.path.abspath(argument("--out") or os.path.join(
        ROOT, "scratch", "suppress-order", "probe-suppress-order-%s.json" % label))
    config = os.path.abspath(argument("--config") or DEFAULT_CONFIG)
    isolated = isolate_config(config, os.path.dirname(out), "probe-suppress-order-" + label)

    report = {
        "title": "Проба порядка подавления (excluded) и переоткрытия",
        "label": label,
        "host_path": host,
        "host_sha256": sha256(host),
        "config_path": isolated,
        "when": datetime.datetime.now().isoformat(timespec="seconds"),
        "analytics": {"plate_mm3": PLATE_V, "boss_mm3": BOSS_V, "cut_mm3": CUT_V,
                      "fillet_mm3": FILLET_V},
        "scenarios": {},
    }

    session = Session(host, isolated)
    try:
        init = session.call("initialize", {
            "protocolVersion": "2025-06-18", "capabilities": {},
            "clientInfo": {"name": "probe-suppress-order", "version": "0"}}, timeout=60)
        report["build_stamp"] = (init.get("serverInfo") or {}).get("version")
        session.notify("notifications/initialized")

        connect = session.tool("kompas_connect", {
            "mode": "launch", "make_visible": False, "operation_id": str(uuid.uuid4())})
        app_id = connect.get("application_id")
        if not app_id:
            raise RuntimeError("kompas_connect не вернул application_id")
        report["application_id"] = app_id

        probe = Probe(session, app_id)
        workdir = os.path.join(os.path.dirname(out), "models")
        os.makedirs(workdir, exist_ok=True)
        for family in ("extrusion", "cut", "fillet"):
            report["scenarios"][family] = scenario(probe, family, workdir)

        session.tool("kompas_disconnect", {
            "application_id": app_id, "close_owned_application": True,
            "operation_id": str(uuid.uuid4())}, timeout=120)
    finally:
        session.close()

    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "w", encoding="utf-8") as fh:
        json.dump(report, fh, ensure_ascii=False, indent=2)
    print(json.dumps(report, ensure_ascii=False, indent=2))
    print("\nЗаписано: " + out)


if __name__ == "__main__":
    main()
