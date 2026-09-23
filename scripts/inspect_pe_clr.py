"""Read-only PE/CLR inspector: bitness, 32BITREQUIRED flag, strong-name, metadata version.

Why this exists: the vendor interop assemblies under the KOMPAS install were produced by tlbimp
on whatever toolchain ASCON used. If any of them carries COMIMAGE_FLAGS_32BITREQUIRED, an x64
worker process cannot load it and we must generate our own interop from the .tlb instead.
That is a P0 blocker, so it is checked here before any COM call is attempted.
"""

import struct
import sys

COMIMAGE_FLAGS = {
    0x00000001: "ILOnly",
    0x00000002: "32BitRequired",
    0x00000004: "ILLibrary",
    0x00000008: "StrongNameSigned",
    0x00000010: "NativeEntryPoint",
    0x00000020: "32BitPref",
    0x00010000: "TrackDebugData",
}


def rva_to_off(sections, rva):
    for s in sections:
        name, vsize, vaddr, rawsize, rawptr = s
        if vaddr <= rva < vaddr + max(vsize, rawsize):
            return rawptr + (rva - vaddr)
    return None


def inspect(path):
    with open(path, "rb") as fh:
        data = fh.read()
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    assert data[pe:pe + 4] == b"PE\0\0", "not a PE file"
    machine, nsec = struct.unpack_from("<HH", data, pe + 4)
    opt_size = struct.unpack_from("<H", data, pe + 20)[0]
    opt = pe + 24
    magic = struct.unpack_from("<H", data, opt)[0]
    pe_kind = {0x10B: "PE32", 0x20B: "PE32+"}.get(magic, hex(magic))
    # data directories start after the standard+windows fields of the optional header
    dd_off = opt + (96 if magic == 0x10B else 112)
    nrva = struct.unpack_from("<I", data, opt + (92 if magic == 0x10B else 108))[0]
    sec_off = opt + opt_size
    sections = []
    for i in range(nsec):
        base = sec_off + i * 40
        name = data[base:base + 8].rstrip(b"\0").decode("latin1")
        # IMAGE_SECTION_HEADER: Name[8], VirtualSize, VirtualAddress, SizeOfRawData, PointerToRawData
        vsize, vaddr, rawsize, rawptr = struct.unpack_from("<IIII", data, base + 8)
        sections.append((name, vsize, vaddr, rawsize, rawptr))
    out = {"path": path, "machine": hex(machine), "pe": pe_kind, "subsystem": None,
           "clr_flags": None, "runtime": None, "dd_count": nrva}
    # COM descriptor directory is index 14.
    # IMAGE_COR20_HEADER: cb(0) major/minor(4) MetaData{RVA,Size}(8) Flags(16) EntryPoint(20)
    # Only the flags are parsed: that is the field that decides whether an x64 process can
    # load the assembly. The metadata version string is deliberately not decoded here.
    if nrva > 14:
        clr_rva, clr_size = struct.unpack_from("<II", data, dd_off + 14 * 8)
        if clr_rva:
            off = rva_to_off(sections, clr_rva)
            if off is None:
                out["clr_flags"] = "<CLR directory RVA not mapped>"
            else:
                flags = struct.unpack_from("<I", data, off + 16)[0]
                names = [n for bit, n in COMIMAGE_FLAGS.items() if flags & bit]
                out["clr_flags"] = f"0x{flags:08X} ({'|'.join(names) or 'none'})"
    return out


if __name__ == "__main__":
    for p in sys.argv[1:]:
        info = inspect(p)
        print(f"{info['path']}")
        print(f"   machine={info['machine']} pe={info['pe']} clr={info['clr_flags']}")
        if info["clr_flags"] and "32BitRequired" in info["clr_flags"]:
            print("   >>> 32BITREQUIRED: cannot be loaded by an x64 process")
