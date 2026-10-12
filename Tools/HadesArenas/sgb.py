"""Чтение карт Hades II: Content/Maps/bin/*.thing_bin (заголовок SGB1, версия 12).

Формат разобран вручную. Запись (вещь на карте):
  u8 ?, u8 ?, u8 0, f32 (-1), f32 Angle,
  u32 n + n*u32 AttachedIDs, 6 байт (AttachToID и др.), u8, RGBA Color, 5 байт, u8, 9 байт флагов,
  u32 n + n*(u8 1, строка) GroupNames, u8 + 3*f32 (Hue/Saturation/Value),
  u32 Id, 2 байта, f32 X, f32 Y, u8 1, строка Name,
  6*f32 (OffsetZ, ParallaxAmount, ?, Scale, SkewAngle, SkewScale), 16 байт.
Строка — u32 длина + байты. Координаты — пиксели экрана, Y вниз.
"""
import struct


class _Reader:
    def __init__(self, data):
        self.d = data
        self.o = 0

    def u8(self):
        v = self.d[self.o]; self.o += 1; return v

    def u32(self):
        v = struct.unpack_from('<I', self.d, self.o)[0]; self.o += 4; return v

    def f32(self):
        v = struct.unpack_from('<f', self.d, self.o)[0]; self.o += 4; return v

    def skip(self, n):
        self.o += n

    def string(self):
        n = self.u32()
        v = self.d[self.o:self.o + n].decode('latin1'); self.o += n
        return v


def read_things(path):
    data = open(path, 'rb').read()
    r = _Reader(data)
    if data[:4] != b'SGB1':
        raise ValueError(f'{path}: не SGB1')
    r.skip(12)  # SGB1, версия, число записей
    count = struct.unpack_from('<I', data, 8)[0]
    things = []
    for _ in range(count):
        flag_a, flag_b = r.u8(), r.u8()
        r.skip(1)
        r.f32()
        angle = r.f32()
        r.skip(4 * r.u32())
        r.skip(6 + 1 + 4 + 5 + 1 + 9)
        groups = []
        for _ in range(r.u32()):
            if r.u8() != 1:
                raise ValueError(f'{path}: сбой разбора на 0x{r.o:x}')
            groups.append(r.string())
        r.skip(13)
        tid = r.u32()
        r.skip(2)
        x, y = r.f32(), r.f32()
        if r.u8() != 1:
            raise ValueError(f'{path}: сбой разбора на 0x{r.o:x}')
        name = r.string()
        f6 = struct.unpack_from('<6f', data, r.o); r.skip(24 + 16)
        things.append({'id': tid, 'name': name, 'groups': groups, 'x': x, 'y': y,
                       'angle': angle, 'scale': f6[3], 'skew': (f6[4], f6[5]), 'flags': (flag_a, flag_b)})
    if r.o != len(data):
        raise ValueError(f'{path}: лишние байты в конце')
    return things
