"""Dump the body and cone of a *-cone.bin blob as a flat binary for the three.js header render."""
import sys, struct, zlib
import numpy as np

def read_mesh(buf, pos, tangents):
    n, subs, nidx = struct.unpack_from("<III", buf, pos); pos += 12
    table = []
    for _ in range(subs):
        table.append(struct.unpack_from("<III", buf, pos)); pos += 12
    V = np.frombuffer(buf, dtype="<f4", count=n * 3, offset=pos).reshape(n, 3); pos += n * 12
    N = np.frombuffer(buf, dtype="<i1", count=n * 3, offset=pos).reshape(n, 3).astype(np.float32) / 127.0; pos += n * 3
    pos += n * 8
    if tangents:
        pos += n * 16
    I = np.frombuffer(buf, dtype="<u4", count=nidx, offset=pos); pos += nidx * 4
    return V, N, table, I, pos

src, out = sys.argv[1], sys.argv[2]
raw = zlib.decompress(open(src, "rb").read(), -15)
assert raw[:4] == b"PSC1"
n_src, flags = struct.unpack_from("<II", raw, 4)
pivot = struct.unpack_from("<fff", raw, 12)
pos = 28
parts = []
for name in ("body", "cone"):
    V, N, table, I, pos = read_mesh(raw, pos, flags & 1)
    parts.append((name, V, N, table, I))
# layout: magic 'SCN1', f32 pivot[3], u32 partCount; per part: u32 vertexCount, u32 subCount, u32 indexCount,
# (u32 material, u32 start, u32 count)*subCount, f32 pos[3n], f32 nrm[3n], u32 idx[indexCount]
with open(out, "wb") as f:
    f.write(b"SCN1"); f.write(struct.pack("<fffI", *pivot, len(parts)))
    for name, V, N, table, I in parts:
        f.write(struct.pack("<III", len(V), len(table), len(I)))
        for m, s, c in table:
            f.write(struct.pack("<III", m, s, c))
        f.write(np.ascontiguousarray(V, dtype="<f4").tobytes())
        f.write(np.ascontiguousarray(N, dtype="<f4").tobytes())
        f.write(np.ascontiguousarray(I, dtype="<u4").tobytes())
        print(f"{src}: {name} {len(V)} verts, {len(I)//3} tris, submeshes {[(m, c//3) for m, s, c in table]}")
print("pivot", pivot)
