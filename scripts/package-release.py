"""Packs an accepted delivery folder into the public release assets.

Usage:
  python scripts/package-release.py --delivery artifacts/<delivery> --tag vX.Y.Z
                                    --passport docs/acceptance/<delivery>/delivery-passport.json
                                    [--out artifacts/release/<tag>]
  python scripts/package-release.py --self-test

INVARIANT (release identity), checked before a single file is written:
  - the tag is "v" + <Version> from Directory.Build.props;
  - every own assembly carries the same stamp "<Version>+<commit>"; that commit is the build commit;
  - the passport belongs to this delivery (Host.dll hash), says PASS / fully_ready, from a clean tree;
  - product sources are identical at the build commit and at the passport commit. The SDK stamps HEAD
    even for a dirty tree, so this diff is what reveals a delivery built from uncommitted sources.
History: docs/decisions/releases.md#versioning

Writes three assets next to each other, ready for a GitHub Release:
  KompasMCP-<tag>-win-x64.zip   the delivery as is, plus LICENSE, notices, install notes, installer
  release-manifest.json         public manifest: tag, scope, requirements, own assembly hashes
  SHA256SUMS.txt                SHA-256 of the ZIP, the manifest and the installer

INVARIANT: the delivery is copied byte for byte; this script adds files, never rebuilds or edits a
binary. A release that needs a different binary needs a new accepted delivery first.
"""

import argparse
import hashlib
import json
import os
import re
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

OWN_ASSEMBLIES = [
    "KompasMcp.Host.exe", "KompasMcp.Host.dll", "KompasMcp.Worker.exe", "KompasMcp.Worker.dll",
    "KompasMcp.Api5Adapter.dll", "KompasMcp.Domain.dll", "KompasMcp.Contracts.dll",
]

# INVARIANT: the numbers here are the ones acceptance-levels.py measures, not the ones a release
# would like to claim. A profile whose actions are not all verified is listed with its real ratio and
# named as open in `not_included`, never rounded up to "closed".
PROFILES = [
    {"id": "mechanical-core-v1", "title": "Детали: твердотельное моделирование", "modes": "54/54", "deps": "15/15"},
    {"id": "assemblies-minimal-v1", "title": "Сборки", "modes": "7/7", "deps": "5/5"},
    {"id": "mates-minimal-v1", "title": "Сопряжения", "modes": "6/6", "deps": "5/5"},
    {"id": "drawings-minimal-v1", "title": "Чертежи: виды, размеры, штамп, DXF/DWG, техтребования", "modes": "6/6", "deps": "6/6"},
    {"id": "variables-material-minimal-v1", "title": "Переменные и материал детали", "modes": "5/5", "deps": "5/5"},
]

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
        (tag_problem("v0.4.1", None) is not None, "нет <Version> — отказ"),
        (stamps_in(f"x0.4.1+{sha}x".encode("utf-16-le")) == {("0.4.1", sha)}, "штамп UTF-16 читается"),
        (stamps_in(f"x0.4.1+{sha}x".encode("utf-8")) == {("0.4.1", sha)}, "штамп UTF-8 читается"),
        (stamp_problem("a.dll", {("0.4.1", sha)}, "0.4.1") is None, "верный штамп принят"),
        (stamp_problem("a.dll", {("1.0.0", sha)}, "0.4.1") is not None, "чужая версия отвергнута"),
        (stamp_problem("a.dll", set(), "0.4.1") is not None, "нет штампа — отказ"),
        (build_commit_problem({sha}) is None, "один коммит у всех сборок принят"),
        (build_commit_problem({sha, "b" * 40}) is not None, "разные коммиты у сборок отвергнуты"),
        (passport_problems(good, "h") == [], "годный паспорт принят"),
        (passport_problems(good, "other") != [], "паспорт чужой поставки отвергнут"),
        (passport_problems(dirty, "h") != [], "паспорт из грязного дерева отвергнут"),
        (passport_problems(failed_passport, "h") != [], "паспорт без PASS отвергнут"),
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
    ap.add_argument("--out")
    a = ap.parse_args()

    delivery = os.path.abspath(a.delivery)

    # Release identity is checked BEFORE anything is written: a refused release leaves no assets.
    with open(os.path.join(ROOT, "Directory.Build.props"), encoding="utf-8-sig") as f:
        version = product_version(f.read())
    problems = [p for p in [tag_problem(a.tag, version)] if p]
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
        "scope": "детали + сборки + сопряжения + чертежи + переменные и материал (mechanical-core-v1, "
                 "assemblies-minimal-v1, mates-minimal-v1, drawings-minimal-v1, "
                 "variables-material-minimal-v1 закрыты полностью)",
        "profiles": PROFILES,
        "not_included": [
            "массивы компонентов сборки",
            "полная спецификация (BOM)",
            "расширенные виды сопряжений сверх mates-minimal-v1",
            "спецификации и другие 2D-документы, кроме чертежей профиля drawings-minimal-v1",
            "создание и удаление переменных (kompas_set_variable меняет существующую внешнюю переменную)",
            "полный каталог P6 (выпуск закрывает пять профилей выше)",
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

    print(json.dumps({"out": out, "zip": zip_name, "zip_sha256": sha256(zip_path), "files": len(files),
                      "tools": len(schemas)}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
