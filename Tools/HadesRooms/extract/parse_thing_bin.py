#!/usr/bin/env python3
"""Parser for Hades (Supergiant, 2020) `*.thing_bin` map instance files, format "SGB1" v12.

Usage:  python3 -I parse_thing_bin.py <file.thing_bin> [-o out.json]

Layout (little endian), reverse-engineered; see FORMAT.md for confidence per field.
  header : char[4] "SGB1" | u32 version (=12) | u32 thingCount
  string : u8 present (0 = null, 1 = present) [ u32 len | bytes[len] ]   (no terminator)
  thing  :
    u32  recordTag            always 1
    u32  flags                bitfield, 0x14416 typical (bit16 / bit24 vary)
    f32  ambient              -1 = inherit from type
    f32  angle                degrees
    u32  nAttached, u32[n]    AttachedIDs
    u32  attachToId           0 = none
    u8   boolA, u8 boolB, u8 boolC     (boolC = 1 for ~87% of things; meaning unknown)
    u8[4] color RGBA
    str  comments             (only seen in non-shipping maps, e.g. "Spawner")
    u32  triStateA            0=Undefined 1=False 2=True  (per-instance override)
    u32  dataType             1 = Obstacle, 2 = Unit
    u32  triStateB            0=Undefined 1=False 2=True  (per-instance override)
    u8   flipHorizontal, u8 flipVertical
    u32  nGroups, { i32 groupId, str groupName }[n]   (ids match map_text ThingGroups; 'Default' = -3)
    u8   boolD, f32 hue, f32 saturation, f32 value
    u32  id
    u8   pad0, u8 pad1
    f32  x, f32 y             Location (map space, see FORMAT.md)
    str  name                 obstacle / unit type name
    f32  offsetZ
    f32  parallaxOrScaleY     1.0 typical, 0.7..1.3 on some art (unknown, not used for geometry)
    u32  nPoints, { f32 x, f32 y }[n]   per-instance collision polygon override (rare)
    f32  scale
    f32  skewAngle            0 or 180 mostly
    f32  skewScale
    f32  unk6, f32 unk7       always 0 in shipping maps
    f32  tallness             0 = inherit
    u32  unk9
"""
import json
import struct
import sys

MAGIC = b'SGB1'


class ThingBinError(Exception):
    pass


class _Reader:
    def __init__(self, data):
        self.d = data
        self.p = 0

    def u8(self):
        v = self.d[self.p]
        self.p += 1
        return v

    def u32(self):
        v = struct.unpack_from('<I', self.d, self.p)[0]
        self.p += 4
        return v

    def i32(self):
        v = struct.unpack_from('<i', self.d, self.p)[0]
        self.p += 4
        return v

    def f32(self):
        v = struct.unpack_from('<f', self.d, self.p)[0]
        self.p += 4
        return v

    def raw(self, n):
        v = self.d[self.p:self.p + n]
        self.p += n
        return v

    def s(self):
        flag = self.u8()
        if flag == 0:
            return None
        if flag != 1:
            raise ThingBinError('bad string flag %d at 0x%x' % (flag, self.p - 1))
        n = self.u32()
        if n > 4096:
            raise ThingBinError('implausible string length %d at 0x%x' % (n, self.p - 4))
        return self.raw(n).decode('utf-8', 'replace')


def _r(v, nd=3):
    return round(v, nd)


def parse_thing(r):
    t = {}
    start = r.p
    tag = r.u32()
    if tag != 1:
        raise ThingBinError('record tag %d != 1 at 0x%x' % (tag, start))
    t['flags'] = r.u32()
    t['ambient'] = _r(r.f32())
    t['angle'] = _r(r.f32(), 4)
    n = r.u32()
    if n > 10000:
        raise ThingBinError('implausible AttachedIDs count %d at 0x%x' % (n, start))
    t['attachedIds'] = [r.u32() for _ in range(n)]
    t['attachToId'] = r.u32()
    t['boolA'], t['boolB'], t['boolC'] = r.u8(), r.u8(), r.u8()
    t['color'] = list(r.raw(4))
    t['comments'] = r.s()
    t['triStateA'] = r.u32()
    t['dataType'] = {1: 'Obstacle', 2: 'Unit'}.get(r.u32(), 'Unknown')
    t['triStateB'] = r.u32()
    t['flipHorizontal'] = bool(r.u8())
    t['flipVertical'] = bool(r.u8())
    ng = r.u32()
    t['groups'] = []
    t['groupIds'] = []
    for _ in range(ng):
        t['groupIds'].append(r.i32())
        t['groups'].append(r.s())
    t['boolD'] = r.u8()
    t['hue'], t['saturation'], t['value'] = _r(r.f32()), _r(r.f32()), _r(r.f32())
    t['id'] = r.u32()
    r.raw(2)
    t['x'], t['y'] = _r(r.f32(), 2), _r(r.f32(), 2)
    t['name'] = r.s()
    t['offsetZ'] = _r(r.f32())
    t['unk1'] = _r(r.f32())
    npts = r.u32()
    if npts > 10000:
        raise ThingBinError('implausible point count %d at 0x%x' % (npts, start))
    t['points'] = [[_r(r.f32(), 2), _r(r.f32(), 2)] for _ in range(npts)]
    t['scale'] = _r(r.f32(), 4)
    t['skewAngle'] = _r(r.f32())
    t['skewScale'] = _r(r.f32())
    t['unk6'] = _r(r.f32())
    t['unk7'] = _r(r.f32())
    t['tallness'] = _r(r.f32())
    t['unk9'] = r.u32()
    t['_offset'] = start
    return t


def parse_bytes(data):
    if data[:4] != MAGIC:
        raise ThingBinError('not an SGB1 thing_bin (legacy/unsupported format)')
    r = _Reader(data)
    r.p = 4
    version = r.u32()
    count = r.u32()
    things = [parse_thing(r) for _ in range(count)]
    if r.p != len(data):
        raise ThingBinError('trailing bytes: parsed %d of %d' % (r.p, len(data)))
    return {'version': version, 'count': count, 'things': things}


def parse_file(path):
    with open(path, 'rb') as f:
        return parse_bytes(f.read())


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    res = parse_file(argv[1])
    out = json.dumps(res, indent=1)
    if '-o' in argv:
        with open(argv[argv.index('-o') + 1], 'w') as f:
            f.write(out)
    else:
        print(out)
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
