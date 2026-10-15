"""Repairs the dangling TypeRefs in a merged 0Harmony.dll so that Burst can read it.

Harmony 2.3+ ships MonoMod, Mono.Cecil and iced merged into 0Harmony.dll. The merge drops their AssemblyRef rows but
leaves ~10 TypeRefs pointing at those rows, which no longer exist. Mono only resolves a TypeRef when it is used and
never uses these, so Harmony works; Burst hashes every TypeRef of every editor assembly up front, throws
"Failed to find entry-points ... Error while hashing type reference" and then compiles no job at all in the editor.

Every dangling TypeRef names a type the DLL defines itself, so its ResolutionScope is re-pointed at this module.
The script refuses to write if any of them is not defined here. Re-run it after every Harmony upgrade:

    python Tools/fix-harmony-typerefs.py Assets/Plugins/Harmony/net48/0Harmony.dll --write

Without --write it only lists what it would change; a repaired DLL lists nothing.
"""
import struct
import sys

path = sys.argv[1]
write = '--write' in sys.argv
d = bytearray(open(path, 'rb').read())

pe = struct.unpack_from('<I', d, 0x3c)[0]
sectionCount = struct.unpack_from('<H', d, pe + 6)[0]
optionalSize = struct.unpack_from('<H', d, pe + 20)[0]
optional = pe + 24
dataDirectories = optional + (96 if struct.unpack_from('<H', d, optional)[0] == 0x10b else 112)
sections = []
for i in range(sectionCount):
    virtualSize, virtualAddress, rawSize, rawPointer = struct.unpack_from('<IIII', d, pe + 24 + optionalSize + i * 40 + 8)
    sections.append((virtualAddress, max(virtualSize, rawSize), rawPointer))


def fileOffset(rva):
    for virtualAddress, size, rawPointer in sections:
        if virtualAddress <= rva < virtualAddress + size: return rva - virtualAddress + rawPointer
    raise ValueError(f'RVA {rva:#x} is in no section')


cliHeader = fileOffset(struct.unpack_from('<I', d, dataDirectories + 14 * 8)[0])
if struct.unpack_from('<I', d, cliHeader + 16)[0] & 8: sys.exit('refusing: the DLL is strong-name signed and a patch would break the signature')
metadata = fileOffset(struct.unpack_from('<I', d, cliHeader + 8)[0])
versionLength = struct.unpack_from('<I', d, metadata + 12)[0]
p = metadata + 16 + versionLength
streamCount = struct.unpack_from('<H', d, p + 2)[0]
p += 4
streams = {}
for i in range(streamCount):
    streamOffset = struct.unpack_from('<I', d, p)[0]
    p += 8
    end = d.index(b'\0', p)
    streams[d[p:end].decode()] = metadata + streamOffset
    p = (end + 4) & ~3

tables = streams['#~']
heapSizes = d[tables + 6]
valid = struct.unpack_from('<Q', d, tables + 8)[0]
p = tables + 24
rows = {}
for i in range(64):
    if valid >> i & 1:
        rows[i] = struct.unpack_from('<I', d, p)[0]
        p += 4
MODULE, TYPEREF, TYPEDEF, FIELDPTR, FIELD, METHODPTR, METHOD, MODULEREF, TYPESPEC, ASSEMBLYREF, EXPORTEDTYPE = 0, 1, 2, 3, 4, 5, 6, 0x1a, 0x1b, 0x23, 0x27
if heapSizes & 0x20 or FIELDPTR in rows or METHODPTR in rows or EXPORTEDTYPE in rows: sys.exit('refusing: unexpected metadata layout')

stringWidth = 4 if heapSizes & 1 else 2
guidWidth = 4 if heapSizes & 2 else 2
def codedWidth(targets, tagBits): return 4 if max(rows.get(t, 0) for t in targets) >= (1 << (16 - tagBits)) else 2
def indexWidth(table): return 4 if rows.get(table, 0) >= 0x10000 else 2
def read(offset, width): return struct.unpack_from('<I' if width == 4 else '<H', d, offset)[0]
def string(offset):
    start = streams['#Strings'] + offset
    return d[start:d.index(b'\0', start)].decode('utf8', 'replace')

scopeTables = [MODULE, MODULEREF, ASSEMBLYREF, TYPEREF]
scopeWidth = codedWidth(scopeTables, 2)
typeRefSize = scopeWidth + 2 * stringWidth
typeRefStart = p + rows[MODULE] * (2 + stringWidth + 3 * guidWidth)
typeDefSize = 4 + 2 * stringWidth + codedWidth([TYPEDEF, TYPEREF, TYPESPEC], 2) + indexWidth(FIELD) + indexWidth(METHOD)
typeDefStart = typeRefStart + rows[TYPEREF] * typeRefSize
defined = {(string(read(typeDefStart + r * typeDefSize + 4 + stringWidth, stringWidth)), string(read(typeDefStart + r * typeDefSize + 4, stringWidth)))
           for r in range(rows[TYPEDEF])}

fixes = []
for row in range(1, rows[TYPEREF] + 1):
    entry = typeRefStart + (row - 1) * typeRefSize
    scope = read(entry, scopeWidth)
    tag, scopeRow = scope & 3, scope >> 2
    if tag == 3 or 0 < scopeRow <= rows.get(scopeTables[tag], 0): continue
    name = (string(read(entry + scopeWidth + stringWidth, stringWidth)), string(read(entry + scopeWidth, stringWidth)))
    print(f'TypeRef {row:#x} -> {["Module", "ModuleRef", "AssemblyRef", "TypeRef"][tag]} row {scopeRow}: {name[0]}.{name[1]}')
    if name not in defined: sys.exit(f'refusing: {name[0]}.{name[1]} is not defined in this DLL')
    fixes.append(entry)

print(f'{len(fixes)} dangling TypeRef(s)')
if write and fixes:
    for entry in fixes: struct.pack_into('<I' if scopeWidth == 4 else '<H', d, entry, (1 << 2) | 0)
    open(path, 'wb').write(d)
    print('written')
