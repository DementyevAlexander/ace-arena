"""Geometry helpers: polygon transform, rasterization, flood fill, contour tracing, simplification."""
import math
from collections import deque

SQ2 = math.sqrt(2.0)


def transform_points(pts, x, y, scale, angle_deg, flip_h, flip_v, rotate):
    """Local type Points -> map space. Order: scale, flip, rotate (screen CCW, y down), translate."""
    a = math.radians(angle_deg) if rotate else 0.0
    ca, sa = math.cos(a), math.sin(a)
    out = []
    for px, py in pts:
        px *= scale
        py *= scale
        if flip_h:
            px = -px
        if flip_v:
            py = -py
        rx = px * ca + py * sa
        ry = -px * sa + py * ca
        out.append([round(rx + x, 1), round(ry + y, 1)])
    return out


def poly_area(poly):
    s = 0.0
    n = len(poly)
    for i in range(n):
        x1, y1 = poly[i]
        x2, y2 = poly[(i + 1) % n]
        s += x1 * y2 - x2 * y1
    return s / 2.0


def point_in_poly(x, y, poly):
    c = False
    n = len(poly)
    for i in range(n):
        x1, y1 = poly[i]
        x2, y2 = poly[(i + 1) % n]
        if (y1 > y) != (y2 > y) and x < (x2 - x1) * (y - y1) / (y2 - y1) + x1:
            c = not c
    return c


# ---- iso ground frame: map (x,y) -> (u,v) with iso diagonals axis-aligned --------------
def to_uv(x, y):
    return ((x + 2 * y) / SQ2, (2 * y - x) / SQ2)


def from_uv(u, v):
    # u = (x+2y)/s2, v = (2y-x)/s2 -> x = (u - v)/s2 ; 2y = (u+v)/s2
    return ((u - v) / SQ2, (u + v) / (2 * SQ2))


class Grid:
    def __init__(self, umin, vmin, umax, vmax, cell):
        self.cell = cell
        self.u0 = umin
        self.v0 = vmin
        self.w = int(math.ceil((umax - umin) / cell)) + 1
        self.h = int(math.ceil((vmax - vmin) / cell)) + 1
        self.blocked = bytearray(self.w * self.h)

    def cell_center(self, i, j):
        return (self.u0 + (i + 0.5) * self.cell, self.v0 + (j + 0.5) * self.cell)

    def fill_poly_uv(self, poly_uv, value=1):
        """Scanline fill: mark cells whose centers lie inside polygon."""
        if len(poly_uv) < 3:
            return
        vs = [p[1] for p in poly_uv]
        jmin = max(0, int((min(vs) - self.v0) / self.cell) - 1)
        jmax = min(self.h - 1, int((max(vs) - self.v0) / self.cell) + 1)
        n = len(poly_uv)
        for j in range(jmin, jmax + 1):
            cv = self.v0 + (j + 0.5) * self.cell
            xs = []
            for k in range(n):
                u1, v1 = poly_uv[k]
                u2, v2 = poly_uv[(k + 1) % n]
                if (v1 > cv) != (v2 > cv):
                    xs.append(u1 + (cv - v1) * (u2 - u1) / (v2 - v1))
            xs.sort()
            for a, b in zip(xs[0::2], xs[1::2]):
                i0 = max(0, int(math.ceil((a - self.u0) / self.cell - 0.5)))
                i1 = min(self.w - 1, int(math.floor((b - self.u0) / self.cell - 0.5)))
                row = j * self.w
                for i in range(i0, i1 + 1):
                    self.blocked[row + i] = value

    def mark_poly_edges(self, poly_uv):
        """Also block cells touched by thin polygon edges (so 10-unit-thin walls are not skipped)."""
        n = len(poly_uv)
        step = self.cell * 0.5
        for k in range(n):
            u1, v1 = poly_uv[k]
            u2, v2 = poly_uv[(k + 1) % n]
            L = math.hypot(u2 - u1, v2 - v1)
            m = max(1, int(L / step))
            for s in range(m + 1):
                t = s / m
                u = u1 + (u2 - u1) * t
                v = v1 + (v2 - v1) * t
                i = int((u - self.u0) / self.cell)
                j = int((v - self.v0) / self.cell)
                if 0 <= i < self.w and 0 <= j < self.h:
                    self.blocked[j * self.w + i] = 1

    def flood(self, seeds_uv):
        w, h = self.w, self.h
        reach = bytearray(w * h)
        q = deque()
        for (u, v) in seeds_uv:
            i = int((u - self.u0) / self.cell)
            j = int((v - self.v0) / self.cell)
            if 0 <= i < w and 0 <= j < h:
                # nudge to nearest free cell within 3 cells
                best = None
                for dj in range(-3, 4):
                    for di in range(-3, 4):
                        ii, jj = i + di, j + dj
                        if 0 <= ii < w and 0 <= jj < h and not self.blocked[jj * w + ii]:
                            d = di * di + dj * dj
                            if best is None or d < best[0]:
                                best = (d, ii, jj)
                if best:
                    k = best[2] * w + best[1]
                    if not reach[k]:
                        reach[k] = 1
                        q.append(k)
        leaks = False
        while q:
            k = q.popleft()
            i, j = k % w, k // w
            if i == 0 or j == 0 or i == w - 1 or j == h - 1:
                leaks = True
            for nk, ok in ((k - 1, i > 0), (k + 1, i < w - 1), (k - w, j > 0), (k + w, j < h - 1)):
                if ok and not reach[nk] and not self.blocked[nk]:
                    reach[nk] = 1
                    q.append(nk)
        return reach, leaks


def trace_regions(mask, w, h):
    """Trace boundaries of a binary cell mask into closed loops of grid-corner coords (i,j).
    Outer boundaries are CCW (positive area in a y-up sense of (i,j)), holes CW."""
    def at(i, j):
        return 0 <= i < w and 0 <= j < h and mask[j * w + i]

    # directed edges with region on the left (i right, j up convention)
    nxt = {}
    for j in range(h):
        for i in range(w):
            if not mask[j * w + i]:
                continue
            if not at(i, j - 1):  # bottom edge, go +i
                nxt.setdefault((i, j), []).append((i + 1, j))
            if not at(i + 1, j):  # right edge, go +j
                nxt.setdefault((i + 1, j), []).append((i + 1, j + 1))
            if not at(i, j + 1):  # top edge, go -i
                nxt.setdefault((i + 1, j + 1), []).append((i, j + 1))
            if not at(i - 1, j):  # left edge, go -j
                nxt.setdefault((i, j + 1), []).append((i, j))
    loops = []
    while nxt:
        start = next(iter(nxt))
        loop = [start]
        cur = start
        prev_dir = None
        while True:
            outs = nxt.get(cur)
            if not outs:
                break
            if len(outs) == 1:
                nx = outs.pop()
            else:
                # ambiguous corner: prefer the left-most turn to keep regions separated
                def turn(o):
                    d = (o[0] - cur[0], o[1] - cur[1])
                    if prev_dir is None:
                        return 0
                    cross = prev_dir[0] * d[1] - prev_dir[1] * d[0]
                    return -cross
                outs.sort(key=turn)
                nx = outs.pop(0)
            if not outs:
                del nxt[cur]
            prev_dir = (nx[0] - cur[0], nx[1] - cur[1])
            cur = nx
            if cur == start:
                break
            loop.append(cur)
        if len(loop) >= 4:
            loops.append(loop)
    return loops


def simplify_closed(pts, tol):
    """Douglas-Peucker on a closed polyline."""
    if len(pts) < 4:
        return pts
    # remove collinear points first
    def dp(seq):
        if len(seq) < 3:
            return seq
        (x1, y1), (x2, y2) = seq[0], seq[-1]
        dx, dy = x2 - x1, y2 - y1
        L = math.hypot(dx, dy) or 1e-9
        idx, dmax = 0, -1.0
        for k in range(1, len(seq) - 1):
            px, py = seq[k]
            d = abs(dy * px - dx * py + x2 * y1 - y2 * x1) / L
            if d > dmax:
                idx, dmax = k, d
        if dmax > tol:
            return dp(seq[:idx + 1])[:-1] + dp(seq[idx:])
        return [seq[0], seq[-1]]
    # split at farthest point from first
    p0 = pts[0]
    far = max(range(len(pts)), key=lambda k: (pts[k][0] - p0[0]) ** 2 + (pts[k][1] - p0[1]) ** 2)
    a = dp(pts[:far + 1])
    b = dp(pts[far:] + [pts[0]])
    out = a[:-1] + b[:-1]
    return out
