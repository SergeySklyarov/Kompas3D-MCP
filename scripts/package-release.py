"""Packs an accepted delivery folder into the public release assets.

Usage:
  python scripts/package-release.py --delivery artifacts/<delivery> --tag vX.Y.Z
                                    --passport docs/acceptance/<delivery>/delivery-passport.json
                                    --title "KompasMCP X.Y.Z - <темы выпуска>"
                                    [--out artifacts/release/<tag>]
  python scripts/package-release.py --self-test

INVARIANT (release identity), checked before a single file is written:
  - the tag is "v" + <Version> from Directory.Build.props;
  - every own assembly carries the same stamp "<Version>+<commit>"; that commit is the build commit;
  - the passport belongs to this delivery (Host.dll hash), says PASS / fully_ready, from a clean tree;
  - product sources are identical at the build commit and at the passport commit. The SDK stamps HEAD
    even for a dirty tree, so this diff is what reveals a delivery built from uncommitted sources.
History: docs/decisions/releases.md#versioning

INVARIANT (release text), checked at the same point: the title is "KompasMCP <Version> - <themes>",
the notes docs/distribution/<tag>.md open with that title, and neither they nor any text file of the
ZIP carries a long dash (scripts/lint-dashes.py). The release is published with the printed command.
History: docs/decisions/releases.md#dashes

Writes three assets next to each other, ready for a GitHub Release:
  KompasMCP-<tag>-win-x64.zip   the delivery as is, plus LICENSE, notices, install notes, installer
  release-manifest.json         public manifest: tag, scope, requirements, own assembly hashes
  SHA256SUMS.txt                SHA-256 of the ZIP, the manifest and the installer

INVARIANT: the delivery is copied byte for byte; this script adds files, never rebuilds or edits a
binary. A release that needs a different binary needs a new accepted delivery first.
"""

import argparse
import hashlib
import importlib.util
import json
import os
import re
import shutil
import sys
import tempfile
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def _load_dash_guard():
    """scripts/lint-dashes.py as a module: one definition of a long dash for both tools."""
    spec = importlib.util.spec_from_file_location("lint_dashes", os.path.join(ROOT, "scripts", "lint-dashes.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


DASHES = _load_dash_guard()

OWN_ASSEMBLIES = [
    "KompasMcp.Host.exe", "KompasMcp.Host.dll", "KompasMcp.Worker.exe", "KompasMcp.Worker.dll",
    "KompasMcp.Api5Adapter.dll", "KompasMcp.Domain.dll", "KompasMcp.Contracts.dll",
]

# INVARIANT (release scope): the profile list and EVERY number in it come from the release profiles
# themselves, measured by acceptance-levels.py - never from a literal that can go stale. The 0.6.0
# release stopped because a hand-kept list missed one profile; here a profile present in
# coverage/solid-v24/release-profiles/ cannot be omitted, and its ratio is measured, not claimed.
# Only the wording below is editorial: a release title cannot be measured. A profile without wording
# is NOT dropped - it falls back to its own meta.title and is still named in the manifest.
PROFILE_TEXT = {
    "mechanical-core-v1": {"title": "Детали: твердотельное моделирование", "scope": "детали"},
    "assemblies-minimal-v1": {"title": "Сборки", "scope": "сборки"},
    "mates-minimal-v1": {"title": "Сопряжения", "scope": "сопряжения"},
    "drawings-minimal-v1": {
        "title": "Чертежи: виды, размеры, штамп, DXF/DWG, техтребования", "scope": "чертежи"},
    "variables-material-minimal-v1": {
        "title": "Переменные и материал детали", "scope": "переменные и материал"},
    "assembly-interference-minimal-v1": {
        "title": "Пересечения и зазоры между компонентами сборки",
        "scope": "пересечения и зазоры сборки"},
    "sketch-bulk-minimal-v1": {
        "title": "Массовая геометрия эскиза: нативная полилиния, сплайн",
        "scope": "массовая геометрия эскиза"},
}

# Spelled-out count of profiles for the release text; the noun agrees with the numeral. Beyond the
# table the number is printed as digits, which is honest but visibly not the editorial style.
NUMERAL_WORDS = ("ноль", "один", "два", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять",
                 "десять", "одиннадцать", "двенадцать", "тринадцать", "четырнадцать", "пятнадцать",
                 "шестнадцать", "семнадцать", "восемнадцать", "девятнадцать", "двадцать")


def profiles_phrase(count):
    """«семь профилей» for a spelled-out count, digits beyond the table."""
    if count >= len(NUMERAL_WORDS):
        return f"{count} профилей"
    noun = "профиль" if count == 1 else ("профиля" if 2 <= count <= 4 else "профилей")
    return f"{NUMERAL_WORDS[count]} {noun}"


def _load_levels():
    """scripts/acceptance-levels.py as a module: one definition of the release scope for both tools."""
    spec = importlib.util.spec_from_file_location(
        "acceptance_levels", os.path.join(ROOT, "scripts", "acceptance-levels.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def profile_title(root, profile_rel):
    """`meta.title` of one release profile, or None."""
    with open(os.path.join(root, profile_rel), encoding="utf-8-sig") as handle:
        return (json.load(handle).get("meta") or {}).get("title")


def measured_profiles(root):
    """Manifest entries {id, title, scope_label, modes, deps}: the set and the numbers are MEASURED."""
    scope = _load_levels().evaluate_release_scope(root)
    entries = []
    for profile in scope["profiles"]:
        profile_id = profile["profile_id"]
        text = PROFILE_TEXT.get(profile_id) or {}
        title = text.get("title") or profile_title(root, profile["profile_artifact"])
        if not title:
            sys.exit(f"профиль {profile_id} без заголовка: нет ни в PROFILE_TEXT, ни в meta.title")
        entries.append({
            "id": profile_id,
            "title": title,
            "scope_label": text.get("scope") or title,
            "modes": f'{profile["modes_closed"]}/{profile["modes_total"]}',
            "deps": f'{profile["deps_closed"]}/{profile["deps_total"]}',
        })
    return entries

TAG_RE = re.compile(r"^v(\d+)\.(\d+)\.(\d+)$")
PROPS_VERSION_RE = re.compile(r"<Version>\s*([^<\s]+)\s*</Version>")
# The SDK writes "<Version>+<full commit>" into AssemblyInformationalVersion: UTF-8 in metadata and
# UTF-16 in the Win32 version resource. Either copy is enough to read the stamp.
STAMP_RE = re.compile(r"(\d+\.\d+\.\d+)\+([0-9a-f]{40})")


def product_version(props_text):
    """`<Version>` of Directory.Build.props, or None."""
    m = PROPS_VERSION_RE.search(props_text)
    return m.group(1) if m else None


def tag_problem(tag, version):
    """Why `tag` cannot name a release of `version`; None when it can."""
    if not TAG_RE.match(tag):
        return f"тег «{tag}» не в форме vMAJOR.MINOR.PATCH (например v0.1.0)"
    if version is None:
        return "в Directory.Build.props нет <Version>"
    if tag != "v" + version:
        return f"тег «{tag}» не совпадает с версией продукта «{version}» (ожидается «v{version}»)"
    return None


def dash_problems(label, text):
    """One problem per long dash in `text`, named by `label:line:column`."""
    return [f"{label}:{line}:{col}: {name} - в публикуемом тексте тире пишется дефисом «-»"
            for line, col, name in DASHES.dash_findings(text)]


def title_problems(title, version):
    """Why `title` cannot name the release of `version`; empty when it can."""
    problems = dash_problems("заголовок", title)
    prefix = f"KompasMCP {version} - "
    if not title.startswith(prefix) or not title[len(prefix):].strip():
        problems.append(f"заголовок «{title}» не в форме «{prefix}<темы выпуска>»")
    return problems


def notes_problems(notes_name, notes_text, title):
    """Why the notes cannot go out under `title`; empty when they can."""
    if notes_text is None:
        return [f"нет заметок к выпуску {notes_name}"]
    problems = dash_problems(notes_name, notes_text)
    first = notes_text.lstrip("﻿").splitlines()[0] if notes_text.strip() else ""
    if first != "# " + title:
        problems.append(f"{notes_name}: первая строка «{first}» не совпадает с «# {title}»")
    return problems


def stamps_in(data):
    """Every `(version, commit)` stamp found in a binary."""
    found = set(STAMP_RE.findall(data.decode("latin-1")))
    found |= set(STAMP_RE.findall(data.decode("utf-16-le", "ignore")))
    return found


def stamp_problem(name, stamps, version):
    """Why one assembly's stamp disagrees with the release version; None when it agrees."""
    if not stamps:
        return f"{name}: штамп «версия+коммит» не найден"
    if len(stamps) > 1:
        return f"{name}: несколько разных штампов {sorted(stamps)}"
    got_version, _ = next(iter(stamps))
    if got_version != version:
        return f"{name}: версия в сборке {got_version}, а у выпуска {version}"
    return None


def build_commit_problem(commits):
    """Why the assemblies do not name one build commit; None when they do."""
    if len(commits) != 1:
        return f"сборки поставки названы разными коммитами: {sorted(c[:12] for c in commits)}"
    return None


def passport_problems(passport, host_dll_sha256):
    """Why the passport does not admit this delivery to a release; empty when it does."""
    problems = []
    acceptance = passport.get("acceptance") or {}
    if acceptance.get("verdict") != "PASS" or acceptance.get("fully_ready") is not True:
        problems.append(f"паспорт не допускает выпуск: verdict={acceptance.get('verdict')}, "
                        f"fully_ready={acceptance.get('fully_ready')}")
    if (passport.get("package") or {}).get("host_dll_sha256") != host_dll_sha256:
        problems.append("паспорт описывает другую поставку: хеш KompasMcp.Host.dll не совпадает")
    source = passport.get("source") or {}
    if not source.get("measured") or not source.get("commit"):
        problems.append("в паспорте не измерен исходный коммит")
    elif source.get("tree_dirty") is not False:
        problems.append("паспорт выпущен из грязного дерева: исходник поставки не восстанавливается")
    return problems


# Paths whose content decides the binaries. Docs, coverage and scripts do not reach a delivery.
PRODUCT_SOURCES = ["src", "Directory.Build.props", "Directory.Build.targets",
                   "Directory.Packages.props", "global.json", "NuGet.config"]


def sources_problem(build_commit, passport_commit):
    """Why product sources differ between the build commit and the passport commit; None if equal."""
    import subprocess
    done = subprocess.run(["git", "diff", "--quiet", build_commit, passport_commit, "--", *PRODUCT_SOURCES],
                          cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if done.returncode == 0:
        return None
    if done.returncode == 1:
        return (f"исходники продукта в коммите сборки {build_commit[:12]} и в коммите паспорта "
                f"{passport_commit[:12]} различаются: поставка собрана не из закоммиченного исходника")
    return f"git diff не выполнен: {done.stderr.decode('utf-8', 'replace').strip()}"


def self_test_profile_catalog():
    """The manifest must name one entry per profile file, MEASURED from the copied directory.

    The hand-kept list this replaces failed by OMISSION - a new profile simply did not appear. So the
    check is about the count following the directory, not about a specific profile: the real catalog
    is copied, one more profile file is added, and the manifest must name exactly one entry more than
    the source directory held. The expected count is derived from that directory, not spelled out:
    the literal "eight" went stale the moment a new profile was added to the real catalog.
    """
    source = os.path.join(ROOT, "coverage", "solid-v24")
    profiles_dir = os.path.join(source, "release-profiles")
    copied = [name for name in os.listdir(profiles_dir) if name.endswith(".json")]
    with tempfile.TemporaryDirectory(prefix="profile-catalog-") as root:
        target = os.path.join(root, "coverage", "solid-v24")
        os.makedirs(os.path.join(target, "release-profiles"))
        shutil.copy(os.path.join(source, "matrix.json"), os.path.join(target, "matrix.json"))
        for name in copied:
            shutil.copy(os.path.join(profiles_dir, name),
                        os.path.join(target, "release-profiles", name))
        extra = {"meta": {"profile_id": "selftest-extra-v1", "revision": "1.0",
                          "title": "Дополнительный профиль контроля прибора"},
                 "modes": [], "common_dependencies": []}
        with open(os.path.join(target, "release-profiles", "selftest-extra-v1.json"), "w",
                  encoding="utf-8") as handle:
            json.dump(extra, handle)
        entries = measured_profiles(root)
    expected = len(copied) + 1
    return (len(entries) == expected,
            f"каталог из {len(copied)} профилей плюс один даёт {len(entries)} записей манифеста "
            f"(ожидание {expected})")


def self_test():
    """The pure checks on fixed inputs: each refusal fires, and the clean case passes."""
    props = "<Project><PropertyGroup><Version>0.4.1</Version></PropertyGroup></Project>"
    sha = "a" * 40
    good = {"acceptance": {"verdict": "PASS", "fully_ready": True},
            "package": {"host_dll_sha256": "h"},
            "source": {"measured": True, "commit": sha, "tree_dirty": False}}
    dirty = dict(good, source=dict(good["source"], tree_dirty=True))
    failed_passport = dict(good, acceptance={"verdict": "FAIL", "fully_ready": False})
    cases = [
        (product_version(props) == "0.4.1", "версия читается из props"),
        (tag_problem("v0.4.1", "0.4.1") is None, "верный тег принят"),
        (tag_problem("v24-core-assemblies-v1", "0.4.1") is not None, "тег-описание отвергнут"),
        (tag_problem("v0.4", "0.4.1") is not None, "неполный номер отвергнут"),
        (tag_problem("v0.4.2", "0.4.1") is not None, "тег, отличный от версии, отвергнут"),
        (tag_problem("v0.4.1", None) is not None, "нет <Version> - отказ"),
        (stamps_in(f"x0.4.1+{sha}x".encode("utf-16-le")) == {("0.4.1", sha)}, "штамп UTF-16 читается"),
        (stamps_in(f"x0.4.1+{sha}x".encode("utf-8")) == {("0.4.1", sha)}, "штамп UTF-8 читается"),
        (stamp_problem("a.dll", {("0.4.1", sha)}, "0.4.1") is None, "верный штамп принят"),
        (stamp_problem("a.dll", {("1.0.0", sha)}, "0.4.1") is not None, "чужая версия отвергнута"),
        (stamp_problem("a.dll", set(), "0.4.1") is not None, "нет штампа - отказ"),
        (build_commit_problem({sha}) is None, "один коммит у всех сборок принят"),
        (build_commit_problem({sha, "b" * 40}) is not None, "разные коммиты у сборок отвергнуты"),
        (passport_problems(good, "h") == [], "годный паспорт принят"),
        (passport_problems(good, "other") != [], "паспорт чужой поставки отвергнут"),
        (passport_problems(dirty, "h") != [], "паспорт из грязного дерева отвергнут"),
        (passport_problems(failed_passport, "h") != [], "паспорт без PASS отвергнут"),
        (title_problems("KompasMCP 0.4.1 - уклоны", "0.4.1") == [], "заголовок с дефисом принят"),
        (title_problems("KompasMCP 0.4.1 \u2014 уклоны", "0.4.1") != [], "заголовок с длинным тире отвергнут"),
        (title_problems("KompasMCP 0.4.1 \u2013 уклоны", "0.4.1") != [], "заголовок со средним тире отвергнут"),
        (title_problems("KompasMCP 0.4.0 - уклоны", "0.4.1") != [], "заголовок с чужой версией отвергнут"),
        (title_problems("KompasMCP 0.4.1 - ", "0.4.1") != [], "заголовок без темы отвергнут"),
        (notes_problems("n.md", "# KompasMCP 0.4.1 - уклоны\n\nтекст - дефис\n",
                        "KompasMCP 0.4.1 - уклоны") == [], "заметки с дефисом приняты"),
        (notes_problems("n.md", "# KompasMCP 0.4.1 - уклоны\n\nтекст \u2014 тире\n",
                        "KompasMCP 0.4.1 - уклоны") != [], "заметки с длинным тире отвергнуты"),
        (notes_problems("n.md", "# KompasMCP 0.4.1 - другое\n", "KompasMCP 0.4.1 - уклоны") != [],
         "заметки под другим заголовком отвергнуты"),
        (notes_problems("n.md", None, "KompasMCP 0.4.1 - уклоны") != [], "нет заметок - отказ"),
        self_test_profile_catalog(),
    ]
    failed = [title for ok, title in cases if not ok]
    for ok, title in cases:
        print(("PASS " if ok else "FAIL ") + title)
    return 1 if failed else 0


# INVARIANT: vendor binaries never ship; the interop comes from the user's KOMPAS installation.
FORBIDDEN = re.compile(r"(^|/)(Interop\.Kompas|Kompas6|KAPITypes|.*\.local\.json$)", re.IGNORECASE)

EXTRA_FILES = {
    "LICENSE.txt": "LICENSE",
    "THIRD-PARTY-NOTICES.txt": "docs/distribution/THIRD-PARTY-NOTICES.txt",
    "INSTALL.md": "docs/distribution/INSTALL.md",
    "Install-KompasMcp.ps1": "scripts/Install-KompasMcp.ps1",
}


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    if "--self-test" in sys.argv[1:]:
        sys.exit(self_test())

    ap = argparse.ArgumentParser()
    ap.add_argument("--delivery", required=True)
    ap.add_argument("--tag", required=True)
    ap.add_argument("--passport", required=True)
    ap.add_argument("--title", required=True)
    ap.add_argument("--out")
    a = ap.parse_args()

    delivery = os.path.abspath(a.delivery)

    # Release identity is checked BEFORE anything is written: a refused release leaves no assets.
    with open(os.path.join(ROOT, "Directory.Build.props"), encoding="utf-8-sig") as f:
        version = product_version(f.read())
    problems = [p for p in [tag_problem(a.tag, version)] if p]
    problems += title_problems(a.title, version)
    notes_rel = f"docs/distribution/{a.tag}.md"
    notes_path = os.path.join(ROOT, notes_rel)
    notes_text = None
    if os.path.isfile(notes_path):
        with open(notes_path, encoding="utf-8-sig") as f:
            notes_text = f.read()
    problems += notes_problems(notes_rel, notes_text, a.title)
    for rel, src in list(EXTRA_FILES.items()) + [("config/kompas-mcp.example.json", None)]:
        path = os.path.join(ROOT, src) if src else os.path.join(delivery, rel)
        if os.path.isfile(path):
            with open(path, encoding="utf-8-sig") as f:
                problems += dash_problems(rel, f.read())
    commits = set()
    for name in OWN_ASSEMBLIES:
        with open(os.path.join(delivery, name), "rb") as f:
            stamps = stamps_in(f.read())
        commits |= {commit for _, commit in stamps}
        p = stamp_problem(name, stamps, version) if version is not None else None
        if p:
            problems.append(p)
    p = build_commit_problem(commits)
    if p:
        problems.append(p)
    build_commit = next(iter(commits)) if len(commits) == 1 else None

    with open(a.passport, encoding="utf-8-sig") as f:
        passport = json.load(f)
    problems += passport_problems(passport, sha256(os.path.join(delivery, "KompasMcp.Host.dll")))
    passport_commit = (passport.get("source") or {}).get("commit")
    if build_commit and passport_commit:
        p = sources_problem(build_commit, passport_commit)
        if p:
            problems.append(p)
    if problems:
        sys.exit("ОТКАЗ: выпуск не собран.\n  - " + "\n  - ".join(problems))

    # The scope is MEASURED here, before a single asset is written: a profile without wording stops
    # the release instead of producing a manifest that silently misses it.
    profiles = measured_profiles(ROOT)
    scope_text = (" + ".join(entry["scope_label"] for entry in profiles)
                  + " (" + ", ".join(entry["id"] for entry in profiles) + " закрыты полностью)")
    manifest_profiles = [{"id": entry["id"], "title": entry["title"],
                          "modes": entry["modes"], "deps": entry["deps"]} for entry in profiles]

    out = os.path.abspath(a.out or os.path.join(ROOT, "artifacts", "release", a.tag))
    os.makedirs(out, exist_ok=True)
    zip_name = f"KompasMCP-{a.tag}-win-x64.zip"
    zip_path = os.path.join(out, zip_name)

    files = []
    for base, _, names in os.walk(delivery):
        for n in names:
            full = os.path.join(base, n)
            rel = os.path.relpath(full, delivery).replace(os.sep, "/")
            if FORBIDDEN.search(rel):
                sys.exit(f"в поставке запрещённый файл: {rel}")
            files.append((rel, full))
    for rel in EXTRA_FILES:
        if any(r == rel for r, _ in files):
            sys.exit(f"добавляемый файл уже есть в поставке: {rel}")
    files += [(rel, os.path.join(ROOT, src)) for rel, src in EXTRA_FILES.items()]
    files.sort()

    schemas = sorted(r[len("schemas/"):-len(".json")] for r, _ in files
                     if r.startswith("schemas/kompas_") and r.endswith(".json"))
    if not os.path.isfile(os.path.join(delivery, "config", "kompas-mcp.example.json")):
        sys.exit("в поставке нет шаблона config/kompas-mcp.example.json")

    # Fixed timestamps keep the archive reproducible from the same inputs.
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for rel, full in files:
            info = zipfile.ZipInfo(rel, date_time=(2026, 10, 6, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            with open(full, "rb") as f:
                z.writestr(info, f.read())

    manifest = {
        "name": "KompasMCP",
        "tag": a.tag,
        "version": version,
        "platform": "win-x64",
        "zip": zip_name,
        "installer": "Install-KompasMcp.ps1",
        "scope": scope_text,
        "profiles": manifest_profiles,
        "not_included": [
            "массивы компонентов сборки",
            "полная спецификация (BOM)",
            "расширенные виды сопряжений сверх mates-minimal-v1",
            "спецификации и другие 2D-документы, кроме чертежей профиля drawings-minimal-v1",
            "создание и удаление переменных (kompas_set_variable меняет существующую внешнюю переменную)",
            f"полный каталог P6 (выпуск закрывает {profiles_phrase(len(profiles))} выше)",
        ],
        "tools_count": len(schemas),
        "tools": schemas,
        "server_info": {"name": "kompas-mcp", "version": version, "protocol": "2025-06-18"},
        "requirements": [
            "Windows x64",
            "КОМПАС-3D v24 x64, установленный, лицензированный и зарегистрированный для COM у текущего пользователя",
            ".NET 10 Runtime x64 (Microsoft.NETCore.App 10.x); подходит и .NET 10 Desktop Runtime x64. .NET SDK не нужен",
        ],
        "build_source_commit": build_commit,
        "file_count": len(files),
        "own_assemblies": {n: sha256(os.path.join(delivery, n)) for n in OWN_ASSEMBLIES},
        "integrity_note": "SHA256SUMS.txt проверяет целостность скачанного файла, а не личность издателя: "
                          "цифровой подписи у выпуска нет.",
    }
    manifest["title"] = a.title
    # INVARIANT: a refused release leaves no assets, so the ZIP written above goes too.
    problems = dash_problems("release-manifest.json", json.dumps(manifest, ensure_ascii=False, indent=2))
    if problems:
        os.remove(zip_path)
        sys.exit("ОТКАЗ: выпуск не собран.\n  - " + "\n  - ".join(problems))
    manifest_path = os.path.join(out, "release-manifest.json")
    with open(manifest_path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)
        f.write("\n")

    installer = os.path.join(out, "Install-KompasMcp.ps1")
    with open(os.path.join(ROOT, EXTRA_FILES["Install-KompasMcp.ps1"]), "rb") as src, open(installer, "wb") as dst:
        dst.write(src.read())

    with open(os.path.join(out, "SHA256SUMS.txt"), "w", encoding="ascii", newline="\n") as f:
        for p in (zip_path, manifest_path, installer):
            f.write(f"{sha256(p)}  {os.path.basename(p)}\n")

    assets = " ".join(f'"{os.path.join(out, n)}"' for n in
                      (zip_name, "release-manifest.json", "SHA256SUMS.txt", "Install-KompasMcp.ps1"))
    print(json.dumps({"out": out, "zip": zip_name, "zip_sha256": sha256(zip_path), "files": len(files),
                      "tools": len(schemas), "title": a.title, "notes": notes_rel,
                      "publish_command": f'gh release create {a.tag} --title "{a.title}" '
                                         f'--notes-file {notes_rel} {assets}',
                      "after_publish_check": f"python scripts/lint-dashes.py --release {a.tag}"},
                     ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
