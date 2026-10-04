# Voxel Village (Hollowbrook) - self-contained Blender generator
# Open this file in Blender's Scripting workspace and choose Run Script.

# ================== HOLLOWBROOK CORE v2 (token-lean) ========================
# MCP safe mode rejects: class defs, numpy, sys/os/open, exec/eval, text
# datablocks, external module loading. Only bpy, bmesh, mathutils and pure
# Python stdlib are available. Everything below is plain dicts and functions.

import bpy
import bmesh
import mathutils
import math
import json
import random

# Compact palette wire format: id|name|r,g,b|rough|metal|emit  separated by ';'
PAL = (
    "1|soil_dark|.24,.17,.11|.95|0|0;2|soil|.38,.27,.16|.95|0|0;"
    "3|grass|.28,.46,.17|.90|0|0;4|grass_dry|.52,.52,.24|.90|0|0;"
    "5|rock|.46,.46,.47|.85|0|0;6|rock_dk|.30,.31,.33|.85|0|0;"
    "7|sand|.68,.60,.42|.90|0|0;8|gravel|.52,.47,.40|.95|0|0;"
    "9|cobble|.42,.42,.44|.80|0|0;10|cobble_dk|.31,.31,.33|.80|0|0;"
    "11|flagstone|.55,.54,.52|.75|0|0;12|dirt|.47,.38,.27|.95|0|0;"
    "20|water|.13,.34,.48|.08|0|0;21|water_dp|.07,.21,.34|.08|0|0;"
    "22|water_sh|.24,.48,.55|.08|0|0;"
    "30|beam|.29,.19,.11|.90|0|0;31|plank|.45,.30,.17|.85|0|0;"
    "32|wood_lt|.60,.44,.26|.85|0|0;33|wood_rd|.46,.19,.14|.80|0|0;"
    "34|wood_bl|.25,.32,.40|.80|0|0;35|wood_gn|.24,.36,.26|.80|0|0;"
    "40|plaster|.74,.70,.60|.90|0|0;41|plaster_wm|.70,.58,.42|.90|0|0;"
    "42|stone|.50,.49,.45|.85|0|0;43|brick|.50,.28,.21|.85|0|0;"
    "44|timber|.26,.16,.09|.90|0|0;"
    "50|roof_red|.44,.15,.11|.75|0|0;51|roof_gry|.25,.26,.28|.75|0|0;"
    "52|roof_slt|.21,.27,.35|.70|0|0;53|thatch|.54,.41,.19|.95|0|0;"
    "54|roof_cpr|.22,.40,.35|.60|0|0;"
    "60|leaf_dk|.14,.31,.13|.95|0|0;61|leaf_md|.22,.44,.18|.95|0|0;"
    "62|leaf_lt|.36,.56,.22|.95|0|0;63|leaf_au|.62,.44,.14|.95|0|0;"
    "64|trunk|.32,.22,.13|.95|0|0;65|bush|.20,.38,.16|.95|0|0;"
    "70|cloth_rd|.62,.20,.18|.90|0|0;71|cloth_bl|.22,.34,.58|.90|0|0;"
    "72|cloth_yl|.80,.66,.22|.90|0|0;73|cloth_wh|.88,.87,.83|.90|0|0;"
    "74|iron|.34,.35,.37|.45|.85|0;75|copper|.62,.40,.20|.35|.90|0;"
    "76|brass|.72,.58,.24|.30|.90|0;77|glass|.30,.40,.46|.10|0|0;"
    "78|win_lit|1.0,.74,.38|.20|0|2.2;79|lantern|1.0,.70,.32|.20|0|5.0;"
    "80|fire|1.0,.50,.15|.30|0|7.0;"
    "81|hay|.72,.60,.26|.95|0|0;82|sack|.66,.58,.42|.95|0|0;"
    "83|rope|.52,.44,.28|.95|0|0;84|skin|.76,.58,.44|.85|0|0;"
    "85|hair_dk|.16,.12,.09|.90|0|0;86|hair_lt|.68,.50,.26|.90|0|0;"
    "87|leather|.36,.25,.15|.90|0|0;88|flw_red|.78,.25,.24|.90|0|0;"
    "89|flw_yel|.85,.75,.26|.90|0|0;90|flw_wht|.90,.89,.86|.90|0|0;"
    "91|smoke|.62,.62,.64|.95|0|0;92|cloth_gn|.26,.46,.30|.90|0|0;"
    "93|cloth_pu|.42,.24,.48|.90|0|0;94|fur_br|.44,.30,.18|.95|0|0;"
    "95|fur_wh|.86,.84,.78|.95|0|0;96|fur_bk|.14,.13,.13|.95|0|0;"
    "97|feather|.80,.78,.72|.90|0|0;98|flw_pk|.82,.48,.58|.90|0|0;"
    "99|moss|.30,.42,.20|.95|0|0"
)


def parse_pal(src):
    out = {}
    for chunk in src.split(";"):
        if not chunk:
            continue
        i, nm, rgb, ro, me, em = chunk.split("|")
        c = [float(v) for v in rgb.split(",")]
        out[int(i)] = (nm, (c[0], c[1], c[2]), float(ro), float(me), float(em))
    return out


PALETTE = parse_pal(PAL)


def build_palette():
    out = {}
    for mid, (name, rgb, rough, metal, emit) in PALETTE.items():
        key = "HB_" + name
        mat = bpy.data.materials.get(key)
        if mat is None:
            mat = bpy.data.materials.new(key)
        mat.use_nodes = True
        bsdf = next(n for n in mat.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
        ins = bsdf.inputs
        ins["Base Color"].default_value = (rgb[0], rgb[1], rgb[2], 1.0)
        ins["Roughness"].default_value = rough
        ins["Metallic"].default_value = metal
        if "Emission Color" in [i.name for i in ins]:
            ins["Emission Color"].default_value = (rgb[0], rgb[1], rgb[2], 1.0)
            ins["Emission Strength"].default_value = emit
        mat.diffuse_color = (rgb[0], rgb[1], rgb[2], 1.0)
        mat.roughness = rough
        out[mid] = mat
    return out


COLLECTION_NAMES = [
    "Terrain", "Water", "Buildings", "Interiors", "Vegetation",
    "Props", "Residents", "Animals", "Animation", "Lighting", "Cameras",
]


def setup_collections():
    scene = bpy.context.scene
    root = bpy.data.collections.get("Hollowbrook")
    if root is None:
        root = bpy.data.collections.new("Hollowbrook")
    if root.name not in [c.name for c in scene.collection.children]:
        scene.collection.children.link(root)
    made = {}
    for cname in COLLECTION_NAMES:
        c = bpy.data.collections.get(cname)
        if c is None:
            c = bpy.data.collections.new(cname)
        if c.name not in [ch.name for ch in root.children]:
            root.children.link(c)
        made[cname] = c
    studio = bpy.data.collections.get("_Studio")
    if studio is None:
        studio = bpy.data.collections.new("_Studio")
        scene.collection.children.link(studio)
    for oname in ("Cube", "Light", "Camera"):
        ob = bpy.data.objects.get(oname)
        if ob is not None and ob.name not in [o.name for o in studio.objects]:
            for c in list(ob.users_collection):
                c.objects.unlink(ob)
            studio.objects.link(ob)
    studio.hide_viewport = True
    studio.hide_render = True
    return root, made


def gnew(cell=0.5, origin=(0.0, 0.0, 0.0)):
    return {"v": {}, "cell": float(cell), "ox": float(origin[0]),
            "oy": float(origin[1]), "oz": float(origin[2])}


def gset(g, x, y, z, m):
    g["v"][(int(x), int(y), int(z))] = int(m)


def gget(g, x, y, z):
    return g["v"].get((int(x), int(y), int(z)), 0)


def gbox(g, a, b, m):
    x0, y0, z0 = (int(round(t)) for t in a)
    x1, y1, z1 = (int(round(t)) for t in b)
    if x0 > x1:
        x0, x1 = x1, x0
    if y0 > y1:
        y0, y1 = y1, y0
    if z0 > z1:
        z0, z1 = z1, z0
    m = int(m)
    v = g["v"]
    for x in range(x0, x1 + 1):
        for y in range(y0, y1 + 1):
            for z in range(z0, z1 + 1):
                v[(x, y, z)] = m


def gshell(g, a, b, m):
    x0, y0, z0 = (int(round(t)) for t in a)
    x1, y1, z1 = (int(round(t)) for t in b)
    if x0 > x1:
        x0, x1 = x1, x0
    if y0 > y1:
        y0, y1 = y1, y0
    if z0 > z1:
        z0, z1 = z1, z0
    m = int(m)
    v = g["v"]
    for x in range(x0, x1 + 1):
        for y in range(y0, y1 + 1):
            for z in range(z0, z1 + 1):
                if x0 < x < x1 and y0 < y < y1 and z0 < z < z1:
                    continue
                v[(x, y, z)] = m


def gwalls(g, a, b, z0, ztop, m, t=1):
    x0, y0, _ = (int(round(q)) for q in a)
    x1, y1, _ = (int(round(q)) for q in b)
    if x0 > x1:
        x0, x1 = x1, x0
    if y0 > y1:
        y0, y1 = y1, y0
    z0 = int(round(z0))
    ztop = int(round(ztop))
    m = int(m)
    t = int(t)
    v = g["v"]
    for x in range(x0, x1 + 1):
        for y in range(y0, y1 + 1):
            if not (x < x0 + t or x > x1 - t or y < y0 + t or y > y1 - t):
                continue
            for z in range(z0, ztop + 1):
                v[(x, y, z)] = m


def gclear(g, a, b):
    x0, y0, z0 = (int(round(t)) for t in a)
    x1, y1, z1 = (int(round(t)) for t in b)
    v = g["v"]
    for x in range(min(x0, x1), max(x0, x1) + 1):
        for y in range(min(y0, y1), max(y0, y1) + 1):
            for z in range(min(z0, z1), max(z0, z1) + 1):
                v.pop((x, y, z), None)


def gcol(g, x, y, z0, z1, m):
    gbox(g, (x, y, z0), (x, y, z1), m)


def gcyl(g, cx, cy, z0, z1, r, m, filled=True, yscale=1.0):
    m = int(m)
    ir = int(math.ceil(r)) + 1
    r2 = float(r) * float(r)
    inner = (float(r) - 1.0) ** 2
    v = g["v"]
    for x in range(int(cx) - ir, int(cx) + ir + 1):
        for y in range(int(cy) - ir, int(cy) + ir + 1):
            dx = x - cx
            dy = (y - cy) / float(yscale)
            d2 = dx * dx + dy * dy
            if d2 > r2:
                continue
            if not filled and d2 < inner:
                continue
            for z in range(int(z0), int(z1) + 1):
                v[(x, y, z)] = m


def gsph(g, cx, cy, cz, r, m, squash=1.0, cut_below=None):
    m = int(m)
    ir = int(math.ceil(r)) + 1
    izr = int(math.ceil(r / max(squash, 1e-6))) + 1
    v = g["v"]
    for x in range(int(cx) - ir, int(cx) + ir + 1):
        for y in range(int(cy) - ir, int(cy) + ir + 1):
            for z in range(int(cz) - izr, int(cz) + izr + 1):
                if cut_below is not None and z < cut_below:
                    continue
                dx = x - cx
                dy = y - cy
                dz = (z - cz) * squash
                if dx * dx + dy * dy + dz * dz <= r * r:
                    v[(x, y, z)] = m


def gmerge(dst, src, ox=0, oy=0, oz=0):
    dv = dst["v"]
    for (x, y, z), m in src["v"].items():
        dv[(x + ox, y + oy, z + oz)] = m


def gbounds(g):
    v = g["v"]
    if not v:
        return None
    xs = [k[0] for k in v]
    ys = [k[1] for k in v]
    zs = [k[2] for k in v]
    return [min(xs), min(ys), min(zs)], [max(xs), max(ys), max(zs)]


def greedy(rows, w, h):
    out = []
    used = bytearray(w * h)
    for i in range(h):
        base = i * w
        for j in range(w):
            idx = base + j
            m = rows[idx]
            if m == 0 or used[idx]:
                continue
            dw = 1
            while j + dw < w:
                k = base + j + dw
                if rows[k] != m or used[k]:
                    break
                dw += 1
            dh = 1
            while i + dh < h:
                b2 = (i + dh) * w
                ok = True
                for t in range(j, j + dw):
                    k = b2 + t
                    if rows[k] != m or used[k]:
                        ok = False
                        break
                if not ok:
                    break
                dh += 1
            for uu in range(i, i + dh):
                b3 = uu * w
                for vv in range(j, j + dw):
                    used[b3 + vv] = 1
            out.append((i, j, dh, dw, m))
    return out


def emit(g, name, id2mat, coll, merge=True, skip_bottom=True):
    old = bpy.data.objects.get(name)
    if old is not None:
        od = old.data
        bpy.data.objects.remove(old, do_unlink=True)
        if od is not None and od.users == 0:
            bpy.data.meshes.remove(od)
    v = g["v"]
    if not v:
        return None
    cell = g["cell"]
    ox, oy, oz = g["ox"], g["oy"], g["oz"]
    vmap = {}
    verts = []
    faces = []
    fmats = []

    def vid(x, y, z):
        key = (x, y, z)
        got = vmap.get(key)
        if got is None:
            got = len(verts)
            vmap[key] = got
            verts.append((ox + x * cell, oy + y * cell, oz + z * cell))
        return got

    dirs = ((1, 0, 0, 0, True), (-1, 0, 0, 0, False),
            (0, 1, 0, 1, True), (0, -1, 0, 1, False),
            (0, 0, 1, 2, True), (0, 0, -1, 2, False))

    for (nx, ny, nz, naxis, positive) in dirs:
        if skip_bottom and nz < 0:
            continue
        if naxis == 0:
            ua, va = 1, 2
        elif naxis == 1:
            ua, va = 2, 0
        else:
            ua, va = 0, 1
        buckets = {}
        for c, m in v.items():
            if (c[0] + nx, c[1] + ny, c[2] + nz) in v:
                continue
            plane = c[naxis] + (1 if positive else 0)
            L = buckets.get(plane)
            if L is None:
                buckets[plane] = [(c, m)]
            else:
                L.append((c, m))
        for plane, items in buckets.items():
            done = False
            if merge and len(items) > 1:
                us = [it[0][ua] for it in items]
                vs = [it[0][va] for it in items]
                u0, u1 = min(us), max(us)
                v0, v1 = min(vs), max(vs)
                w = v1 - v0 + 1
                h = u1 - u0 + 1
                if w * h <= 3000000:
                    rows = [0] * (w * h)
                    for (c, m) in items:
                        rows[(c[ua] - u0) * w + (c[va] - v0)] = m
                    for (ru, rv, du, dv, mm) in greedy(rows, w, h):
                        cs = []
                        for (uu, vv) in ((0, 0), (0, 1), (1, 1), (1, 0)):
                            cc = [0, 0, 0]
                            cc[naxis] = plane
                            cc[ua] = u0 + ru + uu * du
                            cc[va] = v0 + rv + vv * dv
                            cs.append(vid(cc[0], cc[1], cc[2]))
                        faces.append((cs[0], cs[1], cs[2], cs[3]))
                        fmats.append(mm)
                    done = True
            if done:
                continue
            for (c, m) in items:
                cs = []
                for (uu, vv) in ((0, 0), (0, 1), (1, 1), (1, 0)):
                    cc = [0, 0, 0]
                    cc[naxis] = plane
                    cc[ua] = c[ua] + uu
                    cc[va] = c[va] + vv
                    cs.append(vid(cc[0], cc[1], cc[2]))
                faces.append((cs[0], cs[1], cs[2], cs[3]))
                fmats.append(m)

    me = bpy.data.meshes.new(name)
    me.from_pydata(verts, [], faces)
    me.validate(verbose=False)
    ob = bpy.data.objects.new(name, me)
    coll.objects.link(ob)
    slot = {}
    for mid in sorted(set(fmats)):
        mm = id2mat.get(mid)
        if mm is None:
            continue
        slot[mid] = len(ob.data.materials)
        ob.data.materials.append(mm)
    if slot:
        me.polygons.foreach_set("material_index", [slot.get(m, 0) for m in fmats])
        me.update()
    ob["voxel_cell_m"] = cell
    ob["voxel_cells"] = len(v)
    ob["quad_count"] = len(faces)
    return ob


def clear_collection(coll):
    for ob in list(coll.objects):
        data = ob.data
        bpy.data.objects.remove(ob, do_unlink=True)
        if data is not None and data.users == 0:
            if isinstance(data, bpy.types.Mesh):
                bpy.data.meshes.remove(data)
            elif isinstance(data, bpy.types.Light):
                bpy.data.lights.remove(data)
            elif isinstance(data, bpy.types.Camera):
                bpy.data.cameras.remove(data)


# ============ STAGE 5: FINAL BUILD (fixed placement + cameras + anim) =======
# Fixes found by inspection:
#   * buildings on band 2 ran off the north map edge; sites are now clamped to
#     the map and every pad takes its height from the natural terrain there,
#     so nothing floats and nothing hangs over the boundary;
#   * the Town Hall sat on top of the market square (well and stalls inside its
#     walls); it now stands on the upper terrace above the square;
#   * close-up cameras are aimed at the real built positions, not guesses.

SITE = 96.0
CELL = 0.5
N = 192
Z_RIVERBED = 3
Z_WATER = 5
Z_BANKLOW = 7
Z_TERR1 = 11
Z_TERR2 = 15
Z_TERR3 = 18

root, colls = setup_collections()
MATS = build_palette()


def river_center(x):
    return 96.0 + 13.0 * math.sin(x * 0.026) + 5.0 * math.sin(x * 0.068 + 1.3)


def river_half(x):
    return 7.0 + 5.0 * (0.5 + 0.5 * math.sin(x * 0.021 + 0.7))


def noise(x, y, seed=0.0):
    a = math.sin(x * 0.31 + seed) * math.cos(y * 0.27 - seed * 0.7)
    b = math.sin((x + y) * 0.13 + seed * 2.1)
    c = math.sin(x * 0.089 - y * 0.104 + seed * 3.7)
    return (a + b * 0.6 + c * 0.45) / 1.9


def ny(x):
    return river_center(x) - river_half(x)


def sy(x):
    return river_center(x) + river_half(x)


terrain = gnew(cell=CELL)
ground_top = {}
for x in range(N):
    rc = river_center(x)
    rh = river_half(x)
    for y in range(N):
        d = abs(y - rc)
        if d < rh:
            top = max(0, Z_RIVERBED - int(1 + 2 * (1.0 - d / rh)))
        else:
            gap = d - rh
            if y < rc:
                t = (Z_BANKLOW if gap < 22 else
                     Z_TERR1 if gap < 62 else
                     Z_TERR2 if gap < 96 else Z_TERR3)
            else:
                t = (Z_BANKLOW if gap < 62 else
                     Z_TERR1 if gap < 96 else Z_TERR2)
            soften = int(round(min(3.0, gap * 0.32)))
            far = min(1.0, gap / 40.0)
            bump = int(round(noise(x, y, 1.7) * 1.8 * far))
            top = max(Z_BANKLOW - 3, t - soften + bump)
            top = min(top, Z_TERR3 + 1)
        ground_top[(x, y)] = top
        gbox(terrain, (x, y, 0), (x, y, top), 2)
        gset(terrain, x, y, top, 3)

for x in range(N):
    rc = river_center(x)
    rh = river_half(x)
    for y in range(N):
        top = ground_top[(x, y)]
        d = abs(y - rc)
        if d < rh:
            m = 8
        elif d < rh + 2:
            m = 7
        elif d > 84:
            m = 5 if noise(x, y, 2.0) > -0.15 else 6
        elif d < 24 and noise(x, y, 0.4) > 0.24:
            m = 8
        elif noise(x, y, 5.5) > 0.66:
            m = 4
        else:
            m = 3
        gset(terrain, x, y, top, m)

for x in range(N - 1):
    for y in range(N - 1):
        t = ground_top[(x, y)]
        if (abs(t - ground_top[(x + 1, y)]) >= 4
                or abs(t - ground_top[(x, y + 1)]) >= 4):
            gset(terrain, x, y, t, 5)

for x in range(126, 175):
    for y in range(128, 177):
        if not (0 <= x < N and 0 <= y < N):
            continue
        dx = (x - 150) / 21.0
        dy = (y - 152) / 21.0
        r = math.sqrt(dx * dx + dy * dy)
        if r > 1.0:
            continue
        h = int((1.0 - r) * 10.0)
        if h <= 0:
            continue
        base = ground_top[(x, y)]
        for z in range(base + 1, base + h + 1):
            gset(terrain, x, y, z, 6 if (z + x + y) % 4 else 5)


def flatten(x0, y0, x1, y1, z, mat):
    for x in range(int(x0), int(x1) + 1):
        for y in range(int(y0), int(y1) + 1):
            if not (0 <= x < N and 0 <= y < N):
                continue
            for zz in range(0, z + 1):
                terrain["v"][(x, y, zz)] = 2
            terrain["v"][(x, y, z)] = mat
            for zz in range(z + 1, 40):
                terrain["v"].pop((x, y, zz), None)
            ground_top[(x, y)] = z


painted = set()
LANES = [
    (10, 182, "N", 12, 4, 9),
    (10, 182, "N", 40, 5, 9),
    (14, 178, "N", 78, 4, 12),
    (10, 182, "S", 22, 4, 12),
    (16, 176, "S", 78, 4, 12),
]
for (x0, x1, side, off, w, mat) in LANES:
    for x in range(x0, x1 + 1):
        base = ny(x) - off if side == "N" else sy(x) + off
        yc = max(0, min(N - 1, int(round(base))))
        z = ground_top[(x, yc)]
        if z < Z_WATER:
            continue
        for y in range(yc - w // 2, yc + w // 2 + 1):
            if not (0 <= y < N):
                continue
            if ground_top[(x, y)] < Z_WATER:
                continue
            flatten(x, y, x, y, z, mat)
            painted.add((x, y))

for y in range(10, 186):
    if ground_top[(100, y)] < Z_WATER:
        continue
    z = ground_top[(100, y)]
    for x in range(97, 104):
        flatten(x, y, x, y, z, 9)
        painted.add((x, y))

# market square on terrace 1, clear of the town hall above it
SQ_CX = 92
sq_yc = int(round(ny(SQ_CX) - 40))
SQ_Z = ground_top[(SQ_CX, sq_yc)]
SQ = (SQ_CX - 15, sq_yc - 11, SQ_CX + 15, sq_yc + 11)
flatten(SQ[0], SQ[1], SQ[2], SQ[3], SQ_Z, 11)
for x in range(SQ[0], SQ[2] + 1):
    for y in range(SQ[1], SQ[3] + 1):
        painted.add((x, y))
        if (x - SQ[0] < 2 or SQ[2] - x < 2 or y - SQ[1] < 2 or SQ[3] - y < 2):
            gset(terrain, x, y, SQ_Z, 11)
        else:
            gset(terrain, x, y, SQ_Z, 11 if (x + y) % 7 else 9)

# name, x, side, band, half_w, half_d, floors, wall, roof, kind, opts
BUILDINGS = [
    ("Town Hall",   92, "N", 2, 11,  7, 2, 42, 54, "hip",
     {"tower": True, "lit": 4, "chimney": 1, "interior": "hall"}),
    ("Chapel",     126, "N", 2,  8,  8, 2, 40, 52, "gable",
     {"spire": True, "lit": 5, "chimney": 0, "interior": "chapel"}),
    ("The Bridge Inn", 116, "N", 1, 8,  7, 2, 41, 50, "gable",
     {"jetty": 1, "timber": True, "lit": 6, "chimney": 2}),
    ("Bakery",      66, "N", 1,  7,  6, 2, 40, 53, "gable",
     {"jetty": 1, "timber": True, "lit": 3, "chimney": 2, "oven": True,
      "interior": "bakery"}),
    ("Smithy",      52, "N", 0,  8,  6, 1, 42, 51, "lean",
     {"lit": 2, "chimney": 2, "forge": True}),
    ("Warehouse",  140, "N", 0,  9,  7, 2, 44, 51, "gable",
     {"lit": 2, "crates": True}),
    ("River Watch", 40, "N", 1,  6,  6, 1, 42, 52, "hip",
     {"lit": 2, "chimney": 1}),
    ("Weaver's Loft", 32, "N", 1, 6, 6, 2, 41, 53, "gable",
     {"timber": True, "lit": 3, "chimney": 1}),
    ("Fletcher's House", 158, "N", 1, 7, 6, 2, 40, 50, "gable",
     {"jetty": 1, "timber": True, "lit": 3, "chimney": 1}),
    ("Hilltop Cottage", 170, "N", 1, 7, 7, 1, 42, 53, "hip",
     {"lit": 2, "chimney": 1}),
    ("Watermill",   66, "S", 0,  9,  8, 2, 42, 53, "gable",
     {"lit": 3, "chimney": 1, "wheel": True}),
    ("Great Barn", 140, "S", 1, 12,  7, 1, 44, 53, "gable",
     {"lit": 2, "hay": True}),
    ("Miller's Cottage", 36, "S", 0, 7, 7, 1, 40, 50, "gable",
     {"lit": 2, "chimney": 1}),
    ("Stable",     166, "S", 0,  8,  6, 1, 31, 53, "lean",
     {"lit": 1, "horses": True}),
]

# band offsets, clamped so no site leaves the map
BAND_OFF = {"N": {0: 11, 1: 40, 2: 70}, "S": {0: 20, 1: 78}}

PAD_INFO = {}
SITES = []
OCCUPIED = []


def block(rect, pad=1):
    OCCUPIED.append((rect[0] - pad, rect[1] - pad, rect[2] + pad, rect[3] + pad))


CLAMP_LO, CLAMP_HI = 12, N - 14
for (name, cx, side, band, hw, hd, fl, wall, roof, kind, opts) in BUILDINGS:
    off = BAND_OFF[side][band]
    base = ny(cx) - off if side == "N" else sy(cx) + off
    yc = int(round(base))
    yc = max(CLAMP_LO + hd, min(CLAMP_HI - hd, yc))
    # take the pad height from the natural terrain, so it never floats
    z = ground_top[(cx, yc)]
    SITES.append((name, cx, yc, z, hw, hd, fl, wall, roof, kind, opts))
    for x in range(cx - hw - 3, cx + hw + 4):
        for y in range(yc - hd - 3, yc + hd + 4):
            if not (0 <= x < N and 0 <= y < N):
                continue
            if ground_top[(x, y)] < Z_WATER:
                continue
            mat = 9 if (x < cx - hw or x > cx + hw or y < yc - hd or y > yc + hd) else 11
            flatten(x, y, x, y, z, mat)
            painted.add((x, y))
    PAD_INFO[name] = {"x": cx, "y": yc, "z": z, "hw": hw, "hd": hd}
    block((cx - hw - 1, yc - hd - 1, cx + hw + 1, yc + hd + 1), pad=1)

FIELDS = []
for (fx, fy, fw, fh) in [(104, 150, 30, 22), (150, 148, 26, 20), (112, 178, 34, 10)]:
    for x in range(fx, fx + fw):
        for y in range(fy, fy + fh):
            if not (0 <= x < N and 0 <= y < N):
                continue
            if ground_top[(x, y)] < Z_WATER:
                continue
            z = ground_top[(x, y)]
            gset(terrain, x, y, z, 2 if (y % 3) else 12)
    FIELDS.append((fx, fy, fw, fh))
    block((fx - 1, fy - 1, fx + fw, fy + fh), pad=0)

MILL_X = 66
MILL_Y0 = int(round(sy(MILL_X)))
for x in range(MILL_X - 5, MILL_X + 6):
    for y in range(MILL_Y0, MILL_Y0 + 23):
        if not (0 <= y < N):
            continue
        for z in range(0, Z_BANKLOW + 2):
            terrain["v"].pop((x, y, z), None)
        ground_top[(x, y)] = Z_WATER

obj_terrain = emit(terrain, "HB_Terrain", MATS, colls["Terrain"],
                   merge=True, skip_bottom=True)

water = gnew(cell=CELL)
for x in range(N):
    rc = river_center(x)
    rh = river_half(x)
    for y in range(int(rc - rh), int(rc + rh) + 1):
        if 0 <= y < N:
            gset(water, x, y, Z_WATER, 21 if abs(y - rc) < rh * 0.35 else 20)
for x in range(MILL_X - 4, MILL_X + 5):
    for y in range(MILL_Y0, MILL_Y0 + 23):
        if 0 <= y < N:
            gset(water, x, y, Z_WATER, 22)
obj_water = emit(water, "HB_Water", MATS, colls["Water"], merge=True,
                 skip_bottom=True)

BX = 100
BW = 7
DECK_Z = 10
NYB = int(round(ny(BX)))
SYB = int(round(sy(BX)))
span = float(SYB - NYB)


def hump(y):
    t = (y - NYB) / span if span else 0.0
    t = 0.0 if t < 0.0 else (1.0 if t > 1.0 else t)
    return int(round(2.0 * math.sin(math.pi * t)))


bridge = gnew(cell=CELL)
for y in range(NYB - 5, SYB + 6):
    z = DECK_Z + hump(y)
    for x in range(BX - BW, BX + BW + 1):
        gset(bridge, x, y, z, 42)
        gset(bridge, x, y, z - 1, 42)
        gset(bridge, x, y, z - 2, 42)
        if x <= BX - BW + 1 or x >= BX + BW - 1:
            gset(bridge, x, y, z + 1, 11)
arch_span = 12
for y0 in range(NYB, SYB, arch_span):
    for y in range(y0, min(y0 + arch_span, SYB)):
        for x in range(BX - BW, BX + BW + 1):
            for z in range(0, DECK_Z - 2):
                gset(bridge, x, y, z, 10)
    for y in range(y0 + arch_span - 2, y0 + arch_span):
        for x in range(BX - BW, BX + BW + 1):
            for z in range(0, DECK_Z - 2):
                gset(bridge, x, y, z, 42)
for y0 in range(NYB, SYB, arch_span):
    for y in range(y0, min(y0 + arch_span, SYB)):
        t = (y - y0) / float(arch_span - 1)
        rise = int(round(4.0 * math.sin(math.pi * t)))
        for x in range(BX - BW + 2, BX + BW - 1):
            for z in range(0, Z_WATER + rise + 1):
                bridge["v"].pop((x, y, z), None)
obj_bridge = emit(bridge, "HB_Bridge", MATS, colls["Buildings"], merge=True)


def add_window(g, x, y, z, facing, lit):
    if facing in ("+x", "-x"):
        dx = 1 if facing == "+x" else -1
        for zz in range(z, z + 2):
            for dy in (-1, 0):
                g["v"].pop((x, y + dy, zz), None)
                g["v"].pop((x + dx, y + dy, zz), None)
            gset(g, x + dx, y - 1, zz, 78 if lit else 77)
            gset(g, x + dx, y, zz, 78 if lit else 77)
    else:
        dy = 1 if facing == "+y" else -1
        for zz in range(z, z + 2):
            for dx in (-1, 0):
                g["v"].pop((x + dx, y, zz), None)
                g["v"].pop((x + dx, y + dy, zz), None)
            gset(g, x - 1, y + dy, zz, 78 if lit else 77)
            gset(g, x, y + dy, zz, 78 if lit else 77)


def add_door(g, x, y, z, facing, h=4, w=2, m=30):
    if facing in ("+x", "-x"):
        dx = 1 if facing == "+x" else -1
        for zz in range(z, z + h):
            for dy in range(-w // 2, w // 2):
                g["v"].pop((x, y + dy, zz), None)
                g["v"].pop((x + dx, y + dy, zz), None)
        for dy in range(-w // 2, w // 2):
            gset(g, x, y + dy, z + h, m)
    else:
        dy = 1 if facing == "+y" else -1
        for zz in range(z, z + h):
            for dx in range(-w // 2, w // 2):
                g["v"].pop((x + dx, y, zz), None)
                g["v"].pop((x + dx, y + dy, zz), None)
        for dx in range(-w // 2, w // 2):
            gset(g, x + dx, y, z + h, m)


def timber_frame(g, x0, y0, z0, x1, y1, ztop, m=44):
    for z in range(z0, ztop + 1):
        vert = (z - z0) % 5 == 0
        horz = (z - z0) % 5 == 3
        for x in range(x0, x1 + 1):
            for y in range(y0, y1 + 1):
                if not ((x in (x0, x1)) or (y in (y0, y1))):
                    continue
                on_corner = (x in (x0, x1)) and (y in (y0, y1))
                post = vert and ((x - x0) % 6 == 0 or (y - y0) % 6 == 0)
                if on_corner or horz or (post and not horz):
                    g[(x, y, z)] = m


def build_gable_roof(g, x0, y0, z0, x1, y1, mat, rise_cells, thick=2, overhang=1):
    for k in range(rise_cells + 1):
        t = k / float(max(1, rise_cells))
        half = ((y1 - y0) / 2.0 + overhang) * (1.0 - t)
        ymid = (y0 + y1) / 2.0
        ylo = int(math.floor(ymid - half))
        yhi = int(math.ceil(ymid + half))
        z = z0 + k
        for x in range(x0 - overhang, x1 + overhang + 1):
            for y in range(ylo, yhi + 1):
                for dz in range(thick):
                    g[(x, y, z + dz)] = mat


def build_hip_roof(g, x0, y0, z0, x1, y1, mat, rise_cells, thick=2, overhang=1):
    for k in range(rise_cells + 1):
        t = k / float(max(1, rise_cells))
        z = z0 + k
        w = (x1 - x0 + 2 * overhang)
        d = (y1 - y0 + 2 * overhang)
        xlo = int(math.floor(x0 - overhang + w * t * 0.5))
        xhi = int(math.ceil(x1 + overhang - w * t * 0.5))
        ylo = int(math.floor(y0 - overhang + d * t * 0.5))
        yhi = int(math.ceil(y1 + overhang - d * t * 0.5))
        if xhi < xlo:
            xlo = xhi = int((x0 + x1) / 2)
        if yhi < ylo:
            ylo = yhi = int((y0 + y1) / 2)
        for x in range(xlo, xhi + 1):
            for y in range(ylo, yhi + 1):
                for dz in range(thick):
                    g[(x, y, z + dz)] = mat


def build_lean_roof(g, x0, y0, z0, x1, y1, mat, rise_cells, thick=2, overhang=1):
    depth = y1 - y0 + 2 * overhang
    for k in range(rise_cells + 1):
        z = z0 + k
        ylo = y0 - overhang
        yhi = int(math.ceil(y0 - overhang + depth * (k + 1) / float(rise_cells + 1)))
        for x in range(x0 - overhang, x1 + overhang + 1):
            for y in range(ylo, yhi + 1):
                for dz in range(thick):
                    g[(x, y, z + dz)] = mat


WHEEL_INFO = {}
CHIMNEYS = []


def build_house(g, name, cx, cy, z0, hw, hd, floors, wall_m, roof_m, kind, opts):
    FH = 9
    x0, x1 = cx - hw, cx + hw
    y0, y1 = cy - hd, cy + hd
    gbox(g, (x0 - 1, y0 - 1, z0 - 3), (x1 + 1, y1 + 1, z0), 42)
    gbox(g, (x0, y0, z0), (x1, y1, z0), 31)
    z = z0 + 1
    for fl in range(floors):
        top = z + FH - 1
        gwalls(g, (x0, y0, z), (x1, y1, 0), z, top, wall_m, t=2)
        if fl > 0:
            gbox(g, (x0, y0, z - 1), (x1, y1, z - 1), 31)
        if opts.get("jetty") and fl == 1:
            jx0, jx1 = x0 - 2, x1 + 2
            jy0, jy1 = y0 - 2, y1 + 2
            for x in range(jx0, jx1 + 1):
                for y in range(jy0, jy1 + 1):
                    gset(g, x, y, z - 1, 31)
            gwalls(g, (jx0, jy0, z), (jx1, jy1, 0), z, top, wall_m, t=2)
            x0, x1, y0, y1 = jx0, jx1, jy0, jy1
        if opts.get("timber"):
            timber_frame(g, x0, y0, z, x1, y1, top, 44)
        z = top + 1
        lit_budget = opts.get("lit", 0) if fl == floors - 1 else 0
        if fl == 0:
            add_door(g, cx, y1, z0 + 1, "+y", h=4, w=2)
            add_door(g, cx + hw, cy, z0 + 1, "+x", h=4, w=2)
        win_z = z + 2
        spots = [
            (cx - hw + 2, y1, "+y"), (cx + hw - 2, y1, "+y"),
            (cx - hw + 2, y0, "-y"), (cx + hw - 2, y0, "-y"),
            (x1, cy - hd + 2, "+x"), (x1, cy + hd - 2, "+x"),
            (x0, cy - hd + 2, "-x"), (x0, cy + hd - 2, "-x"),
        ]
        for i, (wx, wy, f) in enumerate(spots):
            lit = (lit_budget > 0) and (i % 2 == 0)
            if lit:
                lit_budget -= 1
            add_window(g, wx, wy, win_z, f, lit)
    wall_top = z - 1
    rise = max(3, int(round(max(hw, hd) * (1.0 if kind == "gable" else 0.85 if kind == "hip" else 0.5))))
    if kind == "gable":
        build_gable_roof(g, x0, y0, wall_top + 1, x1, y1, roof_m, rise, thick=2)
        for k in range(rise + 1):
            half = ((y1 - y0) / 2.0) * (1.0 - k / float(rise))
            ymid = (y0 + y1) / 2.0
            ylo = int(math.floor(ymid - half))
            yhi = int(math.ceil(ymid + half))
            for y in range(ylo, yhi + 1):
                gset(g, x0, y, wall_top + 1 + k, wall_m)
                gset(g, x1, y, wall_top + 1 + k, wall_m)
    elif kind == "hip":
        build_hip_roof(g, x0, y0, wall_top + 1, x1, y1, roof_m, rise, thick=2)
    else:
        build_lean_roof(g, x0, y0, wall_top + 1, x1, y1, roof_m, rise, thick=2)
    roof_top = wall_top + 1 + rise

    if opts.get("chimney"):
        chx = x1 - 2
        chy = y0 + 2
        for zz in range(z0, roof_top + 3):
            for dx in (0, 1):
                for dy in (0, 1):
                    gset(g, chx + dx, chy + dy, zz, 43)
        for dx in range(-1, 3):
            for dy in range(-1, 3):
                gset(g, chx + dx, chy + dy, roof_top + 3, 10)
        CHIMNEYS.append((chx + 0.5, chy + 0.5, roof_top + 4, name))

    if opts.get("tower"):
        tx, ty = x0 + 4, y1 - 4
        th = wall_top + 20
        gshell(g, (tx - 3, ty - 3, z0), (tx + 3, ty + 3, th), 42)
        for zz in range(z0 + 6, th, 6):
            for dx in (-3, 3):
                for dy in (-3, 3):
                    gset(g, tx + dx, ty + dy, zz, 11)
            gset(g, tx, ty - 3, zz, 77)
            gset(g, tx, ty + 3, zz, 77)
        build_hip_roof(g, tx - 4, ty - 4, th + 1, tx + 4, ty + 4, 54, 7, thick=2)
        gcol(g, tx, ty, th + 8, th + 13, 76)

    if opts.get("spire"):
        tx, ty = cx, cy + hd - 3
        th = wall_top + 14
        gshell(g, (tx - 3, ty - 3, z0), (tx + 3, ty + 3, th), 42)
        for zz in range(z0 + 8, th, 7):
            gset(g, tx - 3, ty, zz, 77)
            gset(g, tx + 3, ty, zz, 77)
        for k in range(16):
            w = max(1, 4 - k // 4)
            for dx in range(-w, w + 1):
                for dy in range(-w, w + 1):
                    gset(g, tx + dx, ty + dy, th + 1 + k, 52)
        gcol(g, tx, ty, th + 17, th + 20, 76)

    if opts.get("wheel"):
        WHEEL_INFO["name"] = name

    if opts.get("oven"):
        gbox(g, (x0 + 1, y0 + 1, z0 + 1), (x0 + 3, y0 + 3, z0 + 5), 43)
        gset(g, x0 + 2, y0 + 2, z0 + 6, 80)
    if opts.get("forge"):
        gbox(g, (x0 + 1, y0 + 1, z0 + 1), (x0 + 4, y0 + 3, z0 + 3), 74)
        gset(g, x0 + 2, y0 + 2, z0 + 4, 80)
    if opts.get("crates"):
        gbox(g, (x1 - 3, y0 + 1, z0 + 1), (x1 - 1, y0 + 3, z0 + 3), 31)
    if opts.get("hay"):
        gbox(g, (x0 + 2, y0 + 2, z0 + 1), (x0 + 6, y0 + 6, z0 + 5), 81)
    if opts.get("horses"):
        for i in range(2):
            hxx = x0 + 3 + i * 4
            gbox(g, (hxx, y0 + 3, z0 + 1), (hxx + 2, y0 + 4, z0 + 3), 94)

    return {"wall_top": wall_top, "roof_top": roof_top,
            "rect": [x0, y0, x1, y1], "z0": z0,
            "interior": opts.get("interior")}


buildings = gnew(cell=CELL)
BUILT = {}
for (name, cx, yc, z, hw, hd, fl, wall, roof, kind, opts) in SITES:
    BUILT[name] = build_house(buildings, name, cx, yc, z, hw, hd, fl,
                              wall, roof, kind, opts)
obj_buildings = emit(buildings, "HB_Buildings", MATS, colls["Buildings"],
                     merge=True, skip_bottom=True)

# mill wheel in the race, a true vertical disc about the X axis
wheel = gnew(cell=CELL)
WHEEL_PIVOT = None
if WHEEL_INFO:
    wcx = MILL_X
    wcy = MILL_Y0 + 11
    wcz = Z_BANKLOW + 3
    WHEEL_PIVOT = [(wcx + 0.5) * CELL, (wcy + 0.5) * CELL, (wcz + 0.5) * CELL]
    R = 8
    for dx in (-1, 0, 1):
        for b in range(24):
            ang = b * math.pi / 12.0
            gset(wheel, wcx + dx, int(round(wcy + R * math.cos(ang))),
                 int(round(wcz + R * math.sin(ang))), 30)
            rr = R - 2
            gset(wheel, wcx + dx, int(round(wcy + rr * math.cos(ang))),
                 int(round(wcz + rr * math.sin(ang))), 31)
        for s in range(8):
            ang = s * math.pi / 4.0
            for rr in range(1, R - 1):
                gset(wheel, wcx + dx, int(round(wcy + rr * math.cos(ang))),
                     int(round(wcz + rr * math.sin(ang))), 31)
        for dy in (-1, 0, 1):
            for dz in (-1, 0, 1):
                gset(wheel, wcx + dx, wcy + dy, wcz + dz, 74)
    for s in range(10):
        ang = s * math.pi / 5.0
        for rr in range(R, R + 3):
            for dx in (-1, 0, 1):
                gset(wheel, wcx + dx, int(round(wcy + rr * math.cos(ang))),
                     int(round(wcz + rr * math.sin(ang))), 30)
    for dx in range(-7, 8):
        for dy in (-1, 0, 1):
            for dz in (-1, 0, 1):
                gset(wheel, wcx + dx, wcy + dy, wcz + dz, 74)
obj_wheel = emit(wheel, "HB_MillWheel", MATS, colls["Buildings"], merge=True)

smoke = gnew(cell=CELL)
for (cx_c, cy_c, cz_c, bname) in CHIMNEYS:
    for k in range(7):
        r = 1 + k // 3
        gsph(smoke, cx_c, cy_c, cz_c + 2 + k * 4, r, 91, squash=0.85)
obj_smoke = emit(smoke, "HB_Smoke", MATS, colls["Animation"], merge=True)


def prop_sack_i(g, x, y, z):
    gbox(g, (x, y, z), (x + 1, y + 1, z + 1), 82)


inter = gnew(cell=CELL)
INT_INFO = {}
for (name, info) in BUILT.items():
    spec = info.get("interior") if info else None
    if not spec:
        continue
    pad = PAD_INFO.get(name)
    if pad is None:
        continue
    cx, cy, z = pad["x"], pad["y"], pad["z"]
    hw, hd = pad["hw"], pad["hd"]
    fz = z + 1
    if spec == "hall":
        gbox(inter, (cx - 4, cy - 1, fz), (cx + 4, cy + 1, fz + 2), 31)
        for i in range(-4, 5, 2):
            gset(inter, cx + i, cy - 2, fz + 1, 30)
            gset(inter, cx + i, cy + 2, fz + 1, 30)
        gbox(inter, (cx - hw + 3, cy - hd + 2, fz), (cx - hw + 5, cy - hd + 4, fz + 3), 31)
        gbox(inter, (cx + hw - 4, cy - hd + 2, fz), (cx + hw - 2, cy - hd + 4, fz + 4), 44)
        gset(inter, cx + hw - 3, cy - hd + 3, fz + 5, 80)
        gbox(inter, (cx - 2, cy + hd - 3, fz), (cx + 2, cy + hd - 2, fz + 1), 70)
    elif spec == "chapel":
        for i in range(4):
            gbox(inter, (cx - 6 + i * 4, cy - 1, fz), (cx - 4 + i * 4, cy + 1, fz + 1), 31)
        gbox(inter, (cx - 2, cy + hd - 5, fz), (cx + 2, cy + hd - 3, fz + 3), 42)
        gset(inter, cx, cy + hd - 4, fz + 4, 79)
        for i in range(-6, 7, 3):
            gset(inter, cx + i, cy - hd + 3, fz + 6, 78)
    else:
        gbox(inter, (cx - 4, cy - 2, fz), (cx + 2, cy + 1, fz + 2), 31)
        for i in range(4):
            prop_sack_i(inter, cx + 3, cy - hd + 2 + i * 2, fz)
        gbox(inter, (cx - hw + 3, cy + hd - 4, fz), (cx - hw + 6, cy + hd - 1, fz + 1), 43)
        gset(inter, cx - hw + 4, cy + hd - 3, fz + 2, 80)
    INT_INFO[name] = {"spec": spec, "z": fz}
obj_inter = emit(inter, "HB_Interiors", MATS, colls["Interiors"], merge=True)


def free_at(x, y, pad=0):
    for (a, b, c, d) in OCCUPIED:
        if a - pad <= x <= c + pad and b - pad <= y <= d + pad:
            return False
    return True


def ground_z(x, y):
    return ground_top.get((int(x), int(y)), Z_TERR1)


def prop_barrel(g, x, y, z, m=31):
    gcyl(g, x, y, z, z + 4, 2, m)
    gcyl(g, x, y, z + 1, z + 1, 2, 74)
    gcyl(g, x, y, z + 3, z + 3, 2, 74)


def prop_crate(g, x, y, z, m=32, s=3):
    gbox(g, (x, y, z), (x + s, y + s, z + s), m)
    for i in range(0, s + 1):
        gset(g, x + i, y, z, 30)
        gset(g, x, y + i, z + s, 30)


def prop_sack(g, x, y, z, m=82):
    gbox(g, (x, y, z), (x + 2, y + 1, z + 2), m)
    gset(g, x + 1, y, z + 3, 82)


def prop_bench(g, x, y, z, along="x", m=31):
    if along == "x":
        gbox(g, (x, y, z), (x + 4, y + 1, z + 1), m)
        gcol(g, x, y, z - 1, z - 1, 30)
        gcol(g, x + 4, y, z - 1, z - 1, 30)
    else:
        gbox(g, (x, y, z), (x + 1, y + 4, z + 1), m)
        gcol(g, x, y, z - 1, z - 1, 30)
        gcol(g, x, y + 4, z - 1, z - 1, 30)


def prop_lantern(g, x, y, z, m=79):
    gcol(g, x, y, z, z + 4, 74)
    gset(g, x, y, z + 5, m)
    gset(g, x, y, z + 6, 74)


def prop_fence(g, x0, y0, x1, y1, z, m=31, h=3):
    if x0 == x1:
        for y in range(min(y0, y1), max(y0, y1) + 1):
            for zz in range(z, z + h):
                gset(g, x0, y, zz, m if zz < z + h - 1 else 30)
    else:
        for x in range(min(x0, x1), max(x0, x1) + 1):
            for zz in range(z, z + h):
                gset(g, x, y0, zz, m if zz < z + h - 1 else 30)


def prop_pot(g, x, y, z, m=43):
    gbox(g, (x, y, z), (x + 1, y + 1, z + 1), m)
    gset(g, x, y, z + 2, 65)


def prop_tool(g, x, y, z, m=74):
    gset(g, x, y, z, m)
    gcol(g, x, y, z + 1, z + 2, 31)


def prop_cart(g, x, y, z, along="x"):
    if along == "x":
        gbox(g, (x, y, z + 1), (x + 6, y + 3, z + 2), 31)
        gbox(g, (x + 1, y, z + 3), (x + 5, y + 3, z + 4), 30)
        gcyl(g, x + 1, y, z, z + 3, 2, 30, filled=False)
        gcyl(g, x + 5, y, z, z + 3, 2, 30, filled=False)
    else:
        gbox(g, (x, y, z + 1), (x + 3, y + 6, z + 2), 31)
        gcyl(g, x, y + 1, z, z + 3, 2, 30, filled=False)
        gcyl(g, x, y + 5, z, z + 3, 2, 30, filled=False)


def prop_well(g, x, y, z):
    gcyl(g, x, y, z, z + 3, 3, 42, filled=False)
    gcyl(g, x, y, z, z + 1, 2, 20)
    for dx in (-2, 2):
        for dy in (-2, 2):
            gcol(g, x + dx, y + dy, z + 3, z + 9, 31)
    for dy in (-2, 2):
        gbox(g, (x - 2, y + dy, z + 9), (x + 2, y + dy, z + 9), 30)
    for k in range(4):
        for dx in range(-3 + k, 4 - k):
            for dy in range(-3 + k, 4 - k):
                gset(g, x + dx, y + dy, z + 12 + k, 53)


def prop_stall(g, x, y, z, cloth=70):
    for dx in (-3, 3):
        for dy in (-2, 2):
            gcol(g, x + dx, y + dy, z, z + 6, 31)
    gbox(g, (x - 3, y - 2, z + 6), (x + 3, y + 2, z + 7), cloth)
    gbox(g, (x - 3, y - 2, z + 3), (x + 3, y + 2, z + 3), 31)
    for k in range(3):
        prop_crate(g, x - 2 + k * 2, y - 1, z + 4, 32, s=2)


def prop_tree(g, x, y, z, kind=0):
    if kind == 0:
        gcol(g, x, y, z, z + 8, 64)
        gsph(g, x, y, z + 11, 4.4, 61, squash=0.85)
        gsph(g, x, y, z + 12, 3.0, 62, squash=0.9)
    elif kind == 1:
        gcol(g, x, y, z, z + 11, 64)
        gcyl(g, x, y, z + 12, z + 14, 4, 60, filled=False)
        gcyl(g, x, y, z + 6, z + 8, 3, 60, filled=False)
        gsph(g, x, y, z + 15, 3.4, 60, squash=0.7)
    elif kind == 2:
        gcol(g, x, y, z, z + 6, 64)
        gsph(g, x, y, z + 9, 5.0, 63, squash=0.55)
    else:
        gcol(g, x, y, z, z + 5, 64)
        gsph(g, x, y, z + 8, 3.6, 62, squash=0.7)


def prop_bush(g, x, y, z, m=65):
    gsph(g, x, y, z + 1, 2.0, m, squash=0.8, cut_below=z)


veg = gnew(cell=CELL)
random.seed(20261002)
trees = 0
for _ in range(1800):
    x = random.randint(4, N - 5)
    y = random.randint(4, N - 5)
    if not free_at(x, y, 2):
        continue
    if (x, y) in painted:
        continue
    z = ground_z(x, y)
    if z < Z_WATER + 2:
        continue
    if ground_top.get((x, y), 0) >= Z_TERR3:
        continue
    k = random.random()
    if k < 0.13 and trees < 90:
        prop_tree(veg, x, y, z + 1, random.randint(0, 3))
        trees += 1
        block((x - 2, y - 2, x + 2, y + 2), pad=0)
    elif k < 0.34 and free_at(x, y, 0):
        prop_bush(veg, x, y, z + 1, 65 if random.random() < 0.7 else 99)
        block((x - 1, y - 1, x + 1, y + 1), pad=0)
    elif k < 0.38:
        gset(veg, x, y, z + 1, random.choice([88, 89, 90, 98]))
obj_veg = emit(veg, "HB_Vegetation", MATS, colls["Vegetation"], merge=True)

fence = gnew(cell=CELL)
for (fx, fy, fw, fh) in FIELDS:
    z = ground_z(fx, fy)
    prop_fence(fence, fx - 1, fy - 1, fx + fw, fy - 1, z + 1, 31, 3)
    prop_fence(fence, fx - 1, fy + fh, fx + fw, fy + fh, z + 1, 31, 3)
    prop_fence(fence, fx - 1, fy - 1, fx - 1, fy + fh, z + 1, 31, 3)
    prop_fence(fence, fx + fw, fy - 1, fx + fw, fy + fh, z + 1, 31, 3)
    for k in range(0, fw - 2, 3):
        gcol(fence, fx + 2 + k, fy + 2, z + 1, z + 3, 89)
        gcol(fence, fx + 2 + k, fy + fh - 3, z + 1, z + 3, 81)
obj_fence = emit(fence, "HB_Fences", MATS, colls["Props"], merge=True)

props = gnew(cell=CELL)
sqx = (SQ[0] + SQ[2]) // 2
sqy = (SQ[1] + SQ[3]) // 2
z_sq = SQ_Z
prop_well(props, sqx, sqy, z_sq + 1)
block((sqx - 3, sqy - 3, sqx + 3, sqy + 3), pad=0)

for (sx, sy2, cl) in [(sqx - 9, sqy - 5, 70), (sqx - 9, sqy + 5, 71),
                      (sqx + 9, sqy - 5, 72), (sqx + 9, sqy + 5, 92),
                      (sqx, sqy - 8, 93)]:
    if free_at(sx, sy2, 0):
        prop_stall(props, sx, sy2, z_sq + 1, cl)
        block((sx - 4, sy2 - 3, sx + 4, sy2 + 3), pad=0)

for bx, by, al in [(sqx - 6, sqy + 8, "x"), (sqx + 5, sqy - 9, "x"),
                   (sqx - 12, sqy, "y")]:
    if free_at(bx, by, 0):
        prop_bench(props, bx, by, z_sq + 2, al, 31)

for lx, ly in [(SQ[0] + 1, SQ[1] + 1), (SQ[2] - 1, SQ[1] + 1),
               (SQ[0] + 1, SQ[3] - 1), (SQ[2] - 1, SQ[3] - 1)]:
    prop_lantern(props, lx, ly, z_sq + 1)

for (name, kind) in [("Warehouse", "cart"), ("Watermill", "sacks"),
                     ("Great Barn", "hay"), ("Smithy", "tools"),
                     ("Bakery", "pots"), ("Stable", "cart"),
                     ("The Bridge Inn", "barrels")]:
    pd = PAD_INFO.get(name)
    if pd is None:
        continue
    cx2, cy2, z2 = pd["x"], pd["y"], pd["z"]
    if kind == "cart":
        prop_cart(props, cx2 - pd["hw"] - 6, cy2, z2 + 1, "y")
    elif kind == "sacks":
        for i in range(4):
            prop_sack(props, cx2 - pd["hw"] - 5, cy2 - 4 + i * 3, z2 + 1)
    elif kind == "hay":
        for i in range(3):
            gbox(props, (cx2 - 4 + i * 5, cy2 + pd["hd"] + 4, z2 + 1),
                 (cx2 - 1 + i * 5, cy2 + pd["hd"] + 7, z2 + 4), 81)
    elif kind == "tools":
        for i in range(3):
            prop_tool(props, cx2 - pd["hw"] - 4, cy2 - 3 + i * 3, z2 + 1)
    elif kind == "pots":
        for i in range(3):
            prop_pot(props, cx2 + pd["hw"] + 3, cy2 - 3 + i * 3, z2 + 1)
    elif kind == "barrels":
        for i in range(4):
            prop_barrel(props, cx2 + pd["hw"] + 4, cy2 - 5 + i * 3, z2 + 1)

for mx in range(88, 118, 6):
    my = int(round(sy(mx))) + 2
    if 0 <= my < N:
        gcol(props, mx, my, Z_WATER + 1, Z_WATER + 4, 30)
boat_x = 108
boat_y = int(round(sy(108))) - 3
gbox(props, (boat_x, boat_y, Z_WATER), (boat_x + 9, boat_y + 3, Z_WATER + 1), 31)
for k in range(10):
    gset(props, boat_x + k, boat_y, Z_WATER + 2, 30)
    gset(props, boat_x + k, boat_y + 3, Z_WATER + 2, 30)
gbox(props, (boat_x + 4, boat_y + 1, Z_WATER + 2),
     (boat_x + 5, boat_y + 2, Z_WATER + 4), 30)
# a dock beside the boat
for x in range(boat_x - 12, boat_x - 3):
    yy = int(round(sy(x)))
    for y in range(yy - 4, yy + 2):
        if 0 <= y < N:
            gset(props, x, y, Z_WATER + 1, 31)
obj_props = emit(props, "HB_Props", MATS, colls["Props"], merge=True)

people = gnew(cell=CELL)
POSE_SEAT = 0
POSE_WALK = 1
POSE_CARRY = 2
POSE_WORK = 3
POSE_STAND = 4
PEOPLE = [
    (sqx - 4, sqy + 3, POSE_STAND, 70, 85), (sqx + 3, sqy - 4, POSE_WALK, 71, 86),
    (sqx + 8, sqy + 2, POSE_CARRY, 92, 85), (sqx - 11, sqy - 2, POSE_SEAT, 73, 86),
    (58, None, POSE_WORK, 87, 85), (46, None, POSE_WORK, 74, 85),
    (110, None, POSE_CARRY, 70, 86), (134, None, POSE_WALK, 71, 85),
    (60, None, POSE_WORK, 73, 86), (156, None, POSE_STAND, 92, 85),
    (38, None, POSE_WALK, 72, 86), (166, None, POSE_WORK, 87, 85),
    (78, None, POSE_CARRY, 70, 86), (100, None, POSE_WALK, 71, 85),
    (28, None, POSE_STAND, 93, 86), (146, None, POSE_SEAT, 73, 85),
    (120, None, POSE_WALK, 71, 85), (86, None, POSE_CARRY, 92, 86),
]


def person(g, x, y, z, pose, shirt, hair):
    gbox(g, (x, y, z), (x, y, z + 2), 34)
    if pose == POSE_WALK:
        gset(g, x, y, z, 34)
        gset(g, x + 1, y, z + 1, 34)
    gbox(g, (x - 1, y, z + 3), (x + 1, y, z + 5), shirt)
    if pose == POSE_CARRY:
        gbox(g, (x - 1, y - 1, z + 3), (x + 1, y - 1, z + 4), 84)
    if pose == POSE_WORK:
        gbox(g, (x - 1, y + 1, z + 2), (x + 1, y + 1, z + 5), 84)
    if pose == POSE_SEAT:
        gbox(g, (x, y, z), (x, y, z + 1), 34)
        gbox(g, (x - 1, y - 1, z + 3), (x + 1, y - 1, z + 5), shirt)
    gset(g, x, y, z + 6, hair)
    gset(g, x, y, z + 7, 84)
    if pose == POSE_STAND:
        gset(g, x - 1, y, z + 4, shirt)
        gset(g, x + 1, y, z + 4, shirt)


placed_people = 0
for i, (px2, py2, pose, shirt, hair) in enumerate(PEOPLE):
    if py2 is None:
        py2 = int(round(ny(px2) - (12 if i % 2 else 40))) + (2 if i % 3 else -2)
    px2 = max(6, min(N - 8, px2))
    py2 = max(6, min(N - 8, py2))
    pz = ground_top.get((px2, py2), Z_TERR1)
    if pz < Z_WATER:
        continue
    person(people, px2, py2, pz + 1, pose, shirt, hair)
    placed_people += 1
obj_people = emit(people, "HB_Residents", MATS, colls["Residents"], merge=True)

animals = gnew(cell=CELL)


def animal_sheep(g, x, y, z, m=95):
    gbox(g, (x, y, z + 1), (x + 2, y + 3, z + 3), m)
    gset(g, x + 3, y + 1, z + 2, 96)
    gset(g, x + 3, y + 2, z + 2, 96)
    for dx in (0, 2):
        for dy in (0, 3):
            gcol(g, x + dx, y + dy, z, z, 96)


def animal_cow(g, x, y, z, m=94):
    gbox(g, (x, y, z + 2), (x + 3, y + 3, z + 4), m)
    gset(g, x + 4, y + 1, z + 3, m)
    gset(g, x + 4, y + 2, z + 3, 95)
    for dx in (0, 3):
        for dy in (0, 3):
            gcol(g, x + dx, y + dy, z, z + 1, 96)


def animal_chicken(g, x, y, z, m=97):
    gset(g, x, y, z + 1, m)
    gset(g, x, y, z + 2, m)
    gset(g, x, y, z + 3, 88)
    gset(g, x, y, z, 89)
    gset(g, x + 1, y, z, 89)


def animal_dog(g, x, y, z, m=94):
    gbox(g, (x, y, z + 1), (x + 2, y + 1, z + 2), m)
    gset(g, x + 3, y, z + 2, m)
    gset(g, x + 3, y, z + 3, 96)
    gset(g, x + 4, y, z + 3, 96)
    for dx in (0, 2):
        gcol(g, x + dx, y, z, z, 96)


ANIMALS = []
for (fx, fy, fw, fh) in FIELDS:
    for k in range(4):
        ANIMALS.append(("sheep", fx + 4 + k * 6, fy + 5 + (k % 3) * 4))
    ANIMALS.append(("cow", fx + fw // 2, fy + fh - 6))
    ANIMALS.append(("cow", fx + fw // 2 + 5, fy + fh - 6))
for k in range(4):
    ANIMALS.append(("chicken", sqx - 14 + k * 3, sqy + 9))
ANIMALS.append(("dog", sqx + 4, sqy + 9))
ANIMALS.append(("dog", 112, int(round(ny(112) - 40)) + 4))
ANIMALS.append(("chicken", 44, int(round(sy(44) + 16))))
ANIMALS.append(("chicken", 47, int(round(sy(47) + 16))))
ANIMALS.append(("chicken", 50, int(round(sy(50) + 16))))

placed_animals = 0
for (tag, ax, ay) in ANIMALS:
    ax = max(4, min(N - 6, ax))
    ay = max(4, min(N - 6, ay))
    az = ground_top.get((ax, ay), Z_BANKLOW)
    if az < Z_WATER:
        continue
    if tag == "sheep":
        animal_sheep(animals, ax, ay, az + 1)
    elif tag == "cow":
        animal_cow(animals, ax, ay, az + 1)
    elif tag == "chicken":
        animal_chicken(animals, ax, ay, az + 1)
    else:
        animal_dog(animals, ax, ay, az + 1)
    placed_animals += 1
obj_animals = emit(animals, "HB_Animals", MATS, colls["Animals"], merge=True)


# ================================ ANIMATION =================================
def fcurves_of(ob):
    ad = ob.animation_data
    if ad is None or ad.action is None:
        return []
    out = []
    for lay in ad.action.layers:
        for st in lay.strips:
            if hasattr(st, "channelbags"):
                for cb in st.channelbags:
                    out.extend(cb.fcurves)
    return out


def set_interp(ob, mode):
    for fc in fcurves_of(ob):
        for kp in fc.keyframe_points:
            kp.interpolation = mode


SC = bpy.context.scene
SC.render.fps = 24
SC.frame_start = 1
SC.frame_end = 240

# wheel: bake the pivot so a local-X rotation spins it about the axle
if obj_wheel is not None and WHEEL_PIVOT:
    pw = mathutils.Vector(WHEEL_PIVOT)
    for v in obj_wheel.data.vertices:
        v.co -= pw
    obj_wheel.location = pw
    obj_wheel.rotation_mode = "XYZ"
    obj_wheel.rotation_euler = (0.0, 0.0, 0.0)
    obj_wheel.keyframe_insert("rotation_euler", frame=1)
    obj_wheel.rotation_euler = (-2.0 * math.pi / 3.0, 0.0, 0.0)
    obj_wheel.keyframe_insert("rotation_euler", frame=80)
    obj_wheel.rotation_euler = (-4.0 * math.pi / 3.0, 0.0, 0.0)
    obj_wheel.keyframe_insert("rotation_euler", frame=160)
    obj_wheel.rotation_euler = (-2.0 * math.pi, 0.0, 0.0)
    obj_wheel.keyframe_insert("rotation_euler", frame=240)
    set_interp(obj_wheel, "LINEAR")

if obj_smoke is not None:
    obj_smoke.location = (0.0, 0.0, 0.0)
    obj_smoke.keyframe_insert("location", frame=1)
    obj_smoke.location = (0.7, 0.4, 2.4)
    obj_smoke.keyframe_insert("location", frame=120)
    obj_smoke.location = (0.0, 0.0, 0.0)
    obj_smoke.keyframe_insert("location", frame=240)

for fr, dz in ((1, 0.0), (60, 0.07), (120, 0.0), (180, -0.07), (240, 0.0)):
    obj_water.location = (0.0, 0.0, dz)
    obj_water.keyframe_insert("location", frame=fr)

for mname in ("HB_win_lit", "HB_lantern"):
    mat = bpy.data.materials.get(mname)
    if mat is None or not mat.use_nodes:
        continue
    bsdf = next(n for n in mat.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    inp = bsdf.inputs["Emission Strength"]
    base = inp.default_value
    for fr, mul in ((1, 1.00), (20, 1.18), (38, 0.88), (57, 1.24), (76, 0.94),
                    (96, 1.12), (118, 0.86), (140, 1.20), (162, 0.92),
                    (184, 1.15), (206, 0.90), (222, 1.10), (240, 1.00)):
        inp.default_value = base * mul
        inp.keyframe_insert("default_value", frame=fr)

# ================================ LIGHTING ==================================
lcoll = colls["Lighting"]
for o in list(lcoll.objects):
    d = o.data
    bpy.data.objects.remove(o, do_unlink=True)
    if d is not None and d.users == 0 and isinstance(d, bpy.types.Light):
        bpy.data.lights.remove(d)


def make_light(name, kind, loc, rot, energy, color, size=None, angle=None):
    ld = bpy.data.lights.new(name, kind)
    ld.energy = energy
    ld.color = color
    if size is not None and kind == "AREA":
        ld.size = size
    if angle is not None and kind == "SUN":
        ld.angle = angle
    ob = bpy.data.objects.new(name, ld)
    lcoll.objects.link(ob)
    ob.location = loc
    ob.rotation_euler = rot
    return ob


make_light("LGT_Day_Sun", "SUN", (20.0, -30.0, 80.0),
           (math.radians(50.0), 0.0, math.radians(38.0)),
           3.6, (1.0, 0.96, 0.88), angle=0.09)
make_light("LGT_Day_Fill", "AREA", (48.0, -20.0, 50.0),
           (math.radians(40.0), 0.0, math.radians(15.0)),
           1400.0, (0.70, 0.80, 1.0), size=80.0)
make_light("LGT_Eve_Sun", "SUN", (-40.0, 60.0, 30.0),
           (math.radians(76.0), 0.0, math.radians(-42.0)),
           1.0, (1.0, 0.58, 0.32), angle=0.22)
make_light("LGT_Eve_Fill", "AREA", (48.0, 48.0, 40.0),
           (math.radians(12.0), 0.0, math.radians(-120.0)),
           300.0, (0.40, 0.50, 0.95), size=90.0)

for n in ("LGT_Day_Sun", "LGT_Day_Fill", "LGT_Eve_Sun", "LGT_Eve_Fill"):
    ob = bpy.data.objects.get(n)
    if ob is None:
        continue
    is_day = n.startswith("LGT_Day")
    ob.hide_render = is_day
    ob.keyframe_insert("hide_render", frame=1)
    ob.keyframe_insert("hide_render", frame=115)
    ob.hide_render = not is_day
    ob.keyframe_insert("hide_render", frame=116)
    ob.keyframe_insert("hide_render", frame=240)
    set_interp(ob, "CONSTANT")

world = SC.world
if world is None:
    world = bpy.data.worlds.new("HB_World")
    SC.world = world
world.use_nodes = True
bg = next(n for n in world.node_tree.nodes if n.type == "BACKGROUND")
DAY_SKY = (0.40, 0.54, 0.74, 1.0)
EVE_SKY = (0.07, 0.10, 0.20, 1.0)
bg.inputs[0].default_value = DAY_SKY
bg.inputs[1].default_value = 1.20
bg.inputs[0].keyframe_insert("default_value", frame=1)
bg.inputs[1].keyframe_insert("default_value", frame=1)
bg.inputs[0].keyframe_insert("default_value", frame=115)
bg.inputs[1].keyframe_insert("default_value", frame=115)
bg.inputs[0].default_value = EVE_SKY
bg.inputs[1].default_value = 0.30
bg.inputs[0].keyframe_insert("default_value", frame=116)
bg.inputs[1].keyframe_insert("default_value", frame=116)
bg.inputs[0].keyframe_insert("default_value", frame=240)
bg.inputs[1].keyframe_insert("default_value", frame=240)
for fc in fcurves_of(world):
    for kp in fc.keyframe_points:
        kp.interpolation = "CONSTANT"

# ================================= CAMERAS ==================================
ccoll = colls["Cameras"]
for o in list(ccoll.objects):
    d = o.data
    bpy.data.objects.remove(o, do_unlink=True)
    if d is not None and d.users == 0 and isinstance(d, bpy.types.Camera):
        bpy.data.cameras.remove(d)


def look_at(ob, target):
    d = mathutils.Vector((target[0] - ob.location.x,
                          target[1] - ob.location.y,
                          target[2] - ob.location.z))
    ob.rotation_euler = d.to_track_quat("-Z", "Y").to_euler()


def make_cam(name, loc, target, lens=50.0, ortho=None):
    cd = bpy.data.cameras.new(name)
    cd.lens = lens
    cd.clip_end = 900.0
    if ortho is not None:
        cd.type = "ORTHO"
        cd.ortho_scale = ortho
    ob = bpy.data.objects.new(name, cd)
    ccoll.objects.link(ob)
    ob.location = loc
    look_at(ob, target)
    return ob


def w(cell_x, cell_y, cell_z=0.0):
    """cell coords -> world metres"""
    return (cell_x * CELL, cell_y * CELL, cell_z * CELL)


TH = PAD_INFO["Town Hall"]
SQW = (SQ[0] + SQ[2]) / 2.0, (SQ[1] + SQ[3]) / 2.0
MILL = PAD_INFO["Watermill"]
BR = (BX, (NYB + SYB) / 2.0)

make_cam("CAM_Main_3Q", (128.0, -34.0, 54.0), (46.0, 40.0, 6.0), lens=52.0)
make_cam("CAM_Overview_Ortho", (48.0, 48.0, 132.0), (48.0, 48.0, 4.0), ortho=106.0)
make_cam("CAM_Opposite", (-40.0, 132.0, 50.0), (50.0, 52.0, 6.0), lens=52.0)
make_cam("CAM_Close_Square",
         (w(SQ[0] - 16, SQ[1] - 14, 26)), w(SQW[0], SQW[1], 12), lens=42.0)
make_cam("CAM_Close_TownHall",
         (w(TH["x"] + 30, TH["y"] - 24, 30)), w(TH["x"], TH["y"], 26), lens=45.0)
make_cam("CAM_Close_Bridge",
         (w(BX - 26, NYB - 20, 22)), w(BR[0], BR[1], 10), lens=45.0)
make_cam("CAM_Close_Mill",
         (w(MILL_X - 22, MILL["y"] - 26, 20)),
         w(MILL_X + 1, MILL["y"] - 6, 12), lens=45.0)
make_cam("CAM_Close_Market_High",
         (w(sqx + 4, sqy + 30, 30)), w(sqx - 2, sqy, 12), lens=50.0)

orbit = make_cam("CAM_Orbit", (126.0, 48.0, 34.0), (48.0, 48.0, 5.0), lens=50.0)
for fr in range(1, 241, 8):
    ang = 2.0 * math.pi * (fr - 1) / 240.0
    px = 48.0 + 78.0 * math.cos(ang)
    py = 48.0 + 78.0 * math.sin(ang)
    pz = 34.0 + 5.0 * math.sin(ang * 2.0)
    orbit.location = (px, py, pz)
    orbit.keyframe_insert("location", frame=fr)
    look_at(orbit, (48.0, 48.0, 5.0))
    orbit.keyframe_insert("rotation_euler", frame=fr)
orbit.location = (126.0, 48.0, 34.0)
orbit.keyframe_insert("location", frame=240)
look_at(orbit, (48.0, 48.0, 5.0))
orbit.keyframe_insert("rotation_euler", frame=240)
set_interp(orbit, "LINEAR")

SC.camera = bpy.data.objects["CAM_Main_3Q"]
SC.render.resolution_x = 1400
SC.render.resolution_y = 800
SC.render.image_settings.file_format = "PNG"
SC.frame_set(1)

# keep the built metadata on the master object
obj_buildings["hb_pad_info_json"] = json.dumps(PAD_INFO)
obj_buildings["hb_built_info_json"] = json.dumps(BUILT)

print("STAGE5 " + json.dumps({
    "terrain": len(obj_terrain.data.polygons),
    "water": len(obj_water.data.polygons),
    "bridge": len(obj_bridge.data.polygons),
    "buildings": len(obj_buildings.data.polygons),
    "wheel": len(obj_wheel.data.polygons) if obj_wheel else 0,
    "smoke": len(obj_smoke.data.polygons) if obj_smoke else 0,
    "interiors": len(obj_inter.data.polygons) if obj_inter else 0,
    "veg": len(obj_veg.data.polygons),
    "props": len(obj_props.data.polygons),
    "fences": len(obj_fence.data.polygons),
    "people": placed_people,
    "animals": placed_animals,
    "pad_z": {k: v["z"] for k, v in PAD_INFO.items()},
    "pad_y": {k: v["y"] for k, v in PAD_INFO.items()},
}))