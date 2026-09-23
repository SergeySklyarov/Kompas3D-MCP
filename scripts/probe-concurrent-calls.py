"""Проба конкурентных вызовов: переживает ли транспорт несколько запросов в полёте.

Зачем отдельным прибором. `scripts/mcp-smoke.py` вызывает инструменты **по одному** и ждёт ответа
перед следующим вызовом. Живой клиент так не делает: он отправляет `kompas_health` и
`kompas_capabilities` подряд, не дожидаясь ответа. Ровно на этом 18.09.2026 вскрылся дефект
`IpcChannel.RequestAsync` — сериализованная запись при **незащищённом цикле чтения на каждого
вызывающего**: два вызова в полёте читали один канал Host↔Worker одновременно и переплетали байты.
Симптомы были `WORKER_UNRESPONSIVE — Недопустимая длина кадра 1919951483 байт` (это `0x7270227B`,
то есть сырые байты `{"pr`, прочитанные как префикс длины) и `JsonException: 'o' is an invalid start
of a value`.

665 строк приёмки этого класса не покрывали и покрыть не могли. Этот прибор покрывает его
напрямую: он отправляет N запросов **не дожидаясь ответов** и проверяет, что каждый получил свой
ответ и что ни один не пришёл отказом транспорта.

Проба НЕ заменяет приёмку: она проверяет один класс (несколько запросов в полёте), а не режимы B2.

ВАЖНО про сам прибор. Первая редакция (одноразовый `_probe_concurrent.py`) читала stdout **двумя
потоками** — то есть несла собственный дефект того же класса, что искала: гонку на общем потоке.
Здесь читатель **один**: все ответы читаются последовательно из главного потока, а разбираются по
`id`. Иначе прибор описывал бы себя, а не продукт.

Usage:
  python scripts/probe-concurrent-calls.py [--host <KompasMcp.Host.exe>]
      [--config <kompas-mcp.local.json>] [--calls 8] [--worker <KompasMcp.Worker.exe>]
      [--out scratch/mcp-smoke/delivery-20260918/concurrent-calls.json]

Журналы прибора изолированы по умолчанию: Host получает КОПИЮ конфигурации с перенаправленными
`log_path`/`worker_log_path`/`journal_path`/`artifact_directory` в `<каталог отчёта>/probe-logs/`.
Без этого прибор писал бы в `scratch/logs/host.jsonl` — в тот же файл, что и сервер, запущенный
клиентом, — и стирал бы журнал ровно того сеанса, который измеряет (измерено 18.09.2026).
`--keep-shared-logs` возвращает прежнее поведение; он нужен только для отладки самого прибора.

`--worker` задаёт KOMPAS_MCP_WORKER_PATH — нужен только чтобы воспроизвести дефект на прежней паре
бинарей (Host от старой поставки + старый Worker). По умолчанию переменная снимается намеренно:
заданная «на всякий случай», она увела бы Host на чужой Worker, и прибор измерял бы не то, что
запущено.
"""

import datetime
import json
import os
import queue
import subprocess
import sys
import threading

from localconfig import isolate_config as shared_isolate_config

for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_HOST = os.path.join(ROOT, "artifacts", "publish-b2-20260918-b2closed", "KompasMcp.Host.exe")
DEFAULT_CONFIG = os.path.join(ROOT, "config", "kompas-mcp.local.json")
DEFAULT_OUT = os.path.join(ROOT, "scratch", "mcp-smoke", "delivery-20260918", "concurrent-calls.json")

# Коды, которыми транспорт сообщает о СВОЁМ отказе. Любой из них в ответе на конкурентный вызов
# означает, что проверка не пройдена, даже если сам вызов формально вернул конверт.
TRANSPORT_FAILURES = ("WORKER_UNRESPONSIVE", "APPLICATION_DISCONNECTED", "OUTCOME_UNKNOWN", "QUEUE_FULL")


def argument(name, default=None):
    if name in sys.argv:
        index = sys.argv.index(name)
        if index + 1 < len(sys.argv):
            return sys.argv[index + 1]
    return default


def sha256_of(path):
    import hashlib

    digest = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def isolate_config(config, out):
    """Своя конфигурация для прибора: его журналы НЕ должны затирать журналы клиента.

    Зачем. Хост пишет в пути из конфигурации, а `config/kompas-mcp.local.json` указывает на
    `scratch/logs/host.jsonl` — ТОТ ЖЕ файл, куда пишет сервер, запущенный клиентом. Прогон этой
    пробы с такой конфигурацией обрезает ровно тот журнал, который документирует клиентский сеанс,
    ради которого проба и заведена.

    Измерено 18.09.2026: после прогонов через общую конфигурацию в `scratch/logs/host.jsonl`
    осталось 234 строки `Client (probe-concurrent-calls 0)` и НИ ОДНОЙ строки
    `Client (kompas 1.0.0)`. Журнал клиентского сеанса `bae7dc73` был уничтожен прибором, и
    причину остановки клиентского Хоста стало невозможно установить по журналу.

    Возвращает путь к копии конфигурации с перенаправленными `log_path`, `worker_log_path`,
    `journal_path`, `artifact_directory`. Сама исходная конфигурация не меняется.

    С 18.09.2026 реализация ОДНА на весь проект — `localconfig.isolate_config`: этот же дефект
    измерялся у четырёх приборов подряд (`probe-concurrent-calls.py`, `verify-client-entry.py`,
    `verify-delivery.py`, `mcp-smoke.py`), и четвёртая копия функции была бы четвёртым местом, где
    её можно забыть. Здесь остался только вызов с базой отчёта этого прибора.
    """
    return shared_isolate_config(config, os.path.dirname(out), "probe")


class Session:
    """Один Host по stdio. Читатель ровно один — см. предупреждение в шапке модуля."""

    def __init__(self, host, config, worker=None):
        env = dict(os.environ)
        env.pop("KOMPAS_MCP_WORKER_PATH", None)
        if worker:
            env["KOMPAS_MCP_WORKER_PATH"] = worker
        self._proc = subprocess.Popen(
            [host, "--config", config],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8", errors="replace",
            cwd=os.path.expanduser("~"), env=env, bufsize=1,
        )
        self._write_lock = threading.Lock()
        self._next_id = 1
        self.stderr = []
        # Единственный читатель stdout на весь сеанс. Поток на каждое чтение здесь был бы
        # небезопасен: при тайм-ауте прежний поток остался бы висеть на readline, и следующее
        # чтение подняло бы ВТОРОГО читателя — ровно та гонка, которую прибор ищет.
        self._lines = queue.Queue()
        self._reader = threading.Thread(target=self._pump, daemon=True)
        self._reader.start()

    def _pump(self):
        try:
            for line in self._proc.stdout:
                self._lines.put(line)
        except Exception:  # noqa: BLE001
            pass
        finally:
            self._lines.put(None)

    def send(self, payload):
        with self._write_lock:
            self._proc.stdin.write(json.dumps(payload) + "\n")
            self._proc.stdin.flush()

    def send_many(self, payloads):
        """Отправляет все запросы подряд, НЕ дожидаясь ответов, — так делает живой клиент."""
        with self._write_lock:
            for payload in payloads:
                self._proc.stdin.write(json.dumps(payload) + "\n")
            self._proc.stdin.flush()

    def read_one(self, timeout=180):
        """Читает одну строку ответа. Возвращает (msg|None, raw)."""
        try:
            line = self._lines.get(timeout=timeout)
        except queue.Empty:
            return None, "<тайм-аут чтения>"
        if line is None:
            return None, "<поток закрыт>"
        try:
            return json.loads(line), line.strip()
        except json.JSONDecodeError:
            return None, line.strip()[:400]

    def call(self, method, params):
        request_id = self._next_id
        self._next_id += 1
        self.send({"jsonrpc": "2.0", "id": request_id, "method": method, "params": params})
        msg, raw = self.read_one()
        return request_id, msg, raw

    def close(self):
        try:
            self._proc.stdin.close()
        except Exception:  # noqa: BLE001
            pass
        try:
            self._proc.wait(timeout=20)
        except Exception:  # noqa: BLE001
            self._proc.kill()
        try:
            self.stderr = (self._proc.stderr.read() or "").splitlines()[-20:]
        except Exception:  # noqa: BLE001
            self.stderr = []


def envelope_of(msg):
    if not isinstance(msg, dict):
        return None
    result = msg.get("result")
    if isinstance(result, dict) and isinstance(result.get("structuredContent"), dict):
        return result["structuredContent"]
    if isinstance(result, dict):
        return result
    return None


def main():
    host = os.path.abspath(argument("--host") or DEFAULT_HOST)
    config = os.path.abspath(argument("--config") or DEFAULT_CONFIG)
    worker = argument("--worker")
    out = os.path.abspath(argument("--out") or DEFAULT_OUT)
    calls = int(argument("--calls") or 8)
    # По умолчанию прибор пишет в СВОИ журналы: общая конфигурация увела бы его вывод в файл
    # клиентского сеанса и стёрла бы доказательства. `--keep-shared-logs` возвращает прежнее
    # поведение — только для отладки самого прибора.
    isolate = not argument("--keep-shared-logs")

    rows = []

    def row(cid, description, ok, detail=""):
        rows.append({"id": cid, "description": description,
                     "verdict": "PASS" if ok else "FAIL", "detail": detail})
        print(f"  [{'PASS' if ok else 'FAIL':4}] {cid:4} {description}"
              + (f"\n        {detail[:400]}" if detail else ""))
        return ok

    report = {"title": "Проба конкурентных вызовов через MCP",
              "host_path": host, "config_path": config, "calls": calls, "rows": rows}

    if not os.path.isfile(host):
        row("K01", "Host существует", False, host)
        return finish(report, out)

    # `config_path` в отчёте — ИСХОДНАЯ конфигурация поставки (по ней отчёт привязывается к
    # пакету). Хосту отдаётся копия с перенаправленными журналами — см. `isolate_config`.
    config_effective = isolate_config(config, out) if isolate else config
    report["config_path_effective"] = config_effective
    report["logs_isolated"] = isolate

    report["host_sha256"] = sha256_of(host)
    # Имена полей — как в отчётах приёмки и в паспорте (`host_dll_sha256`, `worker_dll_sha256`,
    # `adapter_sha256`): прибор обязан говорить на языке остальных отчётов, иначе его хеши
    # несопоставимы с пакетом автоматически.
    for name, field in (("KompasMcp.Host.dll", "host_dll_sha256"),
                        ("KompasMcp.Worker.dll", "worker_dll_sha256"),
                        ("KompasMcp.Api5Adapter.dll", "adapter_sha256")):
        path = os.path.join(os.path.dirname(host), name)
        report[field] = sha256_of(path) if os.path.isfile(path) else None
    report["worker_path_env"] = os.environ.get("KOMPAS_MCP_WORKER_PATH", "")
    report["worker_override"] = worker

    # Блок `context` — тот же, что несут отчёты `mcp-smoke.py`: по нему паспорт понимает, ЧТО именно
    # измерялось. Без него строки прибора нельзя было бы привязать к поставке автоматически.
    report["context"] = {
        "started_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="milliseconds"),
        "host_path": host,
        "host_sha256": report["host_sha256"],
        "host_dll_sha256": report.get("host_dll_sha256"),
        "worker_path": os.path.join(os.path.dirname(host), "KompasMcp.Worker.exe"),
        "worker_sha256": sha256_of(os.path.join(os.path.dirname(host), "KompasMcp.Worker.exe"))
        if os.path.isfile(os.path.join(os.path.dirname(host), "KompasMcp.Worker.exe")) else None,
        "worker_dll_sha256": report.get("worker_dll_sha256"),
        "adapter_sha256": report.get("adapter_sha256"),
        "config_path": config,
        "config_path_effective": config_effective,
        "logs_isolated": isolate,
        "worker_path_env": report["worker_path_env"],
        "calls": calls,
    }

    session = Session(host, config_effective, worker)
    try:
        request_id, msg, raw = session.call("initialize", {
            "protocolVersion": "2025-06-18", "capabilities": {},
            "clientInfo": {"name": "probe-concurrent-calls", "version": "0"},
        })
        info = (msg or {}).get("result", {}).get("serverInfo", {})
        row("K01", "initialize проходит", bool(info.get("name")),
            f"serverInfo={json.dumps(info, ensure_ascii=False)} raw={raw[:120]}")
        if not info.get("name"):
            return finish(report, out)

        session.send({"jsonrpc": "2.0", "method": "notifications/initialized"})

        _, msg, raw = session.call("tools/list", {})
        tools = (msg or {}).get("result", {}).get("tools", [])
        report["tools_count"] = len(tools)
        row("K02", "tools/list отвечает (базовая линия)", bool(tools),
            f"инструментов {len(tools)}")

        # Блок конкурентности: N запросов в полёте, как их отправляет живой клиент.
        plan = []
        for index in range(calls):
            if index % 2 == 0:
                plan.append(("kompas_health", {"detail": "minimal"}))
            else:
                plan.append(("kompas_capabilities", {}))
        payloads = []
        expected = {}
        for tool, arguments in plan:
            request_id = session._next_id
            session._next_id += 1
            expected[request_id] = tool
            payloads.append({"jsonrpc": "2.0", "id": request_id, "method": "tools/call",
                             "params": {"name": tool, "arguments": arguments}})
        session.send_many(payloads)

        answers = {}
        unparsed = []
        for _ in range(len(payloads)):
            msg, raw = session.read_one()
            if msg is None:
                unparsed.append(raw)
                continue
            answers[msg.get("id")] = msg

        report["answers"] = len(answers)
        report["unparsed"] = unparsed[:5]

        missing = sorted(set(expected) - set(answers))
        row("K03", f"{calls} конкурентных вызовов: каждый получил ответ",
            not missing and not unparsed,
            f"ответов {len(answers)} из {calls}; без ответа {missing}; неразобранных строк {len(unparsed)}"
            + (f"; пример: {unparsed[0][:200]}" if unparsed else ""))

        # Ответы перемешаны по порядку — это нормально и ожидаемо; важно, что адресат свой.
        crossed = []
        transport = []
        bad_status = []
        for request_id, tool in expected.items():
            msg = answers.get(request_id)
            if msg is None:
                continue
            env = envelope_of(msg)
            if env is None:
                crossed.append(f"id={request_id} ({tool}): нет конверта — {json.dumps(msg)[:160]}")
                continue
            if env.get("status") != "succeeded":
                error = env.get("error") if isinstance(env.get("error"), dict) else {}
                code = error.get("code")
                message = str(error.get("message") or "")[:200]
                if str(code) in TRANSPORT_FAILURES:
                    transport.append(f"id={request_id} ({tool}): {code} — {message}")
                else:
                    bad_status.append(f"id={request_id} ({tool}): status={env.get('status')} "
                                      f"code={code} — {message}")

        row("K04", "ни один ответ не пришёл отказом транспорта",
            not transport,
            "; ".join(transport)[:600] if transport else
            f"проверено {len(expected)} ответов, коды транспорта не встречались")

        row("K05", "каждый ответ — конверт своего вызова (нет перекрёстных ответов)",
            not crossed,
            "; ".join(crossed)[:600] if crossed else
            "все ответы разобраны и содержат конверт с полем status")

        row("K06", "все конкурентные вызовы завершились успехом",
            not bad_status,
            "; ".join(bad_status)[:600] if bad_status else
            f"все {len(expected)} вызовов вернули status=succeeded")
    except Exception as exc:  # noqa: BLE001 — отчёт обязан появиться даже при сбое прибора
        row("K99", "проба дошла до конца без исключения", False, f"{type(exc).__name__}: {exc}")
    finally:
        session.close()

    report["host_stderr_tail"] = session.stderr
    return finish(report, out)


def finish(report, out):
    os.makedirs(os.path.dirname(out), exist_ok=True)
    failed = [r for r in report["rows"] if r["verdict"] != "PASS"]
    report["verdict"] = "PASS" if report["rows"] and not failed else "FAIL"
    with open(out, "w", encoding="utf-8") as fh:
        json.dump(report, fh, ensure_ascii=False, indent=2)
    print(f"\nИтог: {report['verdict']} — строк {len(report['rows'])}, отказов {len(failed)}")
    print("Отчёт: " + out)
    return 0 if report["verdict"] == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main())
