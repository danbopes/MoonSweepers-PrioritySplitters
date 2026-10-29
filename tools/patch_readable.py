"""Diagnostic: flip the splitter body mesh's m_IsReadable byte in place inside resources.assets, so the running
game exposes its own decode of the mesh. Only that one byte changes. Restore with
`cp tools/backup/resources.assets.orig <assets>` or a Steam file verification."""
import sys, os, shutil
import UnityPy

assets, backup = sys.argv[1], sys.argv[2]
if not os.path.exists(backup):
    shutil.copy2(assets, backup)
    print("backed up to", backup)
if os.path.getsize(backup) != os.path.getsize(assets):
    sys.exit("assets file size differs from the backup; restore it first")

env = UnityPy.load(assets)
target = None
for obj in env.objects:
    if obj.type.name == "Mesh" and obj.read().m_Name == "SM_splitter.001":
        target = obj
if target is None:
    sys.exit("mesh not found")
raw = bytes(target.get_raw_data())
with open(assets, "rb") as f:
    f.seek(target.byte_start)
    chunk = f.read(target.byte_size)
if chunk != raw:
    sys.exit(f"raw object bytes at {target.byte_start} do not match the parsed object; aborting")

# m_MeshCompression=2, m_IsReadable=0, m_KeepVertices=0, m_KeepIndices=0, m_IndexFormat=1 (int32), m_IndexBuffer size=0
pattern = b"\x02\x00\x00\x00\x01\x00\x00\x00\x00\x00\x00\x00"
pos = raw.find(pattern)
if pos < 0 or raw.find(pattern, pos + 1) >= 0:
    sys.exit(f"readable flag pattern not unique (first at {pos})")
print(f"object at {target.byte_start} size {target.byte_size}; flag byte at object offset {pos + 1}")
with open(assets, "r+b") as f:
    f.seek(target.byte_start + pos + 1)
    f.write(b"\x01")

env2 = UnityPy.load(assets)
for obj in env2.objects:
    if obj.type.name == "Mesh":
        d = obj.read()
        if d.m_Name == "SM_splitter.001":
            print("after: readable", d.m_IsReadable, "compression", d.m_MeshCompression, "keep", d.m_KeepVertices, d.m_KeepIndices,
                  "index format", d.m_IndexFormat, "vertex items", d.m_CompressedMesh.m_Vertices.m_NumItems, "submeshes", len(d.m_SubMeshes))
print("sizes: original", os.path.getsize(backup), "patched", os.path.getsize(assets))
