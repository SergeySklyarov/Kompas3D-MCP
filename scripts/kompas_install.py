"""The KOMPAS installation root - one order for every instrument of this project.

  python scripts/kompas_install.py [--self-test]

WHY ONE PLACE. The installation root is a property of the MACHINE, not of the product. Instruments
that carried their own literal `D:\\Programs\\KOMPAS-3Dv24` all failed on a machine where KOMPAS is
installed elsewhere, and the failure read as a product defect ("interop not loaded", "no such file")
instead of as a wrong machine path.

INVARIANT: the root is taken in this order - `KOMPAS_MCP_INSTALL`, then `KompasRoot` of
`Directory.Build.props.local` in the repository root, then the historical default
`D:/Programs/KOMPAS-3Dv24`. The environment variable is first because it is the operator's explicit
instruction for one run; the props file is second because the build reads the same file, so an
instrument and the build it measures cannot disagree about which installation is installed; the
default is last so an unknown machine behaves exactly as it did before.

LIMIT: only `KompasRoot` is read from the props file. Its other two properties (`KompasInteropDir`,
`KompasTlbDir`) are derived from the root here rather than taken from the file.
"""

from __future__ import annotations

import argparse
import os
import sys
import tempfile
import xml.etree.ElementTree as ET

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ENV_VAR = "KOMPAS_MCP_INSTALL"
LOCAL_PROPS = "Directory.Build.props.local"
ROOT_PROPERTY = "KompasRoot"
DEFAULT_INSTALL = "D:/Programs/KOMPAS-3Dv24"

# The source is prose for the operator, so it stays Russian like the other instrument messages.
SOURCE_ENV = "переменная окружения " + ENV_VAR
SOURCE_LOCAL = LOCAL_PROPS + ": " + ROOT_PROPERTY
SOURCE_DEFAULT = "значение по умолчанию (ни " + ENV_VAR + ", ни " + LOCAL_PROPS + ")"


def local_root(props_path):
    """`KompasRoot` of a props file, or None when the file does not supply one.

    XML, not MSBuild: an instrument must not need a build to locate an installation. A file that is
    missing, unreadable or malformed, and a property that is present but EMPTY, all give None - an
    empty root would join into a relative path and the instrument would silently measure a
    different tree than the one it names in its report.
    """
    try:
        tree = ET.parse(props_path)
    except (OSError, ET.ParseError):
        return None
    for element in tree.iter():
        if element.tag.split("}")[-1] != ROOT_PROPERTY:
            continue
        value = (element.text or "").strip()
        if value:
            return value
    return None


def resolve(repo_root=None, environ=None):
    """`(root, source)`: the installation root and where it came from.

    The source travels with the path because a printed root without its origin makes a wrong root
    indistinguishable from a right one. `repo_root` and `environ` are injectable so that the
    self-test can exercise every branch without touching this machine.
    """
    root = repo_root or ROOT
    env = os.environ if environ is None else environ
    from_env = (env.get(ENV_VAR) or "").strip()
    if from_env:
        return from_env, SOURCE_ENV
    from_local = local_root(os.path.join(root, LOCAL_PROPS))
    if from_local:
        return from_local, SOURCE_LOCAL
    return DEFAULT_INSTALL, SOURCE_DEFAULT


def install_root(repo_root=None, environ=None):
    """The root of the KOMPAS installation on this machine."""
    return resolve(repo_root, environ)[0]


def install_source(repo_root=None, environ=None):
    """Where `install_root()` took its value from, for the instrument's own output."""
    return resolve(repo_root, environ)[1]


def kompas_exe(root=None):
    """`<root>/Bin/KOMPAS.Exe` - the local server of the installation."""
    return os.path.join(root or install_root(), "Bin", "KOMPAS.Exe")


def interop_dir(root=None):
    """`<root>/Libs/PolynomLib/Bin/Client` - the vendor interop assemblies."""
    return os.path.join(root or install_root(), "Libs", "PolynomLib", "Bin", "Client")


def tlb_path(root=None):
    """`<root>/Bin/kAPI7.tlb` - the authoritative API7 type library."""
    return os.path.join(root or install_root(), "Bin", "kAPI7.tlb")


def self_test():
    """Every branch of the order, on temporary files and a passed-in environment.

    The test never reads this machine's installation: it proves the ORDER, and the live check that
    the real props file is picked up is a separate measurement of the real machine.
    """
    failures = []
    checks = 0

    def check(name, condition, detail=""):
        nonlocal checks
        checks += 1
        print(f"  [{'PASS' if condition else 'FAIL'}] {name}" + (f" - {detail}" if detail else ""))
        if not condition:
            failures.append(name)

    def write_props(path, body):
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(body)

    with tempfile.TemporaryDirectory() as tmp:
        env_root = os.path.join(tmp, "env-root")
        props = os.path.join(tmp, LOCAL_PROPS)
        local_root_value = os.path.join(tmp, "from-local")

        check("переменная окружения задаёт корень", install_root(tmp, {ENV_VAR: env_root}) == env_root)
        check("источник назван как переменная окружения",
              install_source(tmp, {ENV_VAR: env_root}) == SOURCE_ENV)

        write_props(props, f"<Project><PropertyGroup><{ROOT_PROPERTY}>{local_root_value}"
                           f"</{ROOT_PROPERTY}></PropertyGroup></Project>")
        check(".local задаёт корень, когда переменной нет", install_root(tmp, {}) == local_root_value)
        check("источник назван как .local", install_source(tmp, {}) == SOURCE_LOCAL)
        check("переменная окружения побеждает .local",
              install_root(tmp, {ENV_VAR: env_root}) == env_root)
        check("путь из .local разбирается без MSBuild",
              local_root(props) == local_root_value)

        write_props(props, f"<Project><PropertyGroup><{ROOT_PROPERTY}></{ROOT_PROPERTY}>"
                           f"</PropertyGroup></Project>")
        check("пустое свойство не принимается за путь", install_root(tmp, {}) == DEFAULT_INSTALL)
        write_props(props, "<Project><PropertyGroup>")
        check("сломанный XML не принимается за путь", install_root(tmp, {}) == DEFAULT_INSTALL)
        write_props(props, "<Project><PropertyGroup><Other>1</Other></PropertyGroup></Project>")
        check("чужое свойство не принимается за путь", install_root(tmp, {}) == DEFAULT_INSTALL)
        os.remove(props)
        check("без файла .local действует прежнее значение по умолчанию",
              install_root(tmp, {}) == DEFAULT_INSTALL and install_source(tmp, {}) == SOURCE_DEFAULT)
        check("пустая переменная окружения не принимается за путь",
              install_root(tmp, {ENV_VAR: "   "}) == DEFAULT_INSTALL)

        check("производные пути идут от корня",
              kompas_exe("R") == os.path.join("R", "Bin", "KOMPAS.Exe")
              and interop_dir("R") == os.path.join("R", "Libs", "PolynomLib", "Bin", "Client")
              and tlb_path("R") == os.path.join("R", "Bin", "kAPI7.tlb"))

    print(f"\nСамопроверка прибора: {checks} проверок, "
          f"{len(failures)} FAIL" + (f" - {failures}" if failures else ""))
    return 1 if failures else 0


def main():
    parser = argparse.ArgumentParser(description="Корень установки КОМПАС для приборов проекта.")
    parser.add_argument("--self-test", action="store_true", help="проверить прибор, ничего не измеряя")
    args = parser.parse_args()

    if args.self_test:
        return self_test()

    print(f"{install_root()} (источник: {install_source()})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
