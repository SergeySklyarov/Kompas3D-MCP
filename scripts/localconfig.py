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
