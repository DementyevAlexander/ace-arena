"""Tiny pure-python RGB canvas + PNG writer (no PIL)."""
import math
import struct
import zlib


class Canvas:
    def __init__(self, w, h, bg=(255, 255, 255)):
        self.w, self.h = w, h
        self.px = bytearray(bytes(bg) * (w * h))

    def set(self, x, y, c):
        if 0 <= x < self.w and 0 <= y < self.h:
            k = (y * self.w + x) * 3
            self.px[k:k + 3] = bytes(c)

    def blend(self, x, y, c, a):
        if 0 <= x < self.w and 0 <= y < self.h:
            k = (y * self.w + x) * 3
            for i in range(3):
                self.px[k + i] = int(self.px[k + i] * (1 - a) + c[i] * a)

    def poly(self, pts, c, alpha=1.0):
        if len(pts) < 3:
            return
        ys = [p[1] for p in pts]
        y0 = max(0, int(math.floor(min(ys))))
        y1 = min(self.h - 1, int(math.ceil(max(ys))))
        n = len(pts)
        for y in range(y0, y1 + 1):
            cy = y + 0.5
            xs = []
            for k in range(n):
                xa, ya = pts[k]
                xb, yb = pts[(k + 1) % n]
                if (ya > cy) != (yb > cy):
                    xs.append(xa + (cy - ya) * (xb - xa) / (yb - ya))
            xs.sort()
            for a, b in zip(xs[0::2], xs[1::2]):
                for x in range(max(0, int(math.ceil(a - 0.5))), min(self.w - 1, int(math.floor(b - 0.5))) + 1):
                    if alpha >= 1.0:
                        self.set(x, y, c)
                    else:
                        self.blend(x, y, c, alpha)

    def line(self, x0, y0, x1, y1, c):
        n = int(max(abs(x1 - x0), abs(y1 - y0))) + 1
        for s in range(n + 1):
            t = s / n
            self.set(int(round(x0 + (x1 - x0) * t)), int(round(y0 + (y1 - y0) * t)), c)

    def outline(self, pts, c):
        for k in range(len(pts)):
            x0, y0 = pts[k]
            x1, y1 = pts[(k + 1) % len(pts)]
            self.line(x0, y0, x1, y1, c)

    def disc(self, cx, cy, r, c):
        for y in range(int(cy - r), int(cy + r) + 1):
            for x in range(int(cx - r), int(cx + r) + 1):
                if (x - cx) ** 2 + (y - cy) ** 2 <= r * r:
                    self.set(x, y, c)

    def save(self, path):
        raw = bytearray()
        row = self.w * 3
        for y in range(self.h):
            raw.append(0)
            raw += self.px[y * row:(y + 1) * row]

        def chunk(t, d):
            return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
        data = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', self.w, self.h, 8, 2, 0, 0, 0))
        data += chunk(b'IDAT', zlib.compress(bytes(raw), 6)) + chunk(b'IEND', b'')
        with open(path, 'wb') as f:
            f.write(data)
