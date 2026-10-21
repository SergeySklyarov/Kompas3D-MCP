"""Приёмка поставки: пакет проверяется тем бинарём, который он содержит.

`inspect-package.ps1` отвечает на вопрос «что лежит в папке» — состав, разрядность, отсутствие
вендорских файлов и чужих конфигов. Этого мало: папка может быть безупречной, а сервер в ней —
отвечать контрактом, не совпадающим с приложенными схемами, или запускать Worker из чужого
каталога. Поэтому здесь поставка **запускается** и сравнивается с тем, что в неё положили:

1. состав и разрядность исполняемых файлов (машинный признак PE, а не имя папки);
2. `initialize` + `tools/list` по настоящему stdio-транспорту — опубликованным Host;
3. схемы из `tools/list` против `schemas/` поставки и репозитория, а состав `schemas/` - против
   реестра операций из `kompas_capabilities`;
4. какие бинари фактически измерялись — пути и SHA-256.

Схемы из `tools/list` берутся с провода, а не из `--print-schemas`: клиент видит именно ответ
сервера, и «поле есть в C# или в файле схемы» не означает «поле опубликовано».

INVARIANT: `schemas/` holds one file per REGISTRY OPERATION, not one per published tool, so the two
sets are not equal by design. The permanent catalog publishes 14 tools; the folder describes 105
operations, and the ones not published directly are reached through `kompas_run`. The folder is
therefore compared with `kompas_capabilities.operations`, and the tools carrying no schema of their
own are named separately.
History: docs/decisions/tests.md#schema-coverage

Usage:
  python scripts/verify-delivery.py --delivery artifacts/publish-b2-20260918 \
      [--config config/kompas-mcp.local.json] [--out scratch/mcp-smoke/delivery-b2-20260918/package-check.json]

Без `--out` отчёт кладётся в каталог, НАЗВАННЫЙ ПО ИЗМЕРЕННОЙ ПОСТАВКЕ
(`artifacts/publish-b4-20260920` → `docs/acceptance/evidence/delivery-b4-20260920/package-check.json`), потому
что паспорт поставки читает `package-check.json` из своего каталога: отчёт, положенный не туда,
оставил бы в паспорте результат чужой поставки.
"""

import datetime
import hashlib
import json
import os
import struct
import subprocess
import sys
import tempfile
import threading
import time

for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from localconfig import isolate_config as shared_isolate_config
from localconfig import local_config

VENDOR_PREFIXES = ("Interop.Kompas", "Kompas6", "KompasAPI7", "KAPITypes", "stdole")
REQUIRED = ("KompasMcp.Host.exe", "KompasMcp.Worker.exe", "ModelContextProtocol.dll")


def argument(name, default=None):
    if name in sys.argv:
        index = sys.argv.index(name)
        if index + 1 < len(sys.argv):
            return sys.argv[index + 1]
    return default


def sha256_of(path):
    try:
        digest = hashlib.sha256()
        with open(path, "rb") as fh:
            for chunk in iter(lambda: fh.read(1 << 20), b""):
                digest.update(chunk)
        return digest.hexdigest()
    except OSError:
        return ""


def pe_machine(path):
    """Machine-поле PE-заголовка: 0x8664 — x64. Разрядность берётся из файла, не из имени каталога."""
    with open(path, "rb") as fh:
        head = fh.read(0x40)
        if head[:2] != b"MZ":
            return None
        offset = struct.unpack_from("<I", head, 0x3C)[0]
        fh.seek(offset)
        signature = fh.read(6)
        if signature[:4] != b"PE\0\0":
            return None
        return struct.unpack_from("<H", signature, 4)[0]


class Host:
    """Минимальный клиент MCP поверх stdio — тот же транспорт, что у рабочего клиента.

    `args`/`cwd`/`env` существуют не «на будущее»: запись клиента задаёт команду целиком, и
    проверять её нужно ровно в том виде, в каком она записана, включая рабочий каталог.
    """

    def __init__(self, exe, config=None, extra_args=None, cwd=None, env=None):
        args = [exe]
        if extra_args is not None:
            args += list(extra_args)
        elif config:
            args += ["--config", config]
        self.command = args
        self.cwd = cwd
        self.p = subprocess.Popen(args, cwd=cwd, env=env, stdin=subprocess.PIPE,
                                  stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                  text=True, encoding="utf-8")
        self.n = 0
        self.err = []
        self.thread = threading.Thread(target=self._drain, daemon=True)
        self.thread.start()

    def _drain(self):
        try:
            while True:
                line = self.p.stderr.readline() if self.p.stderr else ""
                if not line:
                    return
                self.err.append(line.rstrip())
        except Exception:
            return

    def call(self, method, params=None, timeout=60):
        self.n += 1
        msg = {"jsonrpc": "2.0", "id": self.n, "method": method}
        if params is not None:
            msg["params"] = params
        assert self.p.stdin is not None
        self.p.stdin.write(json.dumps(msg, ensure_ascii=False) + "\n")
        self.p.stdin.flush()
        deadline = time.time() + timeout
        while time.time() < deadline:
            line = self.p.stdout.readline() if self.p.stdout else ""
            if not line:
                if self.p.poll() is not None:
                    raise RuntimeError(f"Host завершился rc={self.p.returncode}; stderr: {self.tail()}")
                time.sleep(0.02)
                continue
            line = line.strip()
            if not line:
                continue
            reply = json.loads(line)
            if reply.get("id") == self.n:
                if "error" in reply:
                    raise RuntimeError(f"{method}: {reply['error']}")
                return reply["result"]
        raise RuntimeError(f"таймаут ожидания ответа на {method}")

    def tool(self, name, arguments, timeout=180):
        """`tools/call` → (is_error, envelope, raw result)."""
        result = self.call("tools/call", {"name": name, "arguments": arguments}, timeout=timeout)
        return result.get("isError", False), result.get("structuredContent"), result

    def notify(self, method, params=None):
        msg = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            msg["params"] = params
        assert self.p.stdin is not None
        self.p.stdin.write(json.dumps(msg, ensure_ascii=False) + "\n")
        self.p.stdin.flush()

    def tail(self):
        return "\n".join(self.err[-20:])

    def close(self):
        try:
            if self.p.stdin:
                self.p.stdin.close()
            self.p.wait(timeout=15)
        except Exception:
            self.p.kill()


def canonical(value):
    """Канонизация JSON: сравнение схем не должно зависеть от порядка ключей и пробелов.

    Верхнеуровневый `$schema` снимается намеренно. В файле он обязателен — без него документ не
    является самостоятельной JSON Schema и его нельзя открыть инструментом, — а по проводу SDK
    отдаёт ту же схему без этого ключа. Первое сравнение объявило расхождение у всех 32 схем
    именно из-за него: дефект был в приборе, а не в поставке.
    """
    if isinstance(value, dict):
        value = {k: v for k, v in value.items() if k != "$schema"}
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


def read_schema_dir(path):
    schemas = {}
    if not os.path.isdir(path):
        return schemas
    for name in sorted(os.listdir(path)):
        if not name.endswith(".json"):
            continue
        with open(os.path.join(path, name), encoding="utf-8-sig") as fh:
            schemas[name[: -len(".json")]] = json.load(fh)
    return schemas


META_TOOLS = ("kompas_find_operations", "kompas_describe_operation", "kompas_run")


def coverage_problems(wire, operations, via_run, shipped, meta=META_TOOLS):
    """Where the published tools, the operation registry and the schema folder disagree.

    `wire` - names from `tools/list`; `operations` - the whole registry from `kompas_capabilities`;
    `via_run` - operations reached through `kompas_run`; `shipped` - file names in `schemas/`.
    A direct tool is an operation that is not reached through `kompas_run`, so a direct operation
    missing from the wire is unreachable by either route, and a tool absent from both the registry
    and the meta list has no documented contract behind it. Pure: no Host, no KOMPAS, no files.
    TEST: self_test cases named "схем больше, чем инструментов" and "пропавшая схема названа".
    """
    wire, operations, via_run, shipped, meta = (
        set(x) for x in (wire, operations, via_run, shipped, meta))
    problems = [f"операция {n} есть в реестре, а схемы schemas/{n}.json в поставке нет"
                for n in sorted(operations - shipped)]
    problems += [f"в поставке лежит схема {n}.json, а операции {n} в реестре нет"
                 for n in sorted(shipped - operations)]
    problems += [f"операция {n} не публикуется ни инструментом, ни через kompas_run"
                 for n in sorted((operations - via_run) - wire)]
    problems += [f"инструмент {n} опубликован, но его нет ни в реестре операций, ни среди "
                 f"мета-инструментов"
                 for n in sorted((wire - operations) - meta)]
    return problems


def isolate_config(config, out):
    """Копия конфигурации сервера с журналами в каталоге проверки.

    Зачем. Проверка ЗАПУСКАЕТ опубликованный Host, и `--config` ей передают тот же файл, что и
    клиенту: `config/kompas-mcp.local.json` указывает `log_path` на `scratch/logs/host.jsonl` —
    ТОТ ЖЕ файл, куда пишет сервер, запущенный КЛИЕНТОМ, — и `journal_path` на общий журнал
    операций. Проверка, идущая по общей конфигурации, обрезала бы журнал клиентского сеанса, то
    есть стирала бы доказательства ровно того сеанса, ради которого её запускают.

    Это ТРЕТИЙ прибор с этим дефектом: первыми измерены `probe-concurrent-calls.py` и
    `verify-client-entry.py` (см. `instrument_defects` в `client-acceptance.json`), и у обоих он
    закрыт ровно так — своей копией конфигурации. Здесь он закрывается тем же способом, а не
    оставляется «потому что сейчас клиентский Host не запущен»: доказательство, зависящее от того,
    жив ли измеряемый процесс, — не доказательство.

    Подменяются ТОЛЬКО пути журналов. Команда, остальные аргументы и сам исходный файл не
    меняются: проверяется запись, а не удобный её вариант.

    С 18.09.2026 реализация ОДНА на весь проект — `localconfig.isolate_config`. Этот дефект
    измерялся у четырёх приборов подряд, и четвёртая копия функции была бы четвёртым местом, где
    её можно забыть.
    """
    return shared_isolate_config(config, os.path.dirname(out), "delivery")


def delivery_dir_name(stage):
    """Evidence directory of a delivery stage: `delivery-<stage>`, one name for every instrument.

    INVARIANT: the measurement, the client-entry check and the passport share ONE directory name.
    MEASURED: this report went to `evidence/<stage>/` while every delivery directory on disk is
    `evidence/delivery-<stage>/`, so `verify-client-entry.py` looked in a place nothing wrote to.
    History: docs/decisions/tests.md#evidence-rebuild
    """
    stage = stage.rstrip("/\\")
    return stage if stage.startswith("delivery-") else "delivery-" + stage


def default_out(delivery):
    """Report path: derived from the delivery NAME, under the PERMANENT evidence root.

    INVARIANT: the passport reads `package-check.json` from ITS OWN reports directory, so a report
    written elsewhere by default leaves another delivery's result in the passport. MEASURED: a
    hardcoded `scratch/mcp-smoke/delivery-20260918/...` filed a B4 check under the B2 delivery.
    The root is `docs/acceptance/evidence/`, never `scratch/` - `scratch/` is cleaned, and a delivery
    check that disappears with it was never evidence. `--out` overrides. The passport finds the
    reports directory by search (`default_reports_dir`), so `--reports` sets it too.
    History: docs/decisions/tests.md#evidence-rebuild
    """
    name = os.path.basename(delivery.rstrip("/\\"))
    if name.startswith("publish-"):
        name = name[len("publish-"):]
    return os.path.join(ROOT, "docs", "acceptance", "evidence", delivery_dir_name(name),
                        "package-check.json")


def self_test():
    """Self-check on PURE functions: PE machine, schema canonicalisation, report path.

    Both halves are mandatory: a clean input must pass and a corrupted one must be NAMED, otherwise a
    green `verify-delivery` run is indistinguishable from a run that measured nothing. No Host and no
    KOMPAS is started here, so the check depends on no delivery, no machine and no `scratch/` path.
    """
    checks, failures = [], []

    def check(name, ok, detail=""):
        checks.append(name)
        if not ok:
            failures.append(name)
        print("  [%s] %s%s" % ("PASS" if ok else "FAIL", name, (" — " + detail) if detail else ""))

    def fake_pe(machine):
        # A minimal PE image: MZ, e_lfanew at 0x3C, "PE\0\0", then the Machine field.
        data = bytearray(0x100)
        data[0:2] = b"MZ"
        struct.pack_into("<I", data, 0x3C, 0x40)
        data[0x40:0x44] = b"PE\0\0"
        struct.pack_into("<H", data, 0x44, machine)
        return bytes(data)

    with tempfile.TemporaryDirectory(prefix="verify-delivery-selftest-") as tmp:
        x64 = os.path.join(tmp, "x64.exe")
        with open(x64, "wb") as fh:
            fh.write(fake_pe(0x8664))
        check("PE x64 читается как 0x8664", pe_machine(x64) == 0x8664, str(pe_machine(x64)))

        x86 = os.path.join(tmp, "x86.exe")
        with open(x86, "wb") as fh:
            fh.write(fake_pe(0x014C))
        check("PE x86 отвергается (не 0x8664)", pe_machine(x86) == 0x014C, str(pe_machine(x86)))

        text = os.path.join(tmp, "not-pe.txt")
        with open(text, "w", encoding="utf-8") as fh:
            fh.write("MZ" * 100)
        check("не-PE отвечает None, а не нулём", pe_machine(text) is None)

    # Canonicalisation: key order and `$schema` must not matter; different values must.
    a = {"$schema": "http://x", "type": "object", "properties": {"a": 1, "b": 2}}
    b = {"type": "object", "properties": {"b": 2, "a": 1}}
    c = {"type": "object", "properties": {"b": 3, "a": 1}}
    check("порядок ключей и $schema не влияют", canonical(a) == canonical(b))
    check("разные значения дают разные канонизации", canonical(a) != canonical(c))

    # The folder is per operation, the tool list is per published tool: more schemas than tools is
    # the normal shape after the permanent catalog, and every other mismatch must still be named.
    operations = ["kompas_health", "op_a", "op_b", "op_via_run"]
    wire = ["kompas_health", "kompas_find_operations", "kompas_describe_operation", "kompas_run"]
    via_run = ["op_a", "op_b", "op_via_run"]
    check("схем больше, чем инструментов, — не расхождение",
          coverage_problems(wire, operations, via_run, operations) == [],
          str(coverage_problems(wire, operations, via_run, operations)))
    check("пропавшая схема названа",
          any("op_b" in p for p in coverage_problems(
              wire, operations, via_run, ["kompas_health", "op_a", "op_via_run"])))
    check("лишняя схема названа",
          any("op_ghost" in p for p in coverage_problems(
              wire, operations, via_run, operations + ["op_ghost"])))
    check("инструмент без операции и без мета-роли назван",
          any("kompas_extra" in p for p in coverage_problems(
              wire + ["kompas_extra"], operations, via_run, operations)))
    check("прямая операция вне tools/list названа",
          any("op_direct" in p for p in coverage_problems(
              wire, operations + ["op_direct"], via_run, operations + ["op_direct"])))

    derived = default_out(os.path.join(ROOT, "artifacts", "publish-b4-20260920"))
    check("имя отчёта выводится из имени поставки",
          derived.replace("\\", "/").endswith(
              "docs/acceptance/evidence/delivery-b4-20260920/package-check.json"),
          derived)
    check("отчёт проверки поставки не пишется в scratch/",
          "scratch" not in derived.replace("\\", "/").split("/"), derived)

    print("  проверок пройдено: %d из %d" % (len(checks) - len(failures), len(checks)))
    if failures:
        print("  батарея: КРАСНАЯ — " + ", ".join(failures))
        return 1
    print("  батарея: ЗЕЛЁНАЯ")
    return 0


def main():
    if "--self-test" in sys.argv[1:]:
        return self_test()
    delivery = os.path.abspath(argument("--delivery", os.path.join(ROOT, "artifacts", "publish")))
    config = os.path.abspath(argument("--config") or local_config(ROOT))
    out = os.path.abspath(argument("--out") or default_out(delivery))

    problems = []
    facts = {"delivery": delivery, "config_path": config}

    # Журналы проверки уходят в ЕЁ СОБСТВЕННЫЙ каталог: иначе прогон обрезал бы журнал клиентского
    # сеанса. `config_path` в фактах остаётся исходным — он и есть то, что проверялось.
    measured_config = isolate_config(config, out) if os.path.isfile(config) else config
    facts["measured_config_path"] = measured_config

    if not os.path.isdir(delivery):
        print("Каталог поставки не найден:", delivery)
        return 2

    # 1. Состав и разрядность.
    files = []
    for dirpath, _dirnames, filenames in os.walk(delivery):
        for filename in filenames:
            files.append(os.path.join(dirpath, filename))
    facts["file_count"] = len(files)
    facts["size_mb"] = round(sum(os.path.getsize(f) for f in files) / (1024 * 1024), 1)

    for path in files:
        name = os.path.basename(path)
        if name.startswith(VENDOR_PREFIXES) or path.endswith(".tlb"):
            problems.append("в поставку попал вендорский файл: " + path)

    for required in REQUIRED:
        matches = [f for f in files if os.path.basename(f) == required]
        if not matches:
            problems.append("не найден обязательный файл: " + required)

    machines = {}
    for exe in ("KompasMcp.Host.exe", "KompasMcp.Worker.exe"):
        matches = [f for f in files if os.path.basename(f) == exe]
        if matches:
            machine = pe_machine(matches[0])
            machines[exe] = f"0x{machine:04X}" if machine is not None else "н/д"
            if machine != 0x8664:
                problems.append(f"{exe}: machine={machines[exe]}, ожидается 0x8664 (x64)")
    facts["pe_machine"] = machines

    locals_shipped = [f for f in files if f.endswith(".local.json")]
    for path in locals_shipped:
        problems.append("в поставку попал локальный конфиг: " + path)
    if not [f for f in files if f.endswith(".example.json")]:
        problems.append("нет шаблона конфигурации (*.example.json): оператор не сможет задать корни")

    # 2. Контракт с провода: опубликованный Host отвечает сам.
    host_exe = os.path.join(delivery, "KompasMcp.Host.exe")
    facts["host_path"] = host_exe
    # Хеш и .exe, и .dll: .exe — это apphost-заглушка, она не меняется от правок кода, поэтому
    # «хеш Host» по одному .exe остался бы прежним после пересборки и ничего не доказывал.
    facts["host_sha256"] = sha256_of(host_exe)
    facts["host_dll_path"] = os.path.join(delivery, "KompasMcp.Host.dll")
    facts["host_dll_sha256"] = sha256_of(facts["host_dll_path"])
    facts["worker_path"] = os.path.join(delivery, "KompasMcp.Worker.exe")
    facts["worker_sha256"] = sha256_of(facts["worker_path"])
    facts["worker_dll_path"] = os.path.join(delivery, "KompasMcp.Worker.dll")
    facts["worker_dll_sha256"] = sha256_of(facts["worker_dll_path"])
    facts["adapter_path"] = os.path.join(delivery, "KompasMcp.Api5Adapter.dll")
    facts["adapter_sha256"] = sha256_of(facts["adapter_path"])
    facts["worker_path_env"] = os.environ.get("KOMPAS_MCP_WORKER_PATH", "")
    if facts["worker_path_env"]:
        problems.append("KOMPAS_MCP_WORKER_PATH задан в окружении: поставку перенаправило бы на чужой Worker")

    client = Host(host_exe, measured_config)
    try:
        init = client.call("initialize", {
            "protocolVersion": "2025-06-18",
            "capabilities": {},
            "clientInfo": {"name": "verify-delivery", "version": "0"},
        })
        facts["server_info"] = init.get("serverInfo", {})
        facts["protocol_version"] = init.get("protocolVersion")
        client.notify("notifications/initialized")
        tools = client.call("tools/list", {}).get("tools", [])
        # The registry comes from the delivered Host, not from the repository: it is what tells a
        # schema folder of operations from a folder of tools. A refusal is NAMED, never a skip.
        registry, via_run, capabilities_problems = None, [], []
        try:
            is_error, envelope, _raw = client.tool("kompas_capabilities", {})
            payload = (envelope or {}).get("result") or {}
            registry, via_run = payload.get("operations"), payload.get("operations_via_run") or []
            if is_error or registry is None:
                registry, capabilities_problems = None, [
                    "kompas_capabilities не назвал реестр операций: сверять schemas/ не с чем — "
                    + str(payload.get("error") or "isError")]
            elif payload.get("tool_count") != len(tools):
                capabilities_problems = [
                    f"kompas_capabilities: tool_count={payload.get('tool_count')}, "
                    f"а tools/list отдаёт {len(tools)}"]
        except Exception as exc:
            registry, capabilities_problems = None, [
                "kompas_capabilities не ответил: сверять schemas/ не с чем — " + str(exc)]
    finally:
        client.close()

    facts["tools_count"] = len(tools)
    facts["tools"] = sorted(t["name"] for t in tools)
    wire = {t["name"]: t.get("inputSchema") for t in tools}

    shipped = read_schema_dir(os.path.join(delivery, "schemas"))
    repo = read_schema_dir(os.path.join(ROOT, "schemas"))
    facts["schemas_shipped_count"] = len(shipped)
    facts["schemas_repo_count"] = len(repo)

    problems += capabilities_problems
    if registry is not None:
        facts["operations_count"] = len(registry)
        facts["operations_via_run_count"] = len(via_run)
        facts["tools_without_schema"] = sorted(set(wire) - set(registry))
        problems += coverage_problems(wire, registry, via_run, shipped)
    # The delivery carries the repository's schemas verbatim: a difference means one of the two was
    # rebuilt or edited after the other, and a reader of the folder would get another contract.
    for name in sorted(set(shipped) | set(repo)):
        if name not in repo:
            problems.append(f"схема {name}.json есть в поставке, но не в репозитории")
        elif name not in shipped:
            problems.append(f"схема {name}.json есть в репозитории, но не в поставке")
        elif canonical(shipped[name]) != canonical(repo[name]):
            problems.append(f"схема {name}.json в поставке разошлась с schemas/ репозитория")
    mismatched = []
    for name in sorted(set(wire) & set(shipped)):
        if canonical(wire[name]) != canonical(shipped[name]):
            mismatched.append(name)
    for name in mismatched:
        problems.append(f"схема {name} из tools/list не совпадает с поставленной schemas/{name}.json")
    facts["schema_mismatches"] = mismatched

    repo_drift = [name for name in sorted(set(repo) & set(wire)) if canonical(repo[name]) != canonical(wire[name])]
    for name in repo_drift:
        problems.append(f"schemas/{name}.json в репозитории разошлась с публикуемым контрактом")
    facts["repo_drift"] = repo_drift

    facts["problems"] = problems
    facts["verdict"] = "PASS" if not problems else "FAIL"

    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "w", encoding="utf-8") as fh:
        json.dump(facts, fh, ensure_ascii=False, indent=2)

    print(json.dumps({k: v for k, v in facts.items() if k != "tools"}, ensure_ascii=False, indent=2))
    print("\nОтчёт: " + out)
    if problems:
        print(f"\nИтог: FAIL, замечаний {len(problems)}")
        return 1
    print(f"\nИтог: PASS — поставка отвечает тем же контрактом, что и приложенные схемы ({facts['tools_count']} инструментов).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
