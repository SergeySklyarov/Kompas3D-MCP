"""Packs an accepted delivery folder into the public release assets.

Usage:
  python scripts/package-release.py --delivery artifacts/<delivery> --tag <tag> --build-commit <sha>
                                    [--out artifacts/release/<tag>]

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
    {"id": "variables-material-minimal-v1", "title": "Переменные и материал детали (профиль открыт)", "modes": "3/5", "deps": "4/5"},
]

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
    ap = argparse.ArgumentParser()
    ap.add_argument("--delivery", required=True)
    ap.add_argument("--tag", required=True)
    ap.add_argument("--build-commit", required=True)
    ap.add_argument("--out")
    a = ap.parse_args()

    delivery = os.path.abspath(a.delivery)
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
        "platform": "win-x64",
        "zip": zip_name,
        "installer": "Install-KompasMcp.ps1",
        "scope": "детали + сборки + сопряжения + чертежи (mechanical-core-v1, assemblies-minimal-v1, "
                 "mates-minimal-v1, drawings-minimal-v1 закрыты полностью); переменные и материал "
                 "(variables-material-minimal-v1) идут с открытыми действиями",
        "profiles": PROFILES,
        "not_included": [
            "массивы компонентов сборки",
            "полная спецификация (BOM)",
            "расширенные виды сопряжений сверх mates-minimal-v1",
            "спецификации и другие 2D-документы, кроме чертежей профиля drawings-minimal-v1",
            "закрытие профиля variables-material-minimal-v1: открыто 3 действия (единица чтения "
            "плотности не подтверждена документом), инструменты переменных и материала при этом в пакете",
            "полный каталог P6 (выпуск закрывает четыре профиля выше)",
        ],
        "tools_count": len(schemas),
        "tools": schemas,
        "server_info": {"name": "kompas-mcp", "version": "0.1.0-preview", "protocol": "2025-06-18"},
        "requirements": [
            "Windows x64",
            "КОМПАС-3D v24 x64, установленный, лицензированный и зарегистрированный для COM у текущего пользователя",
            ".NET 10 Runtime x64 (Microsoft.NETCore.App 10.x); подходит и .NET 10 Desktop Runtime x64. .NET SDK не нужен",
        ],
        "build_source_commit": a.build_commit,
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
