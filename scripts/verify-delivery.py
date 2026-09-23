"""Приёмка поставки: пакет проверяется тем бинарём, который он содержит.

`inspect-package.ps1` отвечает на вопрос «что лежит в папке» — состав, разрядность, отсутствие
вендорских файлов и чужих конфигов. Этого мало: папка может быть безупречной, а сервер в ней —
отвечать контрактом, не совпадающим с приложенными схемами, или запускать Worker из чужого
каталога. Поэтому здесь поставка **запускается** и сравнивается с тем, что в неё положили:

1. состав и разрядность исполняемых файлов (машинный признак PE, а не имя папки);
2. `initialize` + `tools/list` по настоящему stdio-транспорту — опубликованным Host;
3. схемы из `tools/list` против каталога `schemas/` поставки и против `schemas/` репозитория;
4. какие бинари фактически измерялись — пути и SHA-256.

Схемы из `tools/list` берутся с провода, а не из `--print-schemas`: клиент видит именно ответ
сервера, и «поле есть в C# или в файле схемы» не означает «поле опубликовано».

Usage:
  python scripts/verify-delivery.py --delivery artifacts/publish-b2-20260918 \
      [--config config/kompas-mcp.local.json] [--out scratch/mcp-smoke/delivery-b2-20260918/package-check.json]

Без `--out` отчёт кладётся в каталог, НАЗВАННЫЙ ПО ИЗМЕРЕННОЙ ПОСТАВКЕ
(`artifacts/publish-b4-20260920` → `scratch/mcp-smoke/delivery-b4-20260920/package-check.json`), потому
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


def default_out(delivery):
    """Каталог отчёта — ИМЯ ТОЙ ПОСТАВКИ, КОТОРУЮ ИЗМЕРИЛИ, а не константа.

    Здесь стоял жёсткий путь `scratch/mcp-smoke/delivery-20260918/package-check.json`: проверка
    поставки B4 складывала отчёт в каталог, названный по поставке B2. Это не косметика — паспорт
    поставки читает `package-check.json` из СВОЕГО каталога, и отчёт, положенный по умолчанию не
    туда, оставлял бы в паспорте чужой результат. Имя выводится из имени поставки:
    `artifacts/publish-b4-20260920` → `scratch/mcp-smoke/delivery-b4-20260920/package-check.json`.
    """
    name = os.path.basename(delivery.rstrip("/\\"))
    if name.startswith("publish-"):
        name = name[len("publish-"):]
    return os.path.join(ROOT, "scratch", "mcp-smoke", f"delivery-{name}", "package-check.json")


def main():
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
    finally:
        client.close()

    facts["tools_count"] = len(tools)
    facts["tools"] = sorted(t["name"] for t in tools)
    wire = {t["name"]: t.get("inputSchema") for t in tools}

    shipped = read_schema_dir(os.path.join(delivery, "schemas"))
    repo = read_schema_dir(os.path.join(ROOT, "schemas"))
    facts["schemas_shipped_count"] = len(shipped)
    facts["schemas_repo_count"] = len(repo)

    for name in sorted(set(wire) - set(shipped)):
        problems.append(f"инструмент {name} отвечает по проводу, но схемы в поставке нет")
    for name in sorted(set(shipped) - set(wire)):
        problems.append(f"в поставке лежит схема {name}.json, но такого инструмента сервер не публикует")
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
