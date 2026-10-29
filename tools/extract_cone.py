"""Split a machine body mesh into a cone-less body and a separate letter cone.

The S cone on a splitter and the M cone on a merger are baked into the body meshes SM_splitter.001 and
SM_merger.001, which ship non-readable, so the mod cannot separate them at runtime. This writes
src/Resources/<model>-cone.bin: both halves, deflated, in the layout PriorityCone.cs reads. Run with the
UnityPy virtualenv:

    python -I tools/extract_cone.py "<game>/MoonSweepers_Data/resources.assets" splitter src/Resources/splitter-cone.bin
    python -I tools/extract_cone.py "<game>/MoonSweepers_Data/resources.assets" merger src/Resources/merger-cone.bin
"""
import struct, sys, zlib
import UnityPy
from UnityPy.helpers.MeshHelper import MeshHandler

# The merger model is the splitter mirrored along the flow axis, so its cone sits at the opposite z.
MODELS = {
    "splitter": ("SM_splitter.001", (0.5, 0.72)),
    "merger": ("SM_merger.001", (-0.72, -0.5)),
}
# Submeshes that make up the cone: graphite body, cyan letters, brushed tip.
CONE_SUBMESHES = (2, 4, 5)


def main(assets, model, out):
    mesh_name, (z_min, z_max) = MODELS[model]

    def in_cone(v):
        x, y, z = v
        return y > 1.0 and abs(x) < 0.1 and z_min < z < z_max

    env = UnityPy.load(assets)
    mesh = None
    bump = []
    for obj in env.objects:
        if obj.type.name == "Mesh":
            d = obj.read()
            if d.m_Name == mesh_name:
                mesh = d
        elif obj.type.name == "Material":
            d = obj.read()
            if d.m_Name.startswith("MC - "):
                envs = d.m_SavedProperties.m_TexEnvs
                envs = envs.items() if isinstance(envs, dict) else envs
                for name, te in envs:
                    if te.m_Texture and te.m_Texture.path_id and name in ("_BumpMap", "_BaseMap", "_MainTex"):
                        bump.append((d.m_Name, name))
    if mesh is None:
        sys.exit(f"{mesh_name} not found in {assets}")
    print("textured material slots:", bump or "none")

    h = MeshHandler(mesh)
    h.process()
    V, N, UV, T = h.m_Vertices, h.m_Normals, h.m_UV0, h.m_Tangents
    idx = h.m_IndexBuffer
    isz = 2 if mesh.m_IndexFormat == 0 else 4
    n = len(V)
    print(f"{mesh_name}: {n} verts, {len(idx)} indices, tangents={bool(T)}")

    # Unity 6 packs every mesh LOD level into the index buffer, one range after another per submesh, and draws
    # only the level the LOD curve picks. Take level 0, the full-detail one; the rest would draw on top of it.
    lod = getattr(mesh, "m_MeshLodInfo", None)
    subs = []
    for i, sm in enumerate(mesh.m_SubMeshes):
        start = sm.firstByte // isz
        count = sm.indexCount
        if lod is not None and lod.m_SubMeshes and lod.m_SubMeshes[i].m_Levels:
            level = lod.m_SubMeshes[i].m_Levels[0]
            start += level.m_IndexStart
            count = level.m_IndexCount
        subs.append(list(idx[start:start + count]))
    print("index counts per submesh (LOD 0):", [len(s) for s in subs], "of", [sm.indexCount for sm in mesh.m_SubMeshes])

    # Cut by triangle, not by vertex: the cone's base ring shares vertices with the trim around it, so a
    # shared vertex is copied into whichever half references it.
    cone_tris = {}
    body_tris = {}
    for i, tri in enumerate(subs):
        c, b = [], []
        for t in range(0, len(tri), 3):
            tv = tri[t:t + 3]
            m = sum(1 for v in tv if in_cone(V[v])) if i in CONE_SUBMESHES else 0
            if m == 3:
                c.extend(tv)
            elif m == 0:
                b.extend(tv)
            else:
                sys.exit(f"submesh {i} has a triangle straddling the cone cut")
        cone_tris[i] = c
        body_tris[i] = b
    for i in CONE_SUBMESHES:
        if not cone_tris[i]:
            sys.exit(f"submesh {i} has no triangles inside the cone region; the model layout differs from the splitter's")

    cone = set(cone_tris[2])
    base_y = min(V[v][1] for v in cone)
    base = [V[v] for v in cone if V[v][1] < base_y + 0.01]
    pivot = (sum(v[0] for v in base) / len(base), base_y, sum(v[2] for v in base) / len(base))
    radius = max(((v[0] - pivot[0]) ** 2 + (v[2] - pivot[2]) ** 2) ** 0.5 for v in base)
    print("cone triangles per submesh:", {i: len(cone_tris[i]) // 3 for i in CONE_SUBMESHES})
    print(f"cone: {sum(len(t) for t in cone_tris.values()) // 3} triangles, pivot {pivot}, base radius {radius:.4f}")

    def pack(offset, submeshes, keep_all=False):
        # The body keeps every vertex at its original index so its vertex buffer is identical to the stock
        # mesh; only the cone's triangles are dropped. The cone is a new object and takes just what it uses.
        order = list(range(n)) if keep_all else sorted({v for _, tri in submeshes for v in tri})
        remap = {v: i for i, v in enumerate(order)}
        out = bytearray()
        table = []
        indices = []
        for material, tri in submeshes:
            if not tri:
                continue
            table.append((material, len(indices), len(tri)))
            indices.extend(remap[v] for v in tri)
        out += struct.pack("<III", len(order), len(table), len(indices))
        for material, start, count in table:
            out += struct.pack("<III", material, start, count)
        for v in order:
            p = V[v]
            out += struct.pack("<fff", p[0] - offset[0], p[1] - offset[1], p[2] - offset[2])
        for v in order:
            nx, ny, nz = N[v]
            out += struct.pack("<bbb", *(max(-127, min(127, round(c * 127))) for c in (nx, ny, nz)))
        for v in order:
            out += struct.pack("<ff", *UV[v][:2])
        if T:
            for v in order:
                out += struct.pack("<ffff", *T[v][:4])
        out += struct.pack(f"<{len(indices)}I", *indices)
        return out, table

    body, body_table = pack((0.0, 0.0, 0.0), [(i, body_tris[i]) for i in range(len(subs))], keep_all=True)
    cone_bytes, cone_table = pack(pivot, [(i, cone_tris[i]) for i in CONE_SUBMESHES])
    print("body submeshes:", body_table)
    print("cone submeshes:", cone_table)

    header = b"PSC1" + struct.pack("<II", n, 1 if T else 0) + struct.pack("<ffff", *pivot, radius)
    raw = header + body + cone_bytes
    comp = zlib.compressobj(9, zlib.DEFLATED, -15)
    data = comp.compress(raw) + comp.flush()
    with open(out, "wb") as f:
        f.write(data)
    print(f"wrote {out}: {len(raw)} bytes raw, {len(data)} deflated")


if __name__ == "__main__":
    if len(sys.argv) != 4 or sys.argv[2] not in MODELS:
        sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2], sys.argv[3])
