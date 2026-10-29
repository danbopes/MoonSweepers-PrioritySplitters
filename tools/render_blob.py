"""Software-render the cone and the body cut-out stored in a src/Resources/*-cone.bin blob, to judge an
extraction without the engine: python -I tools/render_blob.py src/Resources/merger-cone.bin tools/.tmp/merger
writes tools/.tmp/merger_sheet.png (four views of the cone and the body around the cut)."""
import sys, math, struct, zlib
import numpy as np
from PIL import Image

def read_mesh(buf, pos, tangents):
    n, subs, nidx = struct.unpack_from("<III", buf, pos); pos += 12
    table = []
    for _ in range(subs):
        table.append(struct.unpack_from("<III", buf, pos)); pos += 12
    V = np.frombuffer(buf, dtype="<f4", count=n * 3, offset=pos).reshape(n, 3).astype(np.float64); pos += n * 12
    N = np.frombuffer(buf, dtype="<i1", count=n * 3, offset=pos).reshape(n, 3).astype(np.float64) / 127.0; pos += n * 3
    pos += n * 8  # uv
    if tangents:
        pos += n * 16
    I = np.frombuffer(buf, dtype="<u4", count=nidx, offset=pos).astype(np.int64); pos += nidx * 4
    tris = [(mat, I[start:start + count].reshape(-1, 3)) for mat, start, count in table]
    return V, N, tris, pos

raw = zlib.decompress(open(sys.argv[1], "rb").read(), -15)
assert raw[:4] == b"PSC1"
n_src, flags = struct.unpack_from("<II", raw, 4)
pivot = np.array(struct.unpack_from("<fff", raw, 12)); radius = struct.unpack_from("<f", raw, 24)[0]
pos = 28
BV, BN, btris, pos = read_mesh(raw, pos, flags & 1)
CV, CN, ctris, pos = read_mesh(raw, pos, flags & 1)
print(f"{sys.argv[1]}: source verts {n_src}, pivot {pivot}, radius {radius:.4f}; body {len(BV)} verts, cone {len(CV)} verts, cone submeshes {[(m, len(t)) for m, t in ctris]}; trailing bytes {len(raw) - pos}")

colors = {0: (235, 237, 238), 1: (255, 150, 40), 2: (52, 63, 72), 3: (40, 50, 80), 4: (170, 240, 255), 5: (180, 185, 190)}

def render(V, N, tris, centre, size, view_dir, up, out, res=700):
    f = np.array(view_dir, dtype=np.float64); f /= np.linalg.norm(f)
    r = np.cross(f, np.array(up, dtype=np.float64)); r /= np.linalg.norm(r)
    u = np.cross(r, f)
    light = np.array([0.4, 1.0, -0.6]); light /= np.linalg.norm(light)
    zbuf = np.full((res, res), np.inf)
    img = np.zeros((res, res, 3), dtype=np.float64); img[:] = (90, 110, 150)
    for sub, t in tris:
        if len(t) == 0: continue
        P = V[t]; rel = P - centre
        x = (rel @ r) / size * res + res / 2
        y = res / 2 - (rel @ u) / size * res
        z = rel @ f
        keep = (x.max(1) >= 0) & (x.min(1) < res) & (y.max(1) >= 0) & (y.min(1) < res)
        x, y, z, tt = x[keep], y[keep], z[keep], t[keep]
        shade = N[tt] @ light
        base = np.array(colors[sub], dtype=np.float64)
        for k in range(len(tt)):
            xs, ys, zs = x[k], y[k], z[k]
            area = (xs[1] - xs[0]) * (ys[2] - ys[0]) - (xs[2] - xs[0]) * (ys[1] - ys[0])
            if area <= 0: continue
            x0, x1 = int(max(0, math.floor(xs.min()))), int(min(res - 1, math.ceil(xs.max())))
            y0, y1 = int(max(0, math.floor(ys.min()))), int(min(res - 1, math.ceil(ys.max())))
            if x1 < x0 or y1 < y0: continue
            gx, gy = np.meshgrid(np.arange(x0, x1 + 1), np.arange(y0, y1 + 1))
            w0 = ((xs[1] - gx) * (ys[2] - gy) - (xs[2] - gx) * (ys[1] - gy)) / area
            w1 = ((xs[2] - gx) * (ys[0] - gy) - (xs[0] - gx) * (ys[2] - gy)) / area
            w2 = 1 - w0 - w1
            inside = (w0 >= 0) & (w1 >= 0) & (w2 >= 0)
            if not inside.any(): continue
            depth = w0 * zs[0] + w1 * zs[1] + w2 * zs[2]
            s = w0 * shade[k][0] + w1 * shade[k][1] + w2 * shade[k][2]
            region = zbuf[y0:y1 + 1, x0:x1 + 1]
            write = inside & (depth < region)
            region[write] = depth[write]
            lit = (0.35 + 0.65 * np.clip(s, 0, 1))[..., None] * base
            img[y0:y1 + 1, x0:x1 + 1][write] = lit[write]
    Image.fromarray(img.clip(0, 255).astype(np.uint8)).save(out)

out = sys.argv[2]
cc = np.array([0.0, 0.11, 0.0])  # cone is stored relative to its pivot
views = {"px_pz": (0.6, -0.35, 0.7), "nx_pz": (-0.6, -0.35, 0.7), "px_nz": (0.6, -0.35, -0.7), "nx_nz": (-0.6, -0.35, -0.7)}
tiles = []
for name, vd in views.items():
    p = f"{out}_cone_{name}.png"; render(CV, CN, ctris, cc, 0.30, vd, (0, 1, 0), p); tiles.append(p)
# body around the cut, seen from above and slightly in front, to show the hole is clean
near = []
for mat, t in btris:
    d = np.linalg.norm(BV[t].mean(1) - pivot, axis=1)
    near.append((mat, t[d < 0.45]))
p = f"{out}_body_cut.png"; render(BV, BN, near, pivot + np.array([0, 0.03, 0]), 0.6, (0.3, -1.0, 0.3), (0, 0, 1), p); tiles.append(p)
imgs = [Image.open(t) for t in tiles]
sheet = Image.new("RGB", (700 * len(imgs), 700))
for i, im in enumerate(imgs): sheet.paste(im, (700 * i, 0))
sheet = sheet.resize((350 * len(imgs), 350)); sheet.save(f"{out}_sheet.png"); print("wrote", f"{out}_sheet.png")
