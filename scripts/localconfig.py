"""Локальная конфигурация запуска Host'а.

`config/kompas-mcp.local.json` содержит абсолютные пути этой машины (roots доступа к диску,
куда писать журналы и артефакты) и в репозиторий не попадает намеренно — он описывает конкретный
компьютер, а не проект. Из-за этого на свежем клоне файла нет, и скрипты приёмки не могут молча
«взять конфиг по умолчанию»: молчаливый запуск с чужими или несуществующими корнями дал бы отказ
где-нибудь в COM или в политике путей, и искать причину пришлось бы не там, где она.

Поэтому все точки входа спрашивают конфиг через :func:`local_config` и получают либо путь, либо
короткую инструкцию.
"""

from __future__ import annotations

import datetime
import glob
import json
import os

LOCAL_CONFIG = os.path.join("config", "kompas-mcp.local.json")
EXAMPLE_CONFIG = os.path.join("config", "kompas-mcp.example.json")

# The installer keeps a junction `<InstallRoot>\current` and a state file next to it; a client entry
# pointing through the junction names no delivery on its own.
CURRENT_LINK = "current"
INSTALLED_STATE = "installed.json"

# DOC: `@deepseek-ai/dsh-mcp-client` README, "Minimal configuration": a dsh profile patch layer holds
# one entry per server as `- id:` / `name: '@deepseek-ai/dsh-mcp-client'` / `config: {...}`. The
# acceptance client is `dsh web` (decision 06.11.2026), and its patch layer is the record to read.
MCP_CLIENT_PLUGIN = "@deepseek-ai/dsh-mcp-client"
_DSH_WEB_PROFILE = os.path.join(".dsh", "profiles", "web", "cordis.patch.yml")

_MISSING = """Нет локальной конфигурации: {local}

Скопируйте шаблон и правьте его под свою машину:

    copy {example} {local}

Минимум, который нужно задать: read_only_roots (исходные модели, только чтение),
writable_roots и export_roots (куда писать сохранения и экспорты), log_path, journal_path,
artifact_directory. Пути должны существовать и быть доступны вашему пользователю — сервер
ограничивает доступ к диску этим списком и не создаёт корни за вас.

Значения из шаблона путям на этой машине не соответствуют, поэтому скрипт не копирует файл
молча: принятые без настройки прогоны измерили бы не проект, а вашу удачу."""


def local_config(root: str) -> str:
    """Абсолютный путь к локальному конфигу или внятная остановка скрипта.

    :param root: корень репозитория (обычно `ROOT` самого скрипта).
    """
    path = os.path.join(root, LOCAL_CONFIG)
    if os.path.isfile(path):
        return path
    raise SystemExit(
        _MISSING.format(
            local=path,
            example=os.path.join(root, EXAMPLE_CONFIG),
        )
    )


def isolate_config(config: str, base_dir: str, prefix: str = "probe") -> str:
    """Копия конфигурации с журналами в каталоге прибора; возвращает путь к копии.

    Зачем это здесь, а не в каждом приборе. Хост пишет туда, куда указывает конфигурация, а
    `config/kompas-mcp.local.json` указывает `log_path` на `scratch/logs/host.jsonl` — ТОТ ЖЕ файл,
    куда пишет сервер, запущенный КЛИЕНТОМ. Прибор, идущий по общей конфигурации, дописывает в
    журнал клиентского сеанса свои строки и делает доказательство клиентской приёмки неотличимым от
    шума прибора.

    Дефект измерялся ЧЕТЫРЕ раза подряд, и каждый раз его закрывали своей копией функции:
    `probe-concurrent-calls.py`, `verify-client-entry.py`, `verify-delivery.py`, а 18.09.2026 —
    `mcp-smoke.py`, который за один прогон приёмки добавил в общий журнал 27 705 строк. Четыре
    копии одной функции — это четыре места, где её можно забыть; поэтому реализация одна и живёт
    рядом с `local_config`, через который конфиг и получают все точки входа.

    Подменяются ТОЛЬКО пути журналов и каталога артефактов. Команда, остальные поля и сам исходный
    файл не меняются: прибор обязан измерять запись, а не удобный её вариант.

    :param config: исходная конфигурация (в отчёт идёт ОНА — по ней отчёт привязывается к поставке).
    :param base_dir: каталог прибора; копии лягут в `<base_dir>/probe-logs/`. Обязан лежать внутри
        `writable_roots` из конфигурации, иначе Хост не сможет туда писать.
    :param prefix: различитель приборов в именах файлов.
    """
    with open(config, encoding="utf-8-sig") as fh:
        data = json.load(fh)
    base = os.path.join(base_dir, "probe-logs")
    os.makedirs(base, exist_ok=True)
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S-%f")
    data["log_path"] = os.path.join(base, f"{prefix}-host-{stamp}.jsonl")
    data["worker_log_path"] = os.path.join(base, f"{prefix}-worker-{stamp}.jsonl")
    data["journal_path"] = os.path.join(base, f"{prefix}-operations-{stamp}.jsonl")
    data["artifact_directory"] = os.path.join(base, f"{prefix}-artifacts-{stamp}")
    isolated = os.path.join(base, f"{prefix}-config-{stamp}.json")
    with open(isolated, "w", encoding="utf-8") as fh:
        json.dump(data, fh, ensure_ascii=False, indent=2)
    return isolated


def _current_version(delivery_dir: str):
    """Version directory behind `<InstallRoot>\\current`; returns `(dir, tag, source, problem)`.

    INVARIANT: the junction is resolved through `installed.json` first and through the link target
    second, never by reading the junction name as a delivery name. MEASURED: an entry written
    through `current` was taken for a delivery of its own, so the instruments looked for evidence of
    a delivery `current` that does not exist. History: docs/decisions/tests.md#current-layout
    """
    install_root = os.path.dirname(delivery_dir)
    state_path = os.path.join(install_root, INSTALLED_STATE)
    tag, source = None, None
    try:
        with open(state_path, encoding="utf-8-sig") as fh:
            tag = (json.load(fh).get("tag") or "").strip() or None
        source = state_path if tag else None
    except (OSError, ValueError):
        tag = None
    if not tag:
        target = os.path.realpath(delivery_dir)
        resolved = os.path.basename(os.path.normpath(target)) or None
        if resolved and resolved.lower() != CURRENT_LINK:
            tag, source = resolved, "цель точки соединения: " + target
    if not tag:
        return None, None, None, (
            "тег установки не найден: нет %s, и точка соединения %s не разрешается"
            % (state_path, delivery_dir))
    version_dir = os.path.join(install_root, "versions", tag)
    if not os.path.isdir(version_dir):
        return None, tag, source, ("каталога версии нет: %s (тег %s, источник %s)"
                                  % (version_dir, tag, source))
    return version_dir, tag, source, None


def client_delivery(command: str, root: str):
    """Evidence directory and passport of the delivery a client entry points at.

    Returns `(info, problem)`; `info` carries `version_dir`, `tag`, `source`, `evidence_dir`,
    `passport`. INVARIANT: both acceptance instruments call THIS function - a copy would drift and
    the passport and the approval report would name different deliveries. LIMIT: only the layout
    `<InstallRoot>\\current` and a command inside a delivery directory are understood; any other
    layout is reported as the delivery directory of the command itself.
    History: docs/decisions/tests.md#current-layout
    """
    delivery_dir = os.path.dirname(os.path.abspath(command))
    name = os.path.basename(os.path.normpath(delivery_dir))
    info = {"version_dir": delivery_dir, "tag": None, "source": "каталог команды записи",
            "evidence_dir": None, "passport": None}
    if name.lower() != CURRENT_LINK:
        stage = name[len("publish-"):] if name.startswith("publish-") else name
        info["evidence_dir"] = os.path.join(root, "docs", "acceptance", "evidence",
                                           "delivery-" + stage)
        return info, None
    version_dir, tag, source, problem = _current_version(delivery_dir)
    if problem:
        return None, problem
    info.update(version_dir=version_dir, tag=tag, source=source)
    pattern = os.path.join(root, "docs", "acceptance", "delivery-" + tag + "*",
                           "delivery-passport.json")
    found = sorted(glob.glob(pattern))
    if not found:
        return None, "паспорт поставки %s не найден: искал %s" % (tag, pattern)
    if len(found) > 1:
        return None, ("паспортов поставки %s несколько, и выбрать из них один - догадка: %s. "
                      "Укажите --passport"
                      % (tag, ", ".join(os.path.relpath(path, root) for path in found)))
    info["passport"] = found[0]
    info["evidence_dir"] = os.path.join(root, "docs", "acceptance", "evidence",
                                        os.path.basename(os.path.dirname(found[0])))
    if not os.path.isdir(info["evidence_dir"]):
        return None, ("каталог доказательств поставки не найден: %s (паспорт %s)"
                      % (info["evidence_dir"], found[0]))
    return info, None


def dsh_profile_path(home=None):
    """Patch layer of the `web` dsh profile - the client record the acceptance instruments read."""
    return os.path.join(home or os.path.expanduser("~"), _DSH_WEB_PROFILE)


def _dsh_lines(text):
    """`(indent, content)` lines: comments and blank lines dropped, `- key: value` split in two.

    LIMIT: block scalars, anchors and multi-line flow collections are outside the subset; such a
    layer raises ValueError in `_dsh_block`, so an unsupported file is a refusal, not a guess.
    """
    lines = []
    for raw in text.splitlines():
        if not raw.strip() or raw.lstrip().startswith("#"):
            continue
        indent = len(raw) - len(raw.lstrip(" "))
        content = raw.strip()
        if content == "-":
            lines.append((indent, "-"))
        elif content.startswith("- "):
            lines.append((indent, "-"))
            lines.append((indent + 2, content[2:].strip()))
        else:
            lines.append((indent, content))
    return lines


def _dsh_split_flow(text):
    """Split a flow collection body on commas that are outside quotes and nested brackets."""
    parts, depth, quote, current = [], 0, "", []
    for char in text:
        if quote:
            current.append(char)
            if char == quote:
                quote = ""
            continue
        if char in "'\"":
            quote = char
            current.append(char)
        elif char in "[{":
            depth += 1
            current.append(char)
        elif char in "]}":
            depth -= 1
            current.append(char)
        elif char == "," and depth == 0:
            parts.append("".join(current))
            current = []
        else:
            current.append(char)
    if "".join(current).strip():
        parts.append("".join(current))
    return parts


def _dsh_scalar(text):
    """A YAML scalar of the subset: quoted string, flow list/map, or the plain text as written."""
    text = text.strip()
    if text in ("", "~", "null"):
        return None
    if len(text) > 1 and text[0] == text[-1] and text[0] in "'\"":
        if text[0] == "'":
            return text[1:-1].replace("''", "'")
        try:
            return json.loads(text)
        except ValueError:
            return text[1:-1]
    if text.startswith("[") and text.endswith("]"):
        return [_dsh_scalar(part) for part in _dsh_split_flow(text[1:-1])]
    if text.startswith("{") and text.endswith("}"):
        mapping = {}
        for part in _dsh_split_flow(text[1:-1]):
            key, _, value = part.partition(":")
            mapping[str(_dsh_scalar(key))] = _dsh_scalar(value)
        return mapping
    return text


def _dsh_block(lines, index, indent):
    """Parse one block (sequence of `-` items or a mapping) → `(value, next index)`."""
    if lines[index][1] == "-":
        items = []
        while index < len(lines) and lines[index][0] == indent and lines[index][1] == "-":
            index += 1
            if index < len(lines) and lines[index][0] > indent:
                value, index = _dsh_block(lines, index, lines[index][0])
            else:
                value = None
            items.append(value)
        return items, index
    mapping = {}
    while index < len(lines):
        current_indent, content = lines[index]
        if current_indent < indent or content == "-":
            break
        if current_indent > indent:
            raise ValueError("unexpected indentation: %r" % (content,))
        key, separator, rest = content.partition(":")
        if not separator:
            raise ValueError("not a `key: value` line: %r" % (content,))
        index += 1
        if rest.strip():
            mapping[key.strip()] = _dsh_scalar(rest)
        elif index < len(lines) and lines[index][0] > indent:
            mapping[key.strip()], index = _dsh_block(lines, index, lines[index][0])
        else:
            mapping[key.strip()] = None
    return mapping, index


def parse_dsh_patch(text):
    """Entries of a dsh profile patch layer as a list of `id` / `name` / `config` mappings.

    INVARIANT: the whole file is consumed; an unparsed tail is a ValueError, not a shorter list -
    a partially read layer could hide the entry the instruments are looking for.
    """
    lines = _dsh_lines(text)
    if text.strip() in ("", "[]", "~", "null"):
        return []
    if not lines:
        return []
    value, index = _dsh_block(lines, 0, lines[0][0])
    if index != len(lines):
        raise ValueError("unparsed tail: %r" % (lines[index][1],))
    if not isinstance(value, list):
        raise ValueError("top level is not a list of entries")
    return value


def dsh_mcp_entry(path, server_name="kompas"):
    """The `server_name` MCP client entry of a dsh profile patch layer → `(entry, problem)`.

    INVARIANT: only an entry of `MCP_CLIENT_PLUGIN` with `config.serverName == server_name` is
    taken; two such entries are a refusal because dsh fails the later one (`dsh-mcp-client` README,
    "Tool naming and coexistence"). An unreadable or unsupported file is a refusal that names the
    cause - never an empty entry, which would read as "the client has no record".
    """
    try:
        with open(path, encoding="utf-8-sig") as handle:
            entries = parse_dsh_patch(handle.read())
    except (OSError, ValueError) as exc:
        return None, "%s: %s (%s)" % (type(exc).__name__, exc, path)
    found, names = [], []
    for entry in entries:
        if not isinstance(entry, dict):
            continue
        config = entry.get("config") if isinstance(entry.get("config"), dict) else {}
        names.append("%s/%s" % (entry.get("id"), config.get("serverName")))
        if entry.get("name") == MCP_CLIENT_PLUGIN and config.get("serverName") == server_name:
            found.append(entry)
    if not found:
        return None, ("в %s нет записи сервера '%s' пакета %s; есть: %s"
                      % (path, server_name, MCP_CLIENT_PLUGIN, ", ".join(names) or "ничего"))
    if len(found) > 1:
        return None, ("в %s несколько записей '%s' пакета %s - dsh откажет второй: %s"
                      % (path, server_name, MCP_CLIENT_PLUGIN,
                         ", ".join(str(entry.get("id")) for entry in found)))
    config = found[0].get("config") or {}
    args, env = config.get("args") or [], config.get("env") or {}
    if not isinstance(args, list) or not isinstance(env, dict):
        return None, "запись '%s' в %s: args или env не того вида" % (server_name, path)
    return {"id": found[0].get("id"), "plugin": found[0].get("name"),
            "server_name": config.get("serverName"), "transport": config.get("transport"),
            "command": config.get("command"), "args": [str(arg) for arg in args],
            "env": {str(key): str(value) for key, value in env.items()},
            "cwd": config.get("cwd")}, None
