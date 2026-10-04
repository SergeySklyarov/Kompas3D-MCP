"""Передача CAD-сеанса между двумя независимыми MCP-клиентами.

Зачем отдельный прибор, а не флаг у `mcp-smoke.py`. Приёмка владения требует ДВУХ живых Хостов с
ОДНИМ конфигом, которые одновременно держат MCP-транспорт: один владеет сеансом, второй — нет.
Один клиент такого состояния не создаёт, а `mcp-smoke.py` по устройству работает одним клиентом и
не может ответить на вопрос «что видит второй чат, пока первый владеет».

Что здесь измеряется (нумерация — из наряда `MCP_SESSION_RELEASE_DEVELOPER_PROMPT.md` §«Проверки`):
 1. A владеет, B инициализируется и видит инструменты; acquire B → SESSION_OWNER_ACTIVE, status B объясняет причину.
 2. A release → B acquire → CAD-вызов B.
 3. Прежний CAD-вызов A отказывает без COM и без записи в журнал.
 4. Затем B release → A acquire в том же MCP-транспорте; старые ссылки не принимаются.
 5. Два одновременных acquire: ровно один владелец; повторные release не портят состояние.
 6. Несохранённый документ: release отказывает DOCUMENT_DIRTY, после явного сохранения проходит.
 7. Повтор operation_id после передачи: журнал воспроизводит записанный исход, мутация не повторяется.
 8. Политика экземпляра КОМПАС: launched завершается документированным Quit(), attached — остаётся запущенным.
 9. Потеря транспорта владельца: статус согласован, сеанс можно занять снова.

Запуск:

    python scripts/session-handover-check.py
    python scripts/session-handover-check.py --host-exe <путь к KompasMcp.Host.exe>

Отчёт: `docs/acceptance/session-handover/<метка>/report.md` (+ `report.json`). Каталог приёмки в
коммит не идёт (правило `.gitignore` §5), поэтому отчёт — локальное доказательство, а не публикация.
"""

from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import os
import subprocess
import sys
import threading
import time
import uuid

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from localconfig import isolate_config, local_config  # noqa: E402


class McpError(Exception):
    pass


class Client:
    """Один MCP-клиент = отдельный процесс Host с общим конфигом."""

    def __init__(self, host_exe, config, tag):
        self.tag = tag
        self.p = subprocess.Popen(
            [host_exe, "--config", config],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8")
        self.n = 0
        self.err_lines = []
        self._lock = threading.Lock()
        self._stderr_thread = threading.Thread(target=self._pump_stderr, daemon=True)
        self._stderr_thread.start()

    def _pump_stderr(self):
        try:
            while True:
                line = self.p.stderr.readline() if self.p.stderr else ""
                if not line:
                    return
                self.err_lines.append(line.rstrip())
        except Exception:
            return

    def _write(self, msg):
        with self._lock:
            assert self.p.stdin is not None
            self.p.stdin.write(json.dumps(msg, ensure_ascii=False) + "\n")
            self.p.stdin.flush()

    def _read(self, want_id, timeout):
        deadline = time.time() + timeout
        while time.time() < deadline:
            line = self.p.stdout.readline() if self.p.stdout else ""
            if not line:
                if self.p.poll() is not None:
                    raise McpError(f"[{self.tag}] host exited rc={self.p.returncode}; stderr: {self.tail()}")
                time.sleep(0.02)
                continue
            line = line.strip()
            if not line:
                continue
            try:
                msg = json.loads(line)
            except json.JSONDecodeError:
                continue
            if msg.get("id") == want_id:
                return msg
        raise McpError(f"[{self.tag}] timeout waiting for id={want_id}")

    def call(self, method, params=None, timeout=180):
        with self._lock:
            self.n += 1
            me = self.n
        msg = {"jsonrpc": "2.0", "id": me, "method": method}
        if params is not None:
            msg["params"] = params
        self._write(msg)
        reply = self._read(me, timeout)
        if "error" in reply:
            raise McpError(f"[{self.tag}] {method}: {reply['error']}")
        return reply["result"]

    def notify(self, method, params=None):
        msg = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            msg["params"] = params
        self._write(msg)

    def tool(self, name, arguments=None, timeout=180):
        """tools/call → конверт (structuredContent)."""
        result = self.call("tools/call", {"name": name, "arguments": arguments or {}}, timeout=timeout)
        return result.get("structuredContent") or {}

    def initialize(self):
        self.call("initialize", {
            "protocolVersion": "2025-06-18",
            "capabilities": {},
            "clientInfo": {"name": f"session-handover-{self.tag}", "version": "0"},
        }, timeout=90)
        self.notify("notifications/initialized")

    def tools(self):
        return [t.get("name") for t in self.call("tools/list", {}, timeout=60).get("tools", [])]

    def tail(self):
        return "\n".join(self.err_lines[-20:])

    def close(self):
        """Закрыть транспорт: для Хоста это сигнал «клиент ушёл»."""
        try:
            if self.p.stdin:
                self.p.stdin.close()
            self.p.wait(timeout=20)
        except Exception:
            self.p.kill()


def tool_wait(client, name, arguments, poll_seconds=240):
    """Вызов мутации с повтором тем же operation_id, пока операция в статусе running.

    Зачем. Холодный запуск КОМПАС длиннее `sync_budget_ms` из конфигурации, поэтому первая
    мутация отвечает `status: running` — это документированное поведение (spec 1.8), а не отказ.
    Повтор тем же `operation_id` возвращает записанный исход. Прибор, считавший `running` за FAIL,
    измерил бы не продукт, а собственное нетерпение.
    """
    env = client.tool(name, arguments, timeout=300)
    waited = 0
    while status(env) == "running" and waited < poll_seconds:
        time.sleep(5)
        waited += 5
        env = client.tool(name, arguments, timeout=300)
    return env


# -------------------------------------------------------------------------------------------------
# Разбор конвертов
# -------------------------------------------------------------------------------------------------

def code(env):
    return ((env or {}).get("error") or {}).get("code")


def status(env):
    return (env or {}).get("status")


def result(env):
    return (env or {}).get("result") or {}


def warnings(env):
    return (env or {}).get("warnings") or []


def field(env, *path):
    node = env or {}
    for key in path:
        if not isinstance(node, dict):
            return None
        node = node.get(key)
    return node


# -------------------------------------------------------------------------------------------------
# Отчёт
# -------------------------------------------------------------------------------------------------

class Report:
    def __init__(self, title, path):
        self.title = title
        self.path = path
        self.rows = []

    def add(self, rid, desc, verdict, detail="", facts=None):
        self.rows.append({
            "id": rid, "desc": desc, "verdict": verdict, "detail": detail,
            "facts": facts or {}, "ts": datetime.datetime.now().isoformat(timespec="seconds"),
        })
        print(f"  {verdict:5s} {rid}: {desc}" + (f" — {detail}" if detail else ""))

    def save(self):
        os.makedirs(os.path.dirname(self.path), exist_ok=True)
        with open(self.path + ".json", "w", encoding="utf-8") as fh:
            json.dump(self.rows, fh, ensure_ascii=False, indent=2)
        failed = [r for r in self.rows if r["verdict"] == "FAIL"]
        skipped = [r for r in self.rows if r["verdict"] == "SKIP"]
        lines = [f"# {self.title}", ""]
        lines.append(f"Итог: проверок {len(self.rows)}, FAIL {len(failed)}, SKIP {len(skipped)}.")
        lines.append("")
        lines.append("| № | Проверка | Вердикт | Измерено |")
        lines.append("|---|---|---|---|")
        for r in self.rows:
            lines.append(f"| {r['id']} | {r['desc']} | **{r['verdict']}** | {r['detail']} |")
        lines.append("")
        lines.append("## Подробности")
        lines.append("")
        for r in self.rows:
            lines.append(f"### {r['id']} — {r['desc']}")
            lines.append("")
            lines.append(f"Вердикт: **{r['verdict']}**")
            if r["detail"]:
                lines.append("")
                lines.append(r["detail"])
            if r["facts"]:
                lines.append("")
                lines.append("```json")
                lines.append(json.dumps(r["facts"], ensure_ascii=False, indent=2))
                lines.append("```")
            lines.append("")
        with open(self.path + ".md", "w", encoding="utf-8") as fh:
            fh.write("\n".join(lines))
        return len(failed)


# -------------------------------------------------------------------------------------------------
# Вспомогательное
# -------------------------------------------------------------------------------------------------

def save_document(client, document_id, path):
    """Сохранить документ явным вызовом: без этого release обязан отказать DOCUMENT_DIRTY."""
    if os.path.exists(path):
        os.remove(path)
    ctx = client.tool("kompas_get_context", {"document_id": document_id, "detail": "minimal"})
    revision = field(ctx, "result", "revision") or field(ctx, "revision_after")
    return tool_wait(client, "kompas_save_document", {
        "document_id": document_id,
        "expected_revision": revision,
        "target_path": path,
        "operation_id": str(uuid.uuid4()),
    })


def journal_lines(path):
    if not os.path.isfile(path):
        return 0
    with open(path, encoding="utf-8", errors="replace") as fh:
        return sum(1 for line in fh if line.strip())


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def kompas_pids():
    """pid запущенных экземпляров КОМПАС (tasklist, без обращения к COM)."""
    try:
        out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq KOMPAS.exe", "/FO", "CSV", "/NH"],
                             capture_output=True, text=True, timeout=30).stdout
    except Exception:
        return []
    pids = []
    for line in out.splitlines():
        parts = [p.strip('"') for p in line.split(",")]
        if len(parts) >= 2 and parts[0].lower() == "kompas.exe":
            try:
                pids.append(int(parts[1]))
            except ValueError:
                continue
    return pids


def user_kompas_pids():
    """Экземпляры, НЕ запущенные этим прибором: их сравнивают со снимком на старте."""
    return kompas_pids()


KOMPAS_EXE = "D:/Programs/KOMPAS-3Dv24/Bin/KOMPAS.exe"


def start_attachable_kompas():
    """Поднять экземпляр КОМПАС, который сервер увидит как ATTACHED.

    Зачем прибору запускать КОМПАС самому. Политика «attached не завершается» проверяется только
    на экземпляре, который сервер НЕ запускал: если его поднял `mode=launch`, он помечен Launched, и
    `Quit()` для него — правильное поведение, а не то, которое проверяется. Процесс, поднятый
    прибором ДО первого `kompas_connect`, для сервера ничем не отличается от пользовательского: он
    приходит из ROT и регистрируется как Attached.

    Почему это не «трогать чужой КОМПАС»: процесс принадлежит прибору, прибор его же и закрывает
    (см. cleanup ниже), а пользовательские экземпляры прибор не запускает и не останавливает.
    Возвращает None, если путь установки не найден, — тогда строка остаётся SKIP с причиной.
    """
    if not os.path.isfile(KOMPAS_EXE):
        return None
    return subprocess.Popen([KOMPAS_EXE], close_fds=True)


def attach_with_retry(client, process_id, attempts=24, pause=5):
    """Подключиться attach по явному pid, дожидаясь регистрации экземпляра в ROT.

    Экземпляр регистрируется в ROT не мгновенно, и до этого attach отвечает AMBIGUOUS_APPLICATION
    («нет процесса N среди наблюдаемых»). Это состояние ожидания, а не отказ продукта — но КАЖДАЯ
    попытка идёт с НОВЫМ operation_id: повтор того же id вернул бы записанный отказ журнала, и прибор
    вечно читал бы первый неудачный ответ.
    """
    env = None
    op = str(uuid.uuid4())
    for _ in range(attempts):
        env = client.tool("kompas_connect", {
            "mode": "attach",
            "process_id": process_id,
            "make_visible": False,
            "operation_id": op,
        })
        if status(env) == "succeeded":
            return env
        if status(env) == "running":
            # Операция идёт: повтор с ТЕМ ЖЕ operation_id вернёт записанный исход и НЕ отправит
            # вторую команду (это же правило проверяет строка S15). Новый id здесь начал бы
            # подключение заново — то есть создал бы вторую регистрацию того же экземпляра.
            time.sleep(pause)
            continue
        if code(env) == "AMBIGUOUS_APPLICATION":
            # Экземпляр ещё не зарегистрирован в ROT: это ожидание, а не отказ. Здесь нужен НОВЫЙ
            # operation_id — повтор прежнего вернул бы записанный отказ журнала и прибор читал бы
            # его вечно.
            time.sleep(pause)
            op = str(uuid.uuid4())
            continue
        return env
    return env


# -------------------------------------------------------------------------------------------------
# Сценарий
# -------------------------------------------------------------------------------------------------

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host-exe", default=os.path.join(
        ROOT, "src", "KompasMcp.Host", "bin", "x64", "Release", "net10.0-windows", "KompasMcp.Host.exe"))
    ap.add_argument("--config", default=None)
    ap.add_argument("--out", default=None)
    args = ap.parse_args()

    host_exe = args.host_exe
    if not os.path.isfile(host_exe):
        raise SystemExit(f"не найден Host: {host_exe}")

    config = args.config or local_config(ROOT)
    base = os.path.join(ROOT, "scratch", "session-handover")
    os.makedirs(base, exist_ok=True)
    isolated = isolate_config(config, base_dir=base, prefix="handover")

    # АСИНХРОННЫЙ ПУТЬ ДЕЛАЕТСЯ ДЕТЕРМИНИРОВАННЫМ. Правило «освобождение при выполняющейся
    # операции не передаёт владение» относится к состоянию, которое возникает только когда мутация
    # не укладывается в `sync_budget_ms` и отвечает `running`. На холодном запуске это случается, на
    # тёплом — нет, и прибор, надеявшийся на случай, измерил бы удачу. Бюджет снижается до 1 с:
    # тогда «работа после ответа» возникает всегда, и правило проверяется, а не угадывается.
    with open(isolated, encoding="utf-8") as fh:
        isolated_data = json.load(fh)
    isolated_data["sync_budget_ms"] = 1000
    with open(isolated, "w", encoding="utf-8") as fh:
        json.dump(isolated_data, fh, ensure_ascii=False, indent=2)
    journal = isolated_data["journal_path"]

    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    out_dir = args.out or os.path.join(ROOT, "docs", "acceptance", "session-handover", stamp)
    rep = Report(f"Передача CAD-сеанса между двумя независимыми MCP-клиентами · {stamp}",
                 os.path.join(out_dir, "report"))

    print(f"Host: {host_exe} (sha256 {sha256(host_exe)[:16]})")
    print(f"Конфиг: {isolated}")
    print(f"Журнал: {journal}")

    a = Client(host_exe, isolated, "A")
    b = Client(host_exe, isolated, "B")
    c = None
    started = datetime.datetime.now()
    foreign_kompas = set(user_kompas_pids())
    print(f"Чужие экземпляры КОМПАС на старте: {sorted(foreign_kompas) or 'нет'}")

    probe_instance = None
    try:
        # ---------------------------------------------------------------- S01
        a.initialize()
        b.initialize()
        tools_a = a.tools()
        tools_b = b.tools()
        need = {"kompas_session_status", "kompas_acquire_session", "kompas_release_session"}
        ok = len(tools_a) == len(tools_b) and need <= set(tools_b)
        rep.add("S01", "Оба клиента проходят initialize и видят каталог, включая инструменты сеанса",
                "PASS" if ok else "FAIL",
                f"инструментов A {len(tools_a)}, B {len(tools_b)}; сеансовые в каталоге B: "
                f"{sorted(need & set(tools_b))}",
                {"tools_a": len(tools_a), "tools_b": len(tools_b),
                 "session_tools_present": sorted(need & set(tools_b))})

        # ---------------------------------------------------------------- S02
        env = a.tool("kompas_acquire_session")
        gen_a1 = field(env, "result", "generation")
        ok_a = status(env) == "succeeded" and gen_a1
        rep.add("S02", "A занимает сеанс явно и получает поколение",
                "PASS" if ok_a else "FAIL", f"status={status(env)}, поколение {gen_a1}", env)

        st_b = b.tool("kompas_session_status")
        state_b = field(st_b, "result", "session_state")
        acq_b = b.tool("kompas_acquire_session")
        code_b = code(acq_b)
        ok = ok_a and state_b == "owned_by_other" and code_b == "SESSION_OWNER_ACTIVE"
        rep.add("S03", "B видит занятый сеанс: status объясняет причину, acquire отвечает SESSION_OWNER_ACTIVE",
                "PASS" if ok else "FAIL",
                f"status B: session_state={state_b}, can_acquire={field(st_b, 'result', 'can_acquire')}; "
                f"acquire B: {code_b}",
                {"status_b": st_b, "acquire_b": acq_b})

        # ---------------------------------------------------------------- S04
        # Запуск КОМПАС — длинная операция: первый ответ часто `running`. Именно этим состоянием
        # измеряется правило «освобождение при выполняющейся операции не передаёт владение».
        connect_args = {"mode": "launch", "make_visible": False, "operation_id": str(uuid.uuid4())}
        conn = a.tool("kompas_connect", connect_args, timeout=300)
        running_first = status(conn) == "running"

        if not running_first:
            rep.add("S03b", "release при выполняющейся операции КОМПАС отклоняется SESSION_RELEASE_BUSY",
                    "SKIP",
                    f"не измерено: connect ответил {status(conn)} в пределах sync_budget_ms, "
                    "состояние «работа после ответа» не возникло",
                    {"connect_first": conn})
        else:
            rel_busy = a.tool("kompas_release_session")
            in_flight = field(rel_busy, "error", "details", "operations_in_flight")
            busy_ok = code(rel_busy) == "SESSION_RELEASE_BUSY"
            rep.add("S03b", "release при выполняющейся операции КОМПАС отклоняется SESSION_RELEASE_BUSY",
                    "PASS" if busy_ok else "FAIL",
                    f"первый ответ connect: {status(conn)}; release: {code(rel_busy)}, операций в работе "
                    f"{in_flight}; владение осталось у A",
                    {"connect_first": conn, "release": rel_busy})

        conn = tool_wait(a, "kompas_connect", connect_args) if running_first else conn
        app_id = field(conn, "result", "application_id") or field(conn, "application_id")
        doc = tool_wait(a, "kompas_create_document", {"application_id": app_id, "kind": "part", "operation_id": str(uuid.uuid4())})
        doc_id = field(doc, "result", "document_id") or field(doc, "result", "id")
        revision = field(doc, "result", "revision")
        op_rebuild = str(uuid.uuid4())
        reb = tool_wait(a, "kompas_rebuild", {"document_id": doc_id, "operation_id": op_rebuild})
        ok = app_id and doc_id and status(reb) == "succeeded"
        rep.add("S04", "A работает с моделью: connect(launch) → create_document → rebuild",
                "PASS" if ok else "FAIL",
                f"application_id {app_id}, document_id {doc_id}, revision {revision}, rebuild {status(reb)}",
                {"connect": conn, "create": doc, "rebuild": reb, "operation_id": op_rebuild})

        health_b = b.tool("kompas_health")
        ok = status(health_b) == "succeeded" and field(health_b, "result", "cad_channel") == "not_started"
        rep.add("S05", "Диагностика B отвечает без владения и без запуска Worker",
                "PASS" if ok else "FAIL",
                f"kompas_health B: status={status(health_b)}, cad_channel="
                f"{field(health_b, 'result', 'cad_channel')}",
                {"health_b": health_b})

        # ---------------------------------------------------------------- S05: несохранённый документ
        rel_dirty = a.tool("kompas_release_session")
        documents = field(rel_dirty, "error", "details", "documents") or []
        ok = code(rel_dirty) == "DOCUMENT_DIRTY" and len(documents) >= 1
        rep.add("S06", "release при несохранённом документе отказывает DOCUMENT_DIRTY и называет документы",
                "PASS" if ok else "FAIL",
                f"код {code(rel_dirty)}, документов в ответе {len(documents)}",
                {"release": rel_dirty})

        st_after_refusal = a.tool("kompas_session_status")
        still_owner = field(st_after_refusal, "result", "session_state") == "owned_by_self"
        rep.add("S07", "Отказ до очистки возвращает owned: A остаётся владельцем",
                "PASS" if still_owner else "FAIL",
                f"session_state A после отказа: {field(st_after_refusal, 'result', 'session_state')}",
                {"status_a": st_after_refusal})

        target = os.path.join(os.path.dirname(journal), "handover-part.m3d")
        if os.path.exists(target):
            os.remove(target)
        # Ревизия берётся ПОСЛЕ перестроения: rebuild поднимает её, и сохранение с прежней ревизией
        # ответило бы REVISION_CONFLICT — отказом контракта, а не дефектом освобождения.
        revision_after = field(reb, "revision_after") or field(reb, "result", "revision") or revision
        saved = tool_wait(a, "kompas_save_document", {
            "document_id": doc_id, "expected_revision": revision_after, "target_path": target,
            "operation_id": str(uuid.uuid4())})
        saved_ok = status(saved) == "succeeded" and os.path.isfile(target)
        rep.add("S08", "Явное сохранение документа проходит, файл на диске есть",
                "PASS" if saved_ok else "FAIL",
                f"ревизия сохранения {revision_after}, status={status(saved)}, файл {target} ({os.path.getsize(target) if os.path.exists(target) else 0} Б)",
                {"save": saved, "target": target,
                 "sha256": sha256(target) if os.path.exists(target) else None})

        rel_a = a.tool("kompas_release_session")
        ok = status(rel_a) == "succeeded" and field(rel_a, "result", "released_by_this_request") is True
        rep.add("S09", "После явного сохранения release проходит и подтверждает остановку Worker",
                "PASS" if ok else "FAIL",
                f"status={status(rel_a)}, released_by_this_request="
                f"{field(rel_a, 'result', 'released_by_this_request')}, worker="
                f"{field(rel_a, 'result', 'worker')}",
                {"release": rel_a})

        # Политика launched: собственный экземпляр завершён документированным Quit().
        time.sleep(2)
        after_release = set(kompas_pids()) - foreign_kompas
        rep.add("S10", "Собственный (launched) экземпляр КОМПАС завершён освобождением сеанса",
                "PASS" if not after_release else "FAIL",
                f"процессов КОМПАС, порождённых прибором, после release: {sorted(after_release) or 'нет'}",
                {"kompas_after_release": sorted(after_release)})

        # ---------------------------------------------------------------- S06: B занимает сеанс
        st_b = b.tool("kompas_session_status")
        requires_explicit = field(st_b, "result", "requires_explicit_acquire")
        cad_b = b.tool("kompas_list_documents", {"application_id": "0" * 32})
        code_cad = code(cad_b)
        ok = requires_explicit is True and code_cad == "SESSION_NOT_ACQUIRED"
        rep.add("S11", "После явного release обычный CAD-вызов не берёт владение: SESSION_NOT_ACQUIRED",
                "PASS" if ok else "FAIL",
                f"requires_explicit_acquire={requires_explicit}, kompas_list_documents B: {code_cad}",
                {"status_b": st_b, "cad_b": cad_b})

        env = b.tool("kompas_acquire_session")
        gen_b = field(env, "result", "generation")
        ok = status(env) == "succeeded" and gen_b and gen_b != gen_a1
        rep.add("S12", "B занимает сеанс явно и получает НОВОЕ поколение",
                "PASS" if ok else "FAIL",
                f"поколение A было {gen_a1}, поколение B {gen_b}",
                {"acquire_b": env, "generation_a": gen_a1, "generation_b": gen_b})

        # ---------------------------------------------------------------- S07: прежний вызов A
        before = journal_lines(journal)
        old_call = a.tool("kompas_get_context", {"document_id": doc_id, "detail": "minimal"})
        after = journal_lines(journal)
        ok = code(old_call) in ("SESSION_NOT_ACQUIRED", "SESSION_OWNER_ACTIVE") and after == before
        rep.add("S13", "Прежний CAD-вызов A отказывается без COM и без записи в журнал",
                "PASS" if ok else "FAIL",
                f"код {code(old_call)}; строк в журнале до {before}, после {after}",
                {"call": old_call, "journal_before": before, "journal_after": after})

        # ---------------------------------------------------------------- S08: новый контекст B
        conn_b = tool_wait(b, "kompas_connect", {"mode": "launch", "make_visible": False, "operation_id": str(uuid.uuid4())})
        app_b = field(conn_b, "result", "application_id") or field(conn_b, "application_id")
        doc_b = tool_wait(b, "kompas_create_document", {"application_id": app_b, "kind": "part", "operation_id": str(uuid.uuid4())})
        doc_b_id = field(doc_b, "result", "document_id") or field(doc_b, "result", "id")
        stale = b.tool("kompas_get_context", {"document_id": doc_id, "detail": "minimal"})
        ok = bool(doc_b_id) and status(stale) == "failed"
        rep.add("S14", "Новый контекст: B создаёт свой документ, ссылка прежнего сеанса не принимается",
                "PASS" if ok else "FAIL",
                f"новый document_id {doc_b_id}; чтение прежнего {doc_id}: {code(stale)}",
                {"connect_b": conn_b, "create_b": doc_b, "stale_read": stale})

        # ---------------------------------------------------------------- S09: повтор operation_id
        replay = b.tool("kompas_rebuild", {"document_id": doc_id, "operation_id": op_rebuild})
        replayed = any("Запись журнальная" in str(w) for w in warnings(replay))
        ok = status(replay) == "succeeded" and replayed
        rep.add("S15", "Повтор operation_id прежнего владельца воспроизводит записанный исход, мутация не повторяется",
                "PASS" if ok else "FAIL",
                f"status={status(replay)}, признак воспроизведения: {replayed}",
                {"replay": replay})

        # ---------------------------------------------------------------- S10: обратная передача
        # Освобождение требует отсутствия несохранённых документов: сохраняем явным вызовом, а не
        # ждём, что сервер «как-нибудь» закроет документ.
        saved_b = save_document(b, doc_b_id, os.path.join(os.path.dirname(journal), "handover-part-b.m3d"))
        rel_b = b.tool("kompas_release_session")
        acq_a2 = a.tool("kompas_acquire_session")
        gen_a2 = field(acq_a2, "result", "generation")
        ok = status(rel_b) == "succeeded" and status(acq_a2) == "succeeded" and gen_a2 not in (gen_a1,)
        rep.add("S16", "B release → A acquire в том же MCP-транспорте: новое поколение у прежнего владельца",
                "PASS" if ok else "FAIL",
                f"release B {status(rel_b)}, acquire A {status(acq_a2)}, поколения A: {gen_a1} → {gen_a2}",
                {"release_b": rel_b, "acquire_a": acq_a2})

        conn_a2 = tool_wait(a, "kompas_connect", {"mode": "launch", "make_visible": False, "operation_id": str(uuid.uuid4())})
        app_a2 = field(conn_a2, "result", "application_id") or field(conn_a2, "application_id")
        doc_a2 = tool_wait(a, "kompas_create_document", {"application_id": app_a2, "kind": "part", "operation_id": str(uuid.uuid4())})
        doc_a2_id = field(doc_a2, "result", "document_id") or field(doc_a2, "result", "id")
        stale2 = a.tool("kompas_get_context", {"document_id": doc_b_id, "detail": "minimal"})
        ok = bool(doc_a2_id) and status(stale2) == "failed"
        rep.add("S17", "После обратной передачи A работает новым контекстом; документ сеанса B недоступен",
                "PASS" if ok else "FAIL",
                f"document_id A {doc_a2_id}; чтение документа B {doc_b_id}: {code(stale2)}",
                {"connect_a2": conn_a2, "create_a2": doc_a2, "stale_read": stale2})

        # Документ A тоже сохраняется: иначе release откажет, и конкуренция измерялась бы на
        # незанятом сеансе.
        saved_a2 = save_document(a, doc_a2_id, os.path.join(os.path.dirname(journal), "handover-part-a2.m3d"))
        st_a2 = a.tool("kompas_session_status")
        rep.add("S17b", "Оба документа сеанса сохранены явными вызовами перед передачей",
                "PASS" if status(saved_b) == "succeeded" and status(saved_a2) == "succeeded" else "FAIL",
                f"сохранение B: {status(saved_b)}, сохранение A: {status(saved_a2)}",
                {"save_b": saved_b, "save_a": saved_a2})

        # ---------------------------------------------------------------- S11: конкуренция
        c = Client(host_exe, isolated, "C")
        c.initialize()
        acq_c = c.tool("kompas_acquire_session")
        ok = code(acq_c) == "SESSION_OWNER_ACTIVE"
        rep.add("S18", "Третий клиент при живом владельце получает SESSION_OWNER_ACTIVE, а не владение",
                "PASS" if ok else "FAIL", f"код {code(acq_c)}", {"acquire_c": acq_c})

        # Владелец известен из предыдущей строки: A занял сеанс и работал с моделью.
        rel_a = a.tool("kompas_release_session")
        outcomes = {}
        gate = threading.Barrier(2)

        def race(client, key):
            gate.wait()
            try:
                outcomes[key] = client.tool("kompas_acquire_session")
            except Exception as ex:  # noqa: BLE001
                outcomes[key] = {"status": "error", "error": {"code": str(ex)}}

        threads = [threading.Thread(target=race, args=(a, "A")), threading.Thread(target=race, args=(c, "C"))]
        for t in threads:
            t.start()
        for t in threads:
            t.join()
        winners = [k for k, v in outcomes.items() if status(v) == "succeeded"]
        loser_code = [code(v) for k, v in outcomes.items() if status(v) != "succeeded"]
        ok = status(rel_a) == "succeeded" and len(winners) == 1 and "SESSION_OWNER_ACTIVE" in loser_code
        rep.add("S19", "Два одновременных acquire: ровно один владелец",
                "PASS" if ok else "FAIL",
                f"победитель {winners}, код проигравшего {loser_code}",
                {"outcomes": {k: {"status": status(v), "code": code(v)} for k, v in outcomes.items()}})

        # Повторные release не портят состояние. Освобождает ПОБЕДИТЕЛЬ гонки: он владелец.
        winner_client = a if winners and winners[0] == "A" else c
        watcher = c if winner_client is a else a
        rep_release = winner_client.tool("kompas_release_session")
        rep_release2 = winner_client.tool("kompas_release_session")
        ok = (status(rep_release) == "succeeded"
              and field(rep_release, "result", "released_by_this_request") is True
              and status(rep_release2) == "succeeded"
              and field(rep_release2, "result", "released_by_this_request") is False)
        rep.add("S20", "Повторный release безопасен и различает «освобождён этим запросом» и «уже освобождён»",
                "PASS" if ok else "FAIL",
                f"первый: released_by_this_request={field(rep_release, 'result', 'released_by_this_request')}; "
                f"второй: {field(rep_release2, 'result', 'released_by_this_request')}",
                {"first": rep_release, "second": rep_release2, "owner": winner_client.tag})

        # ---------------------------------------------------------------- S12: потеря транспорта
        # Владелец снова занимает сеанс, и его транспорт закрывается без освобождения: Хост обязан
        # отдать сеанс тем же механизмом, что и при явном release, а не оставить «вечного владельца».
        acq_again = winner_client.tool("kompas_acquire_session")
        winner_client.close()
        time.sleep(3)
        st = watcher.tool("kompas_session_status")
        state = field(st, "result", "session_state")
        ok = status(acq_again) == "succeeded" and state in ("free", "released")
        rep.add("S21", "Потеря транспорта владельца: сеанс становится свободным, состояние названо",
                "PASS" if ok else "FAIL",
                f"закрыт транспорт {winner_client.tag}; session_state у наблюдателя: {state}, "
                f"can_acquire={field(st, 'result', 'can_acquire')}",
                {"status": st, "closed_client": winner_client.tag, "acquire_before_close": acq_again})

        env = watcher.tool("kompas_acquire_session")
        ok = status(env) == "succeeded"
        rep.add("S22", "После потери транспорта сеанс занимается снова другим клиентом",
                "PASS" if ok else "FAIL", f"status={status(env)}", {"acquire": env})

        # ---------------------------------------------------------------- S13: attached КОМПАС
        # Пользовательский экземпляр, если он есть, используется как есть и НЕ закрывается прибором.
        # Если его нет — прибор поднимает свой, который сервер видит ровно так же: пришедшим из ROT,
        # то есть Attached. Это и делает проверку исполнимо́й без ожидания пользователя.
        attach_pid = None
        source = ""
        if foreign_kompas:
            attach_pid = sorted(foreign_kompas)[0]
            source = "экземпляр пользователя, запущенный до прибора (прибор его не закрывает)"
        else:
            probe_instance = start_attachable_kompas()
            if probe_instance is not None:
                attach_pid = probe_instance.pid
                source = "экземпляр, поднятый САМИМ прибором до первого connect (для сервера — attached)"

        if attach_pid is None:
            rep.add("S23", "Освобождение сеанса НЕ завершает attached КОМПАС", "SKIP",
                    f"не измерено: {KOMPAS_EXE} не найден, поднять экземпляр нечем",
                    {"kompas_exe": KOMPAS_EXE})
        else:
            attach = attach_with_retry(watcher, attach_pid)
            attached_ok = status(attach) == "succeeded" and field(attach, "result", "ownership") == "attached"
            rep.add("S23", "Сеанс подключается к СТОРОННЕМУ экземпляру как attached, а не launched",
                    "PASS" if attached_ok else "FAIL",
                    f"attach pid {attach_pid}: {status(attach)}, ownership="
                    f"{field(attach, 'result', 'ownership')}; источник — {source}",
                    {"attach": attach, "attach_pid": attach_pid, "source": source})

            doc_att = None
            doc_att_id = None
            save_att = None
            if attached_ok:
                app_att = field(attach, "result", "application_id") or field(attach, "application_id")
                doc_att = tool_wait(watcher, "kompas_create_document", {
                    "application_id": app_att, "kind": "part", "operation_id": str(uuid.uuid4())})
                doc_att_id = field(doc_att, "result", "document_id") or field(doc_att, "result", "id")
                # Документ сохраняется ЯВНО: несохранённый — законный отказ release, и проверка
                # «attached не завершается» не должна подменяться проверкой политики несохранённых
                # документов (измерено в первом прогоне этой строки: release ответил DOCUMENT_DIRTY).
                save_att = save_document(watcher, doc_att_id,
                                         os.path.join(os.path.dirname(journal), "handover-attached.m3d"))

            rel_att = watcher.tool("kompas_release_session")
            alive = attach_pid in kompas_pids()
            ok = attached_ok and status(save_att) == "succeeded" and status(rel_att) == "succeeded" and alive
            rep.add("S24", "Освобождение сеанса НЕ завершает attached КОМПАС",
                    "PASS" if ok else "FAIL",
                    f"сохранение {status(save_att)}, release {status(rel_att)}; процесс pid {attach_pid} "
                    f"после освобождения: {'жив' if alive else 'ЗАВЕРШЁН'}; документ сеанса был {doc_att_id}",
                    {"save": save_att, "release": rel_att, "attach_pid": attach_pid, "alive": alive,
                     "create": doc_att, "kompas_alive": sorted(kompas_pids())})

            # Документ сеанса закрыт, а сам экземпляр по-прежнему доступен для attach: это разные
            # утверждения, и «не завершили процесс» ещё не значит «отпустили его корректно».
            # Сеанс занимается ЯВНО: после явного release обычный CAD-вызов владение не берёт, и
            # attach без acquire вернул бы SESSION_NOT_ACQUIRED — это правило продукта, а не отказ
            # экземпляра (измерено в предыдущем прогоне этой строки).
            reacquire = watcher.tool("kompas_acquire_session")
            reattach = attach_with_retry(watcher, attach_pid, attempts=12) if status(reacquire) == "succeeded" else reacquire
            docs = None
            stale_doc = None
            open_docs = None
            if status(reattach) == "succeeded":
                app_re = field(reattach, "result", "application_id") or field(reattach, "application_id")
                open_docs = field(reattach, "result", "open_document_count")
                docs = watcher.tool("kompas_list_documents", {"application_id": app_re})
                # Документ прежнего сеанса в НОВОМ сеансе недоступен: это и есть «контекст не оживает».
                stale_doc = watcher.tool("kompas_get_context", {"document_id": doc_att_id, "detail": "minimal"})
                reattach_ok = (status(reacquire) == "succeeded" and status(docs) == "succeeded"
                               and status(stale_doc) == "failed" and open_docs == 0)
            else:
                reattach_ok = False
            rep.add("S25", "Экземпляр снова доступен, документов в нём нет, ссылка прежнего сеанса не принимается",
                    "PASS" if reattach_ok else "FAIL",
                    f"повторный acquire: {status(reacquire)}; повторный attach: {status(reattach)}; "
                    f"открытых документов в экземпляре "
                    f"{open_docs}; kompas_list_documents: {status(docs)}; чтение документа прежнего "
                    f"сеанса {doc_att_id}: {code(stale_doc)}",
                    {"reacquire": reacquire, "reattach": reattach, "documents": docs,
                     "stale_read": stale_doc, "open_document_count": open_docs})

            # Оставшийся после проверки сеанс закрывается: экземпляр, поднятый прибором, прибор и
            # закрывает. Пользовательский экземпляр не трогается вовсе.
            if status(reattach) == "succeeded":
                watcher.tool("kompas_disconnect", {
                    "application_id": field(reattach, "result", "application_id") or field(reattach, "application_id"),
                    "close_owned_application": False,
                    "operation_id": str(uuid.uuid4())})
                watcher.tool("kompas_release_session")

            if probe_instance is not None:
                time.sleep(2)
                subprocess.run(["taskkill", "/PID", str(probe_instance.pid), "/F"],
                               capture_output=True, text=True, timeout=60)
                time.sleep(2)
                gone = probe_instance.pid not in kompas_pids()
                rep.add("S26", "Прибор закрыл СВОЙ экземпляр КОМПАС; пользовательский не тронут",
                        "PASS" if gone else "FAIL",
                        f"pid {probe_instance.pid} после закрытия прибором: {'нет' if gone else 'ЖИВ'}",
                        {"probe_pid": probe_instance.pid, "gone": gone,
                         "user_instances": sorted(foreign_kompas)})
            else:
                rep.add("S26", "Пользовательский экземпляр не тронут прибором", "PASS",
                        f"прибор не запускал и не закрывал ничего; пользовательских экземпляров "
                        f"{len(foreign_kompas)}, все на месте: "
                        f"{sorted(set(foreign_kompas) & set(kompas_pids()))}",
                        {"user_instances": sorted(foreign_kompas)})

        # ---------------------------------------------------------------- Журнал
        lines = journal_lines(journal)
        rep.add("S27", "Журнал операций не повреждён после передачи сеанса",
                "PASS" if lines > 0 else "FAIL",
                f"строк в {journal}: {lines}",
                {"journal": journal, "lines": lines})

    finally:
        for client in (a, b, c):
            if client is not None:
                try:
                    client.close()
                except Exception:
                    pass
        # Уборка: экземпляр, поднятый САМИМ прибором для проверки attached, закрывается прибором же
        # и на аварийном пути тоже — иначе упавший прибор оставил бы за собой чужой процесс.
        # Пользовательские экземпляры не трогаются ни здесь, ни где-либо ещё.
        if probe_instance is not None:
            try:
                subprocess.run(["taskkill", "/PID", str(probe_instance.pid), "/F"],
                               capture_output=True, text=True, timeout=60)
            except Exception:
                pass
        time.sleep(1)
        leftover = set(kompas_pids()) - foreign_kompas
        if leftover:
            print(f"ВНИМАНИЕ: остались процессы КОМПАС, порождённые прибором: {sorted(leftover)}")

    failed = rep.save()
    print(f"\nОтчёт: {rep.path}.md")
    print(f"Длительность: {(datetime.datetime.now() - started).total_seconds():.0f} с")
    print(f"Итог: FAIL {failed}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
