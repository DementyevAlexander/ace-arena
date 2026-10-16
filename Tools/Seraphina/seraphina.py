"""Серафина — ангел-хранитель (по концепту Seraphina_reference.png).
Запуск в Blender: exec(open(".../Tools/Seraphina/seraphina.py").read())
Персонаж смотрит в -Y, макушка капюшона ~1.0 м, ноги на z=0. Правая рука (-X) держит меч, левая (+X) — кадило."""
import bpy, bmesh, math
from mathutils import Vector, Matrix
from mathutils.bvhtree import BVHTree

TAU = math.tau
# лёгкая сетка для игры (ставит export_fbx.py), полная — для рендеров
GAME = bool(globals().get("SERAPHINA_GAME"))


def V(*a):
    return Vector(a)


def sstep(e0, e1, x):
    t = max(0.0, min(1.0, (x - e0) / (e1 - e0)))
    return t * t * (3 - 2 * t)


def herm(pts, x):
    """Гладкая интерполяция по точкам (x, y) с касательными Катмулла–Рома."""
    if x <= pts[0][0]:
        return pts[0][1]
    if x >= pts[-1][0]:
        return pts[-1][1]
    n = len(pts)

    def m(i):
        a, b = pts[max(i - 1, 0)], pts[min(i + 1, n - 1)]
        return (b[1] - a[1]) / (b[0] - a[0])

    for i in range(n - 1):
        x0, y0 = pts[i]
        x1, y1 = pts[i + 1]
        if x0 <= x <= x1:
            h = x1 - x0
            t = (x - x0) / h
            t2, t3 = t * t, t * t * t
            return ((2 * t3 - 3 * t2 + 1) * y0 + (t3 - 2 * t2 + t) * h * m(i)
                    + (-2 * t3 + 3 * t2) * y1 + (t3 - t2) * h * m(i + 1))


# ---------------------------------------------------------------- сцена
def reset():
    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o, do_unlink=True)
    for coll in (bpy.data.meshes, bpy.data.curves, bpy.data.materials, bpy.data.lights, bpy.data.cameras,
                 bpy.data.node_groups):
        for d in list(coll):
            coll.remove(d)
    for c in list(bpy.data.collections):
        bpy.data.collections.remove(c)


reset()
SCN = bpy.context.scene
COL = bpy.data.collections.new("Seraphina")
SCN.collection.children.link(COL)
STAGE = bpy.data.collections.new("Stage")
SCN.collection.children.link(STAGE)


# ---------------------------------------------------------------- материалы
def lin(c):
    out = []
    for x in c:
        x = x / 255
        out.append(x / 12.92 if x <= 0.04045 else ((x + 0.055) / 1.055) ** 2.4)
    return tuple(out)


def principled(m):
    return next(n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED")


def mat(name, rgb, rough=0.6, metal=0.0, emit=None, estr=0.0, alpha=1.0, coat=0.0):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    b = principled(m)
    col = lin(rgb) + (1.0,)
    b.inputs["Base Color"].default_value = col
    b.inputs["Roughness"].default_value = rough
    b.inputs["Metallic"].default_value = metal
    if coat:
        b.inputs["Coat Weight"].default_value = coat
    if emit:
        b.inputs["Emission Color"].default_value = lin(emit) + (1.0,)
        b.inputs["Emission Strength"].default_value = estr
    if alpha < 1:
        b.inputs["Alpha"].default_value = alpha
        try:
            m.surface_render_method = "BLENDED"
        except Exception:
            m.blend_method = "BLEND"
    m.diffuse_color = col
    return m


M_ROBE = mat("Robe", (236, 222, 196), rough=0.62)
M_ROBE_IN = mat("RobeInner", (170, 150, 120), rough=0.8)
M_PORC = mat("Porcelain", (226, 214, 194), rough=0.42, coat=0.15)
M_LIMB = mat("PorcelainLimb", (205, 190, 168), rough=0.48)
M_GOLD = mat("Gold", (232, 178, 78), rough=0.34, metal=0.65)
M_GOLD_DK = mat("GoldDark", (190, 132, 52), rough=0.38, metal=0.65)
M_BRONZE = mat("Bronze", (150, 92, 48), rough=0.38, metal=0.8)
M_BLADE = mat("Blade", (250, 242, 222), rough=0.25, emit=(255, 246, 225), estr=1.6)
M_FLAME = mat("BladeFlame", (232, 160, 60), rough=0.3, emit=(255, 170, 60), estr=0.9)
M_EYE = mat("EyeGlow", (255, 246, 220), rough=0.3, emit=(255, 238, 200), estr=6.0)
M_GEM = mat("Gem", (255, 236, 200), rough=0.1, emit=(255, 214, 150), estr=3.5)
M_EMBER = mat("Ember", (255, 140, 60), rough=0.5, emit=(255, 120, 40), estr=4.0)
M_SMOKE = mat("Smoke", (200, 165, 235), rough=1.0, emit=(185, 135, 230), estr=1.6, alpha=0.7)


def feather_mat():
    m = bpy.data.materials.new("Feather")
    m.use_nodes = True
    nt = m.node_tree
    b = principled(m)
    b.inputs["Roughness"].default_value = 0.6
    at = nt.nodes.new("ShaderNodeAttribute")
    at.attribute_name = "rim"
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    els = ramp.color_ramp.elements
    els[0].position, els[0].color = 0.0, lin((246, 238, 220)) + (1,)
    els[1].position, els[1].color = 1.0, lin((214, 160, 80)) + (1,)
    mid = els.new(0.55)
    mid.color = lin((240, 226, 196)) + (1,)
    nt.links.new(at.outputs["Fac"], ramp.inputs["Fac"])
    nt.links.new(ramp.outputs["Color"], b.inputs["Base Color"])
    # металличность только на золотой кромке
    mr = nt.nodes.new("ShaderNodeMapRange")
    mr.inputs["From Min"].default_value = 0.6
    mr.inputs["From Max"].default_value = 1.0
    mr.inputs["To Max"].default_value = 0.55
    nt.links.new(at.outputs["Fac"], mr.inputs["Value"])
    nt.links.new(mr.outputs["Result"], b.inputs["Metallic"])
    m.diffuse_color = lin((246, 238, 220)) + (1,)
    return m


M_FEATHER = feather_mat()


# ---------------------------------------------------------------- построитель мешей
class MB:
    def __init__(self):
        self.bm = bmesh.new()
        self.mats = []
        self.rim = self.bm.verts.layers.float.new("rim")

    def mi(self, m):
        if m not in self.mats:
            self.mats.append(m)
        return self.mats.index(m)

    def v(self, p, rim=0.0):
        vv = self.bm.verts.new(p)
        vv[self.rim] = rim
        return vv

    def f(self, vs, m, smooth=True):
        try:
            fa = self.bm.faces.new(vs)
        except ValueError:
            return None
        fa.material_index = self.mi(m)
        fa.smooth = smooth
        return fa

    def loft(self, rings, m, closed=True, cap0=False, cap1=False, smooth=True, rims=None):
        vr = []
        for i, r in enumerate(rings):
            vr.append([self.v(p, rims[i][j] if rims else 0.0) for j, p in enumerate(r)])
        for i in range(len(vr) - 1):
            a, b = vr[i], vr[i + 1]
            if len(b) == 1:
                rng = range(len(a)) if closed else range(len(a) - 1)
                for j in rng:
                    self.f([a[j], a[(j + 1) % len(a)], b[0]], m, smooth)
            elif len(a) == 1:
                rng = range(len(b)) if closed else range(len(b) - 1)
                for j in rng:
                    self.f([a[0], b[(j + 1) % len(b)], b[j]], m, smooth)
            else:
                rng = range(len(a)) if closed else range(len(a) - 1)
                for j in rng:
                    self.f([a[j], a[(j + 1) % len(a)], b[(j + 1) % len(b)], b[j]], m, smooth)
        if cap0 and len(vr[0]) > 2:
            self.f(list(reversed(vr[0])), m, smooth)
        if cap1 and len(vr[-1]) > 2:
            self.f(vr[-1], m, smooth)
        return vr

    def grid(self, G, m, smooth=True, flip=False):
        """G[i][j] — сетка точек; грани между соседями."""
        vs = [[self.v(p) for p in row] for row in G]
        for i in range(len(vs) - 1):
            for j in range(len(vs[0]) - 1):
                q = [vs[i][j], vs[i + 1][j], vs[i + 1][j + 1], vs[i][j + 1]]
                self.f(list(reversed(q)) if flip else q, m, smooth)
        return vs

    def finish(self, name, parent=None, subsurf=0, recalc=True, merge=0.0, solidify=0.0):
        if merge:
            bmesh.ops.remove_doubles(self.bm, verts=self.bm.verts, dist=merge)
        if recalc:
            bmesh.ops.recalc_face_normals(self.bm, faces=self.bm.faces)
        me = bpy.data.meshes.new(name)
        for m in self.mats:
            me.materials.append(m)
        self.bm.to_mesh(me)
        self.bm.free()
        ob = bpy.data.objects.new(name, me)
        COL.objects.link(ob)
        if solidify:
            md = ob.modifiers.new("Solidify", "SOLIDIFY")
            md.thickness = solidify
            md.offset = -1
        if subsurf:
            md = ob.modifiers.new("Subsurf", "SUBSURF")
            md.levels = subsurf
            md.render_levels = subsurf
        if parent:
            ob.parent = parent
        return ob


def basis(d, ref=V(0, 0, 1)):
    d = d.normalized()
    if abs(d.dot(ref)) > 0.95:
        ref = V(1, 0, 0) if abs(d.x) < 0.9 else V(0, 1, 0)
    a = ref.cross(d).normalized()
    b = d.cross(a).normalized()
    return a, b


def ring_axis(c, d, r, n, rot=0.0, ry=None, ref=V(0, 0, 1)):
    a, b = basis(d, ref)
    ry = r if ry is None else ry
    return [c + a * (math.cos(rot + TAU * i / n) * r) + b * (math.sin(rot + TAU * i / n) * ry) for i in range(n)]


def lathe(mb, c, axis, prof, n, m, rot=0.0, smooth=True, cap0=False, cap1=False, ref=V(0, 0, 1)):
    axis = axis.normalized()
    rings = []
    for (r, h) in prof:
        cc = c + axis * h
        rings.append([cc] if r <= 1e-6 else ring_axis(cc, axis, r, n, rot, ref=ref))
    return mb.loft(rings, m, cap0=cap0, cap1=cap1, smooth=smooth)


def tube(mb, pts, radii, n, m, cap0=True, cap1=True, smooth=True, ref=V(0, 0, 1), flat=1.0, closed_path=False):
    """Трубка по точкам с параллельным переносом рамки (без перекрута)."""
    rings = []
    a_prev = None
    N = len(pts)
    for i, p in enumerate(pts):
        if closed_path:
            d = pts[(i + 1) % N] - pts[(i - 1) % N]
        else:
            d = pts[min(i + 1, N - 1)] - pts[max(i - 1, 0)]
        d.normalize()
        if a_prev is None:
            a, b = basis(d, ref)
        else:
            a = (a_prev - d * a_prev.dot(d)).normalized()
            b = d.cross(a).normalized()
        a_prev = a
        r = radii[i] if isinstance(radii, (list, tuple)) else radii
        if r <= 1e-6:
            rings.append([p])
        else:
            rings.append([p + a * (math.cos(TAU * k / n) * r) + b * (math.sin(TAU * k / n) * r * flat) for k in range(n)])
    if closed_path:
        rings.append(rings[0])
        vr = mb.loft(rings[:-1], m, smooth=smooth)
        a, b = vr[-1], vr[0]
        for j in range(len(a)):
            mb.f([a[j], a[(j + 1) % len(a)], b[(j + 1) % len(b)], b[j]], m, smooth)
        return vr
    return mb.loft(rings, m, cap0=cap0, cap1=cap1, smooth=smooth)


def ellipsoid(mb, c, r, m, sub=3, rot=None, smooth=True):
    tmp = bmesh.new()
    bmesh.ops.create_icosphere(tmp, subdivisions=sub, radius=1.0)
    R = rot.to_matrix() if rot is not None else Matrix.Identity(3)
    vm = {}
    for v in tmp.verts:
        p = V(v.co.x * r[0], v.co.y * r[1], v.co.z * r[2])
        vm[v] = mb.v(c + R @ p)
    for f in tmp.faces:
        mb.f([vm[v] for v in f.verts], m, smooth)
    tmp.free()


def qbez(P0, P1, P2, t):
    return P0 * (1 - t) ** 2 + P1 * (2 * t * (1 - t)) + P2 * (t * t)


# ---------------------------------------------------------------- поверхность мантии-робы
ROBE = [(0.07, 0.296), (0.10, 0.300), (0.16, 0.286), (0.25, 0.258), (0.35, 0.229), (0.45, 0.201),
        (0.52, 0.181), (0.56, 0.163), (0.59, 0.130), (0.615, 0.088)]
DEPTH = 0.80


def robe_r(z):
    return herm(ROBE, z)


def robe_base(phi, z):
    r = robe_r(z)
    fold = 1 + (0.014 * math.sin(10 * phi + 0.4) + 0.006 * math.sin(17 * phi + 1.3)) * (1 - sstep(0.10, 0.46, z)) * (0.35 + 0.65 * abs(math.sin(phi)))
    return V(r * math.sin(phi) * fold, -r * DEPTH * math.cos(phi) * fold, z)


def robe_n(phi, z):
    e = 1e-4
    dp = robe_base(phi + e, z) - robe_base(phi - e, z)
    dz = robe_base(phi, z + e) - robe_base(phi, z - e)
    n = dp.cross(dz).normalized()
    p = robe_base(phi, z)
    if n.dot(V(p.x, p.y, 0)) < 0:
        n = -n
    return n


def surf(phi, z, off=0.0):
    return robe_base(phi, z) + robe_n(phi, z) * off


def front_phi(x, z):
    return math.asin(max(-0.99, min(0.99, x / robe_r(z))))


def ribbon(mb, path, width, off, thick, m, closed=False, step=None, prof=None):
    """Выпуклая лента по поверхности робы. path — список (phi, z)."""
    step = step or (0.012 if GAME else 0.006)
    # уплотнить путь
    pts = []
    P = path + ([path[0]] if closed else [])
    for i in range(len(P) - 1):
        a, b = P[i], P[i + 1]
        la = (surf(*a) - surf(*b)).length
        k = max(1, int(la / step))
        for s in range(k):
            t = s / k
            pts.append((a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t))
    if not closed:
        pts.append(P[-1])
    prof = prof or [(-0.5, 0.0), (-0.5, 0.55), (-0.32, 1.0), (0.32, 1.0), (0.5, 0.55), (0.5, 0.0)]
    rings = []
    N = len(pts)
    for i, (ph, z) in enumerate(pts):
        p = surf(ph, z, off)
        if closed:
            T = surf(*pts[(i + 1) % N], off) - surf(*pts[(i - 1) % N], off)
        else:
            T = surf(*pts[min(i + 1, N - 1)], off) - surf(*pts[max(i - 1, 0)], off)
        T.normalize()
        n = robe_n(ph, z)
        B = n.cross(T).normalized()
        rings.append([p + B * (u * width) + n * (h * thick) for (u, h) in prof])
    if closed:
        vr = mb.loft(rings, m)
        a, b = vr[-1], vr[0]
        for j in range(len(a)):
            mb.f([a[j], a[(j + 1) % len(a)], b[(j + 1) % len(b)], b[j]], m)
    else:
        mb.loft(rings, m, cap0=True, cap1=True)


# ---------------------------------------------------------------- тело
ZTOP, ZV = 0.600, 0.418
M_OUT, M_IN = 0.020, 0.004


def phi_open(z):
    return 0.0 if z <= ZV else 0.66 * ((z - ZV) / (ZTOP - ZV)) ** 0.85


def zlow(phi):
    return 0.425 + 0.06 * math.sin(phi) ** 2 - 0.02 * (1 - math.cos(phi)) / 2


def build_robe(root):
    mb = MB()
    nphi, dz = (72, 0.018) if GAME else (112, 0.012)
    zs = [0.072 + i * dz for i in range(int((0.615 - 0.072) / dz) + 1)]
    rings = [[V(0, 0, 0.082)]]
    rings.append([robe_base(TAU * j / nphi, 0.068) * 0.93 + V(0, 0, 0.068 * 0.07) for j in range(nphi)])
    for z in zs:
        rings.append([robe_base(TAU * j / nphi, z) for j in range(nphi)])
    rings.append([V(0, 0, 0.622)])
    mb.loft(rings, M_ROBE)
    ob = mb.finish("Robe", root)

    # передний фартук-полотно: от подола до шеи, под вырезом мантии
    mb = MB()
    nz, nu = 40, 14

    def half_w(z):
        return 0.074 + (0.44 - z) * 0.11 if z < 0.44 else 0.074 - (z - 0.44) * 0.05

    Go, Gi = [], []
    for i in range(nz + 1):
        z = 0.080 + (0.600 - 0.080) * i / nz
        ro, ri = [], []
        for j in range(nu + 1):
            x = -half_w(z) + 2 * half_w(z) * j / nu
            ph = front_phi(x, z)
            ro.append(surf(ph, z, 0.009))
            ri.append(surf(ph, z, 0.001))
        Go.append(ro)
        Gi.append(ri)
    shell(mb, Go, Gi, M_ROBE)
    tab = mb.finish("Tabard", root)

    # золото фартука
    mb = MB()
    for sd in (-1, 1):
        path = [(front_phi(sd * (half_w(z) - 0.008), z), z) for z in [0.085 + 0.02 * k for k in range(19)]]
        ribbon(mb, path, 0.021, 0.009, 0.008, M_GOLD)
    # шеврон и ромб внизу фартука
    P = lambda x, z: (front_phi(x, z), z)
    ribbon(mb, [P(-0.075, 0.355), P(0.0, 0.29), P(0.075, 0.355)], 0.017, 0.009, 0.007, M_GOLD)
    ribbon(mb, [P(-0.06, 0.300), P(0.0, 0.245), P(0.06, 0.300)], 0.012, 0.009, 0.006, M_GOLD)
    ribbon(mb, [P(0, 0.235), P(0.052, 0.170), P(0, 0.112), P(-0.052, 0.170)], 0.014, 0.009, 0.007, M_GOLD, closed=True)
    ribbon(mb, [P(0, 0.198), P(0.022, 0.170), P(0, 0.142), P(-0.022, 0.170)], 0.016, 0.009, 0.008, M_GOLD, closed=True)
    # кайма подола
    ribbon(mb, [(TAU * k / 64, 0.098) for k in range(64)], 0.036, 0.010, 0.011, M_GOLD, closed=True)
    ribbon(mb, [(TAU * k / 64, 0.128) for k in range(64)], 0.006, 0.006, 0.006, M_GOLD, closed=True)
    # дужки над каймой на спине и боках
    for k in range(18):
        c = math.pi * 0.42 + (TAU - math.pi * 0.84) * k / 17
        arc = [(c + 0.07 * math.cos(a), 0.127 + 0.018 * math.sin(a)) for a in [math.pi * i / 6 for i in range(7)]]
        ribbon(mb, arc, 0.006, 0.006, 0.005, M_GOLD)
    mb.finish("RobeGold", root)
    return ob


def shell(mb, Go, Gi, m):
    """Пластина толщиной: внешняя и внутренняя сетки, сшитые по краю."""
    vo = mb.grid(Go, m)
    vi = mb.grid(Gi, m, flip=True)
    nI, nJ = len(Go), len(Go[0])
    edge = ([(0, j) for j in range(nJ)] + [(i, nJ - 1) for i in range(1, nI)] +
            [(nI - 1, j) for j in range(nJ - 2, -1, -1)] + [(i, 0) for i in range(nI - 2, 0, -1)])
    for k in range(len(edge)):
        a, b = edge[k], edge[(k + 1) % len(edge)]
        mb.f([vo[a[0]][a[1]], vo[b[0]][b[1]], vi[b[0]][b[1]], vi[a[0]][a[1]]], m)


def mantle_pt(u, v, off):
    z = ZTOP
    ph = 0.0
    for _ in range(4):
        po = phi_open(z)
        ph = po + u * (TAU - 2 * po)
        z = ZTOP + (zlow(ph) - ZTOP) * v
    bulge = 0.012 * math.sin(math.pi * v) * (0.5 + 0.5 * abs(math.sin(ph)))  # плечи пухлее
    return surf(ph, z, off + bulge), ph, z


def build_mantle(root):
    mb = MB()
    nu, nv = (80, 12) if GAME else (120, 16)
    Go = [[mantle_pt(i / nu, j / nv, M_OUT)[0] for j in range(nv + 1)] for i in range(nu + 1)]
    Gi = [[mantle_pt(i / nu, j / nv, M_IN)[0] for j in range(nv + 1)] for i in range(nu + 1)]
    shell(mb, Go, Gi, M_ROBE)
    ob = mb.finish("Mantle", root)

    mb = MB()
    OFF = M_OUT + 0.002
    zs = [ZTOP - (ZTOP - ZV) * k / 24 for k in range(25)]
    for sd in (1, -1):
        # кромка выреза
        path = [(sd * (phi_open(z) + 0.010 / robe_r(z)), z) for z in zs]
        ribbon(mb, path, 0.024, OFF, 0.009, M_GOLD)
        # внешняя кромка отворота
        zz = [ZTOP - (ZTOP - zlow(0.35)) * k / 24 for k in range(25)]
        path = [(sd * (phi_open(z) + 0.072 / robe_r(z)), z) for z in zz]
        ribbon(mb, path, 0.019, OFF, 0.008, M_GOLD)
        # зигзаг внутри отворота
        zig = []
        for k in range(7):
            z = ZTOP - 0.02 - (ZTOP - ZV - 0.03) * k / 6
            w = 0.028 if k % 2 == 0 else 0.054
            zig.append((sd * (phi_open(z) + w / robe_r(z)), z))
        ribbon(mb, zig, 0.013, OFF, 0.007, M_GOLD)
    # кайма по низу мантии
    path = [(TAU * k / 96, zlow(TAU * k / 96) + 0.012) for k in range(97)]
    path = [(ph, z) for (ph, z) in path]
    ribbon(mb, [(p, z) for (p, z) in path], 0.024, OFF - 0.004, 0.009, M_GOLD)
    mb.finish("MantleGold", root)
    return ob


# ---------------------------------------------------------------- рукава и кисти
SWORD_HAND = V(-0.285, -0.14, 0.48)
CENSER_HAND = V(0.300, -0.06, 0.335)


def build_arm(root, sd, S, E, H, name):
    mb = MB()
    Wc = H + (E - H).normalized() * 0.040
    P1 = E
    pts = [qbez(S, P1, Wc, i / 18) for i in range(19)]
    radii = [0.056 + 0.012 * (i / 18) ** 1.4 for i in range(19)]
    # трубка рукава, открытая на конце, с подворотом внутрь
    rings = []
    a_prev = None
    for i, p in enumerate(pts):
        d = (pts[min(i + 1, 18)] - pts[max(i - 1, 0)]).normalized()
        if a_prev is None:
            a, b = basis(d)
        else:
            a = (a_prev - d * a_prev.dot(d)).normalized()
            b = d.cross(a)
        a_prev = a
        r = radii[i]
        fold = lambda k: 1 + 0.06 * math.sin(5 * TAU * k / 28 + i * 0.25) * (i / 18) ** 2
        ring = []
        for k in range(28):
            q = (a * math.cos(TAU * k / 28) + b * math.sin(TAU * k / 28)) * r * fold(k)
            # широкий рукав провисает книзу
            q.z -= 0.045 * (i / 18) ** 2 * max(0.0, -q.normalized().z) ** 1.5
            ring.append(p + q)
        rings.append(ring)
    dl = (pts[-1] - pts[-2]).normalized()
    inner = [pts[-1] + (q - pts[-1]) * 0.86 - dl * 0.012 for q in rings[-1]]
    deep = [pts[-1] + (q - pts[-1]) * 0.7 - dl * 0.07 for q in rings[-1]]
    rings += [inner, deep, [pts[-1] - dl * 0.08]]
    mb.loft([[S - (pts[1] - S).normalized() * 0.01]] + rings, M_ROBE)
    # манжета
    a_end = (pts[-1] - pts[-3]).normalized()
    r_end = radii[-1]
    prof = [(r_end * 0.97, -0.060), (r_end + 0.005, -0.062), (r_end + 0.011, -0.052), (r_end + 0.013, -0.010),
            (r_end + 0.009, 0.004), (r_end * 0.97, 0.006), (r_end * 0.9, -0.01)]
    lathe(mb, pts[-1], a_end, prof, 36, M_GOLD)
    lathe(mb, pts[-1] - a_end * 0.03, a_end, [(r_end + 0.013, -0.004), (r_end + 0.016, 0.0), (r_end + 0.013, 0.004)], 36, M_GOLD_DK)
    ob = mb.finish(name, root)
    return ob, a_end


def build_fist(mb, H, axis, grip_axis):
    """Кулак-варежка: тело кисти, валик пальцев, большой палец."""
    axis = axis.normalized()
    side = grip_axis.cross(axis).normalized()
    R = Matrix((side, grip_axis.normalized(), axis)).transposed()
    ellipsoid(mb, H + axis * 0.004, (0.045, 0.049, 0.048), M_LIMB, sub=3, rot=R.to_euler())
    ellipsoid(mb, H + axis * 0.03 + side * 0.0, (0.037, 0.04, 0.022), M_LIMB, sub=3, rot=R.to_euler())
    ellipsoid(mb, H - side * 0.03 + grip_axis * 0.025 + axis * 0.01, (0.015, 0.022, 0.014), M_LIMB, sub=2, rot=R.to_euler())


def build_arms(root):
    _, ax_r = build_arm(root, -1, V(-0.160, 0.01, 0.540), V(-0.335, -0.02, 0.335), SWORD_HAND, "SleeveR")
    _, ax_l = build_arm(root, 1, V(0.160, 0.01, 0.540), V(0.330, 0.0, 0.43), CENSER_HAND, "SleeveL")
    mb = MB()
    build_fist(mb, SWORD_HAND, ax_r, V(0, 0, 1))
    build_fist(mb, CENSER_HAND, ax_l, V(0, -1, 0.2).normalized())
    mb.finish("Hands", root, subsurf=0 if GAME else 1)


# ---------------------------------------------------------------- голова
HC = V(0, 0.0, 0.802)
HOOD_R = (0.186, 0.176, 0.196)


def build_head(root):
    # капюшон с вырезом под лицо
    mb = MB()
    # кольца вокруг оси «вперёд» — край выреза получается ровным овалом
    tilt = Matrix.Rotation(math.radians(-4), 3, "X")
    a0, na, nb = (math.radians(55), 14, 36) if GAME else (math.radians(55), 26, 64)
    rings = []
    for i in range(na):
        a = a0 + (math.pi - a0) * i / na
        ring = []
        for k in range(nb):
            b = TAU * k / nb
            d = tilt @ V(math.sin(a) * math.cos(b), -math.cos(a), math.sin(a) * math.sin(b))
            ring.append(HC + V(d.x * HOOD_R[0], d.y * HOOD_R[1], d.z * HOOD_R[2]))
        rings.append(ring)
    rings.append([HC + tilt @ V(0, HOOD_R[1], 0)])
    mb.loft(rings, M_ROBE)
    hood = mb.finish("Hood", root, solidify=0.024, subsurf=1 if GAME else 2)
    hood.modifiers["Solidify"].use_even_offset = True

    # лицо-маска
    mb = MB()
    FC, FR = HC + V(0, -0.012, -0.004), (0.163, 0.158, 0.172)
    tmp = bmesh.new()
    bmesh.ops.create_icosphere(tmp, subdivisions=5, radius=1.0)
    vm = {}
    for v in tmp.verts:
        p = V(v.co.x * FR[0], v.co.y * FR[1], v.co.z * FR[2])
        # плоская маска спереди (вид сбоку — почти вертикальная плоскость)
        if p.y < -0.095:
            p.y = -0.095 + (p.y + 0.095) * 0.5
        w = p + FC
        # глазницы
        for sx in (-1, 1):
            dx, dz = w.x - sx * 0.054, w.z - 0.818
            g = math.exp(-(dx * dx / 0.034 ** 2 + dz * dz / 0.022 ** 2))
            w.y += 0.016 * g * (1 if w.y < 0 else 0)
            # надбровье
            gb = math.exp(-(dx * dx / 0.04 ** 2 + (w.z - 0.848) ** 2 / 0.012 ** 2))
            w.y -= 0.003 * gb * (1 if w.y < 0 else 0)
        # нос — мягкий валик
        gn = math.exp(-(w.x / 0.014) ** 2) * math.exp(-((w.z - 0.785) / 0.028) ** 2)
        w.y -= 0.010 * gn * (1 if w.y < 0 else 0)
        # скулы
        for sx in (-1, 1):
            gc = math.exp(-(((w.x - sx * 0.07) / 0.04) ** 2 + ((w.z - 0.765) / 0.03) ** 2))
            w.y -= 0.004 * gc * (1 if w.y < 0 else 0)
        vm[v] = mb.v(w)
    for f in tmp.faces:
        mb.f([vm[v] for v in f.verts], M_PORC)
    tmp.free()
    bvh = BVHTree.FromBMesh(mb.bm)
    face = mb.finish("Face", root)

    # светящиеся глаза-миндалины
    mb = MB()
    for sx in (-1, 1):
        o = V(sx * 0.054, -0.5, 0.818)
        hit = bvh.ray_cast(o, V(0, 1, 0))
        p, n = hit[0], hit[1]
        rot = Matrix.Rotation(sx * math.radians(14), 4, "Y") @ Matrix.Rotation(-sx * math.radians(12), 4, "Z")
        ellipsoid(mb, p + V(0, 0.004, 0), (0.037, 0.008, 0.0165), M_EYE, sub=3, rot=rot.to_euler())
    mb.finish("Eyes", root)

    # золотой ворот
    mb = MB()
    pts = [V(math.cos(TAU * k / 48) * 0.102, math.sin(TAU * k / 48) * 0.090, 0.612) for k in range(48)]
    tube(mb, pts, 0.019, 14, M_GOLD, closed_path=True)
    pts = [V(math.cos(TAU * k / 48) * 0.112, math.sin(TAU * k / 48) * 0.099, 0.594) for k in range(48)]
    tube(mb, pts, 0.010, 10, M_GOLD_DK, closed_path=True)
    mb.finish("Collar", root)

    # нимб
    mb = MB()
    pts = [V(math.cos(TAU * k / 72) * 0.142, math.sin(TAU * k / 72) * 0.142, 0) for k in range(72)]
    tube(mb, pts, 0.017, 16, M_GOLD, closed_path=True, flat=0.75)
    halo = mb.finish("Halo", root)
    halo.location = (0, 0.035, 1.035)
    halo.rotation_euler = (math.radians(-12), 0, 0)


# ---------------------------------------------------------------- ноги
def build_legs(root):
    mb = MB()
    for sx in (-1, 1):
        x = sx * 0.075
        prof = [(0.0, 0.0), (0.040, 0.002), (0.052, 0.014), (0.054, 0.032), (0.047, 0.05), (0.043, 0.08), (0.043, 0.13), (0.0, 0.13)]
        lathe(mb, V(x, -0.005, 0.0), V(0, 0, 1), prof, 24, M_LIMB)
    mb.finish("Legs", root, subsurf=1)


# ---------------------------------------------------------------- крылья
def feather(mb, base, d, nrm, L, W, T, m, rimk=1.0, curl=0.02, ns=None, na=3):
    ns = ns or (7 if GAME else 10)
    d = d.normalized()
    side = d.cross(nrm).normalized()
    n = side.cross(d).normalized()
    rings, rims = [], []
    for i in range(1, ns):
        s = i / ns
        hw = 0.5 * W * max(0.0, 1 - ((s - 0.47) / 0.53) ** 2) ** 0.55
        c = base + d * (s * L) + n * (curl * s * s)
        ring, rim = [], []
        vs = [-1 + k / na for k in range(2 * na + 1)]
        for v in vs:
            th = T * (1 - v * v) * (hw / (0.5 * W)) + T * 0.35 * math.exp(-(v / 0.18) ** 2) * (1 - s)
            ring.append(c + side * (hw * v) + n * th)
            rim.append(min(1.0, (abs(v) ** 3) * rimk + (s ** 9) * rimk))
        for v in reversed(vs[1:-1]):
            th = T * 0.5 * (1 - v * v) * (hw / (0.5 * W))
            ring.append(c + side * (hw * v) - n * th)
            rim.append(min(1.0, (abs(v) ** 3) * rimk + (s ** 9) * rimk))
        rings.append(ring)
        rims.append(rim)
    rings = [[base]] + rings + [[base + d * L + n * curl]]
    rims = [[0.0]] + rims + [[rimk]]
    mb.loft(rings, m, rims=rims)


def build_wing(root, sd):
    mb = MB()
    org = V(0.045 * sd, 0.025, 0.0)
    U = V(sd, 0.62, 0).normalized()
    Wd = V(0, 0.16, 1).normalized()
    N = U.cross(Wd).normalized()
    if N.y < 0:
        N = -N

    def to3d(u, w, lift=0.0):
        return org + U * u + Wd * w + N * (lift - 0.30 * u * u)

    A, B, C = V(0.035, 0.60, 0), V(0.205, 0.965, 0), V(0.360, 0.835, 0)
    P1 = B * 2 - (A + C) * 0.5

    def le(t):
        return qbez(A, P1, C, t)

    def fdir(t):
        a = math.radians(-102 + 30 * t ** 1.1)
        return V(math.cos(a), math.sin(a), 0)

    rows = [  # (кол-во, t0, t1, отступ, L0, L1, ширина, подъём, кромка)
        (10, 0.16, 1.00, 0.19, 0.20, 0.31, 0.082, 0.000, 0.80),  # маховые
        (10, 0.08, 1.00, 0.095, 0.17, 0.25, 0.078, 0.016, 0.85),  # второстепенные
        (11, 0.00, 1.00, 0.00, 0.12, 0.17, 0.070, 0.032, 1.00),  # кроющие
    ]
    for (cnt, t0, t1, off, L0, L1, w, lift, rk) in rows:
        for i in range(cnt):
            k = i / (cnt - 1)
            t = t0 + (t1 - t0) * k
            f = 0.62 + 0.38 * sstep(0.0, 0.4, t)
            d2 = fdir(t)
            b2 = le(t) + d2 * (off * f - 0.025)
            L = (L0 + (L1 - L0) * k) * f
            base = to3d(b2.x, b2.y, lift)
            tip = to3d(b2.x + d2.x * L, b2.y + d2.y * L, lift)
            feather(mb, base, tip - base, N, (tip - base).length, w, 0.011, M_FEATHER, rimk=rk, curl=0.018)
    # плечо крыла — валик по переднему краю
    pts = [to3d(le(t).x, le(t).y, 0.046) for t in [i / 24 for i in range(25)]]
    tube(mb, pts, [0.030 - 0.010 * (i / 24) for i in range(25)], 14, M_FEATHER, ref=N)
    for vv in mb.bm.verts:
        pass
    ob = mb.finish(f"Wing{'L' if sd > 0 else 'R'}", root)
    # золото по гребню крыла
    mb = MB()
    pts = [to3d(le(t).x, le(t).y, 0.046) + Wd * (0.022 - 0.007 * t) + N * 0.006 for t in [i / 24 for i in range(25)]]
    tube(mb, pts, [0.0075 - 0.003 * (i / 24) for i in range(25)], 8, M_GOLD)
    mb.finish(f"Wing{'L' if sd > 0 else 'R'}Gold", root)
    return ob


# ---------------------------------------------------------------- меч
def build_sword(root):
    G = SWORD_HAND
    up = V(0, 0, 1)
    mb = MB()
    # рукоять с обмоткой
    prof = [(0.0, -0.075), (0.016, -0.075)]
    for k in range(9):
        h = -0.068 + k * 0.016
        prof += [(0.017, h), (0.0145, h + 0.008)]
    prof += [(0.017, 0.07), (0.0, 0.07)]
    lathe(mb, G, up, prof, 16, M_GOLD_DK)
    # навершие: шар + капля-самоцвет
    ellipsoid(mb, G + V(0, 0, -0.088), (0.024, 0.024, 0.02), M_GOLD, sub=3)
    lathe(mb, G + V(0, 0, -0.10), up, [(0.0, -0.075), (0.012, -0.045), (0.02, -0.02), (0.016, 0.0), (0.0, 0.004)], 8, M_GEM, smooth=False)
    # гарда: дуга с загнутыми вверх концами и шарами
    gz = G + V(0, 0, 0.078)
    pts, radii = [], []
    for k in range(21):
        t = -1 + 2 * k / 20
        pts.append(gz + V(t * 0.112, 0, 0.026 * t ** 4 - 0.004))
        radii.append(0.021 - 0.009 * abs(t))
    tube(mb, pts, radii, 12, M_GOLD, flat=0.65, ref=V(0, 1, 0))
    for sx in (-1, 1):
        ellipsoid(mb, gz + V(sx * 0.118, 0, 0.030), (0.016, 0.013, 0.02), M_GOLD, sub=3)
    # центральный ромб с камнем
    lathe(mb, gz + V(0, -0.006, 0.0), V(0, -1, 0), [(0.0, -0.012), (0.034, -0.01), (0.036, 0.006), (0.0, 0.012)], 4, M_GOLD, rot=math.pi / 4, smooth=False)
    lathe(mb, gz + V(0, -0.016, 0.0), V(0, -1, 0), [(0.02, 0.0), (0.0, 0.012)], 4, M_GEM, rot=math.pi / 4, smooth=False, cap0=True)
    # втулка клинка
    lathe(mb, gz + V(0, 0, 0.012), up, [(0.0, 0), (0.03, 0), (0.026, 0.024), (0.0, 0.024)], 4, M_GOLD, rot=math.pi / 4, smooth=False)
    mb.finish("SwordHilt", root)

    # клинок: шестигранник, широкий плоский дол, острие вверх
    mb = MB()
    b0, L, Wb, T = gz.z + 0.03, 0.76, 0.050, 0.014
    st = [(0.0, 1.0), (0.05, 1.02), (0.80, 0.94), (0.90, 0.68), (1.0, 0.0)]
    rings = []
    for s, k in st:
        z = b0 + L * s
        c = V(G.x, G.y, z)
        if k == 0:
            rings.append([c])
        else:
            w, t = Wb * k, T * max(k, 0.3)
            rings.append([c + V(-w, 0, 0), c + V(-w * 0.62, -t, 0), c + V(w * 0.62, -t, 0), c + V(w, 0, 0),
                          c + V(w * 0.62, t, 0), c + V(-w * 0.62, t, 0)])
    mb.loft(rings, M_BLADE, cap0=True, smooth=False)
    blade = mb.finish("Blade", root)
    # огненный узор по обеим плоскостям клинка
    mb = MB()
    shape = [(0, 0), (0.5, 0.04), (0.78, 0.17), (0.42, 0.30), (0.72, 0.44), (0.30, 0.60), (0.48, 0.72), (0.10, 0.86), (0.0, 1.0),
             (-0.20, 0.84), (-0.06, 0.70), (-0.55, 0.56), (-0.28, 0.42), (-0.70, 0.27), (-0.40, 0.14), (-0.55, 0.05)]
    fw, fh, fz = Wb * 0.72, L * 0.66, b0 + 0.04
    for sy in (-1, 1):
        vs = [mb.v(V(G.x + x * fw, G.y + sy * (T + 0.0012), fz + z * fh)) for (x, z) in shape]
        if sy > 0:
            vs.reverse()
        mb.f(vs, M_FLAME)
    bmesh.ops.triangulate(mb.bm, faces=mb.bm.faces)
    mb.finish("BladeFlame", root, recalc=False)
    flame = bpy.data.objects.new("SwordFlame", None)
    flame.location = (G.x, G.y, b0 + L * 0.5)
    flame.empty_display_type = "CUBE"
    flame.scale = (Wb, T, L * 0.5)
    COL.objects.link(flame)
    flame.parent = blade


# ---------------------------------------------------------------- кадило
def chain(mb, a, b, pitch=0.0125, m=None):
    d = b - a
    n = max(2, int(d.length / pitch))
    dn = d.normalized()
    s1, s2 = basis(dn)
    for k in range(n):
        c = a + d * ((k + 0.5) / n)
        sx = s1 if k % 2 == 0 else s2
        pts = []
        nl = 8 if GAME else 12
        for i in range(nl):
            ang = TAU * i / nl
            pts.append(c + dn * (math.cos(ang) * 0.0095) + sx * (math.sin(ang) * 0.0056))
        tube(mb, pts, 0.0026, 4 if GAME else 6, m or M_GOLD_DK, closed_path=True)


def build_censer(root):
    H = CENSER_HAND
    mb = MB()
    # кольцо в кулаке
    pts = [H + V(math.cos(TAU * k / 20) * 0.02, 0, math.sin(TAU * k / 20) * 0.02 - 0.03) for k in range(20)]
    tube(mb, pts, 0.004, 6, M_BRONZE, closed_path=True)
    top = H + V(0, 0, -0.05)
    z0 = 0.062
    K = 0.74  # высота корпуса
    c0 = V(H.x, H.y, z0)
    up = V(0, 0, 1)
    # корпус-фонарь: ножка, чаша, обод, луковичная крышка, маковка
    prof = [(0.0, 0.0 * K), (0.040, 0.0 * K), (0.046, 0.006 * K), (0.040, 0.013 * K), (0.024, 0.022 * K), (0.026, 0.03 * K),
            (0.055, 0.042 * K), (0.071, 0.062 * K), (0.077, 0.083 * K), (0.074, 0.094 * K)]
    lathe(mb, c0, up, prof, 8, M_BRONZE, rot=math.pi / 8, smooth=False)
    lathe(mb, c0, up, [(0.074, 0.094 * K), (0.084, 0.095 * K), (0.086, 0.104 * K), (0.082, 0.112 * K), (0.070, 0.113 * K)], 8, M_GOLD, rot=math.pi / 8, smooth=False)
    lid = [(0.070, 0.113 * K), (0.072, 0.13 * K), (0.064, 0.152 * K), (0.048, 0.172 * K), (0.030, 0.188 * K), (0.016, 0.2 * K), (0.012, 0.21 * K)]
    lathe(mb, c0, up, lid, 8, M_BRONZE, rot=math.pi / 8, smooth=False)
    lathe(mb, c0, up, [(0.012, 0.21 * K), (0.022, 0.215 * K), (0.022, 0.226 * K), (0.010, 0.232 * K), (0.012, 0.242 * K), (0.0, 0.25 * K)], 8, M_GOLD, rot=math.pi / 8, smooth=False)
    # прорези в крышке — светятся угольки
    for k in range(8):
        a = math.pi / 8 + TAU * k / 8 + TAU / 16
        dirv = V(math.cos(a), math.sin(a), 0)
        for (h0, h1, r0, r1, w) in ((0.12 * K, 0.148 * K, 0.0705, 0.066, 0.012),):
            p0 = c0 + dirv * (r0 + 0.002) + up * h0
            p1 = c0 + dirv * (r1 + 0.002) + up * h1
            tng = up.cross(dirv).normalized()
            vs = [mb.v(p0 - tng * w), mb.v(p0 + tng * w), mb.v(p1 + tng * w * 0.6), mb.v(p1 - tng * w * 0.6)]
            mb.f(vs, M_EMBER, smooth=False)
    # три цепи
    anchors = []
    for k in range(3):
        a = math.pi / 2 + TAU * k / 3
        anchors.append(c0 + V(math.cos(a) * 0.078, math.sin(a) * 0.078, 0.108 * K))
    for an in anchors:
        chain(mb, top, an)
        ellipsoid(mb, an, (0.006, 0.006, 0.006), M_GOLD, sub=2)
    chain(mb, H + V(0, 0, -0.03), top)
    ellipsoid(mb, top, (0.009, 0.009, 0.007), M_GOLD, sub=2)
    ob = mb.finish("Censer", root, recalc=True)
    # пивот у кулака, чтобы кадило качалось
    off = H.copy()
    ob.data.transform(Matrix.Translation(-off))
    ob.location = off
    smoke = bpy.data.objects.new("CenserSmoke", None)
    smoke.location = V(0, 0, c0.z + 0.25 * K - H.z)
    smoke.empty_display_size = 0.05
    COL.objects.link(smoke)
    smoke.parent = ob
    return ob


def build_smoke(root):
    cu = bpy.data.curves.new("SmokeCurve", "CURVE")
    cu.dimensions = "3D"
    cu.bevel_depth = 0.016
    cu.bevel_resolution = 3
    cu.resolution_u = 10
    base = V(CENSER_HAND.x, CENSER_HAND.y, 0.245)
    for (ox, ph, h, rr) in ((0.0, 0.0, 0.30, 1.0), (0.012, 2.1, 0.22, 0.8), (-0.01, 4.0, 0.16, 0.6)):
        sp = cu.splines.new("NURBS")
        n = 9
        sp.points.add(n - 1)
        for i in range(n):
            t = i / (n - 1)
            x = base.x + ox + 0.035 * math.sin(ph + t * 6.0) * (0.4 + t) + 0.05 * t
            y = base.y + 0.02 * math.cos(ph + t * 5.0)
            z = base.z + h * t
            sp.points[i].co = (x, y, z, 1)
            sp.points[i].radius = rr * (0.5 + 1.6 * math.sin(math.pi * min(1, t * 1.15)) ** 0.8) * (1 - t * 0.5)
        sp.use_endpoint_u = True
        sp.order_u = 4
    ob = bpy.data.objects.new("Smoke", cu)
    ob.data.materials.append(M_SMOKE)
    COL.objects.link(ob)
    ob.parent = root


# ---------------------------------------------------------------- сборка
ROOT = bpy.data.objects.new("Seraphina", None)
COL.objects.link(ROOT)
build_robe(ROOT)
build_mantle(ROOT)
build_arms(ROOT)
build_head(ROOT)
build_legs(ROOT)
build_wing(ROOT, 1)
build_wing(ROOT, -1)
build_sword(ROOT)
build_censer(ROOT)
build_smoke(ROOT)


# ---------------------------------------------------------------- свет, камера, фон
def stage():
    w = bpy.data.worlds.get("World") or bpy.data.worlds.new("World")
    SCN.world = w
    w.use_nodes = True
    bg = next(n for n in w.node_tree.nodes if n.type == "BACKGROUND")
    bg.inputs["Color"].default_value = lin((150, 142, 136)) + (1,)
    bg.inputs["Strength"].default_value = 0.7

    def light(name, kind, loc, energy, color, size=1.0, rot=None):
        ld = bpy.data.lights.new(name, kind)
        ld.energy = energy
        ld.color = color
        if kind == "AREA":
            ld.size = size
            ld.shape = "DISK"
        o = bpy.data.objects.new(name, ld)
        o.location = loc
        STAGE.objects.link(o)
        tr = o.constraints.new("TRACK_TO")
        tgt = bpy.data.objects.get("StageTarget")
        tr.target = tgt
        tr.track_axis = "TRACK_NEGATIVE_Z"
        tr.up_axis = "UP_Y"
        return o

    tgt = bpy.data.objects.new("StageTarget", None)
    tgt.location = (0, 0, 0.6)
    STAGE.objects.link(tgt)
    key = light("Key", "SUN", (-1.8, -2.2, 2.6), 3.2, (1.0, 0.93, 0.84))
    key.data.angle = math.radians(12)
    light("Fill", "AREA", (2.2, -1.8, 1.2), 110, (0.86, 0.9, 1.0), size=2.5)
    light("Rim", "AREA", (0.6, 2.4, 2.2), 380, (1.0, 0.92, 0.8), size=1.5)

    fl = bpy.data.meshes.new("Floor")
    bm = bmesh.new()
    bmesh.ops.create_grid(bm, x_segments=1, y_segments=1, size=30)
    bm.to_mesh(fl)
    bm.free()
    fo = bpy.data.objects.new("Floor", fl)
    fl.materials.append(mat("FloorMat", (150, 128, 108), rough=0.8))
    STAGE.objects.link(fo)

    cd = bpy.data.cameras.new("Cam")
    cd.lens = 70
    cam = bpy.data.objects.new("Cam", cd)
    STAGE.objects.link(cam)
    SCN.camera = cam
    tr = cam.constraints.new("TRACK_TO")
    tr.target = tgt
    tr.track_axis = "TRACK_NEGATIVE_Z"
    tr.up_axis = "UP_Y"
    cam.location = (0, -4.2, 0.95)
    SCN.render.resolution_x, SCN.render.resolution_y = 900, 1000
    try:
        SCN.render.engine = "BLENDER_EEVEE"
    except TypeError:
        pass
    SCN.view_settings.view_transform = "AgX"
    SCN.view_settings.look = "AgX - Medium High Contrast" if "AgX - Medium High Contrast" in [
        i.identifier for i in bpy.types.ColorManagedViewSettings.bl_rna.properties["look"].enum_items] else "None"

    glow()


def glow():
    """Мягкое свечение клинка, глаз и угольков (Glare в композиторе); не критично, если API другой."""
    try:
        ng = bpy.data.node_groups.new("SeraphinaComp", "CompositorNodeTree")
        SCN.compositing_node_group = ng
        rl = ng.nodes.new("CompositorNodeRLayers")
        gl = ng.nodes.new("CompositorNodeGlare")
        if "Type" in gl.inputs:
            gl.inputs["Type"].default_value = "Bloom"
        else:
            gl.glare_type = "BLOOM"
        for key, val in (("Threshold", 1.2), ("Strength", 0.6), ("Size", 0.5)):
            if key in gl.inputs:
                gl.inputs[key].default_value = val
        out = ng.interface.new_socket("Image", in_out="OUTPUT", socket_type="NodeSocketColor")
        go = ng.nodes.new("NodeGroupOutput")
        ng.links.new(rl.outputs["Image"], gl.inputs["Image"])
        ng.links.new(gl.outputs["Image"], go.inputs[0])
    except Exception as e:
        print("glow skipped:", e)


stage()
print("Seraphina built:", sorted(o.name for o in COL.objects))
