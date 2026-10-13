#!/usr/bin/env python3
"""Hades room JSON (extract/extract_rooms.py) -> RoomDef JSON for Ace Arena (Assets/_Project/Resources/Rooms).

Usage:
  python3 -I convert_rooms.py <extractedRoomsDir> <outDir> [--enable A_Combat01] [--intro A_Combat01]

Coordinates:
  Hades map space is the screen plane, x right, y down, floor squashed 2:1 vertically.
  Ground plane: g = (x, -2y) around the room centre. The arena camera looks along +45 deg yaw, so the
  screen right axis on the floor is (1, -1)/sqrt2 and screen up is (1, 1)/sqrt2 in world (x, z).
  Scale: K = 4.1 / 540 (Ace run speed / Zagreus run speed).
"""
import json
import math
import os
import sys

K = 4.1 / 540.0
S = math.sqrt(0.5)
COS30 = math.cos(math.radians(30))

# Hades: Asphodel magma 0.5 per hit every 0.19 s -> damage per 0.5 s arena tick
LAVA_PER_TICK = 0.5 * 0.5 / 0.19

# EnemyIntroFight01 (EncounterData.lua:3058): the first fight of Hades
INTRO = {
    'name': 'EnemyIntroFight01',
    'activeCap': 10,
    'generated': True,
    'waves': [
        {'enemies': ['SimpleMelee'], 'startDelay': 0, 'intervalMin': 0.175, 'intervalMax': 0.225, 'preSpawned': True},
        {'enemies': ['HeavyMelee', 'HeavyMelee'], 'startDelay': 1, 'intervalMin': 0.175, 'intervalMax': 0.225, 'preSpawned': False},
        {'enemies': ['LightRanged'] * 3, 'startDelay': 1, 'intervalMin': 1.5, 'intervalMax': 1.5, 'preSpawned': False},
        {'enemies': ['HeavyMelee', 'LightRanged', 'LightRanged', 'LightRanged'], 'startDelay': 1, 'intervalMin': 0.3, 'intervalMax': 0.3, 'preSpawned': False},
    ],
}


class Xf:
    def __init__(self, bounds):
        self.cx = (bounds['minX'] + bounds['maxX']) * 0.5
        self.cy = (bounds['minY'] + bounds['maxY']) * 0.5

    def pt(self, x, y):
        gx, gz = (x - self.cx), -2.0 * (y - self.cy)
        return {'x': round((gx * S + gz * S) * K, 4), 'y': round((-gx * S + gz * S) * K, 4)}

    def poly(self, pts):
        return [self.pt(p[0], p[1]) for p in pts]

    def angle(self, deg):
        """Hades angle (0 = screen right, CCW on screen) -> world angle in degrees (atan2 over world x, z)."""
        a = math.radians(deg)
        dx, dy = math.cos(a), -math.sin(a)        # screen y is down
        gx, gz = dx, -2.0 * dy
        wx, wz = gx * S + gz * S, -gx * S + gz * S
        return round(math.degrees(math.atan2(wz, wx)), 3)


def radius_of(pts, x, y):
    if not pts:
        return 0.3
    gs = [((p[0] - x), -2.0 * (p[1] - y)) for p in pts]
    return max(math.hypot(a, b) for a, b in gs) * K


def wall_height(o):
    t = o.get('tallnessUnscaled') or 0
    if o.get('invisible') or t > 1e6:
        return 0.0
    h = t * (o.get('scaleY') or 1.0) * K / COS30
    return round(min(3.5, max(0.35, h)), 3)


def num(v, d=0.0):
    return float(v) if isinstance(v, (int, float)) else d


def trap_params(tt):
    """Base trap parameters from trapTypes: timings, damage, range, speed, radius."""
    p = {'pre': num(tt.get('PreAttackDuration')), 'fire': num(tt.get('FireDuration')),
         'post': num(tt.get('PostAttackDuration')), 'cooldown': num(tt.get('PostAttackCooldown')),
         'cooldownMin': num(tt.get('PostAttackCooldownMin')), 'cooldownMax': num(tt.get('PostAttackCooldownMax')),
         'wakeMin': num(tt.get('WakeUpDelayMin')), 'wakeMax': num(tt.get('WakeUpDelayMax')),
         'trigger': num(tt.get('AIAttackDistance')) * K, 'enemyMul': num(tt.get('damageMultiplierVsEnemies'), 1.0),
         'fuse': num(tt.get('FuseDuration')), 'dormant': num(tt.get('FuseDormantDuration')),
         'damage': 0.0, 'range': 0.0, 'speed': 0.0, 'radius': 0.0, 'knock': 0.0, 'barrel': 0.0}
    for w in tt.get('weapons') or []:
        pr = w.get('projectile') or {}
        p['damage'] = num(pr.get('DamageLow'), p['damage'])
        p['range'] = num(pr.get('Range')) * K
        p['speed'] = num(pr.get('Speed')) * K
        p['radius'] = num(pr.get('DamageRadius')) * K
        p['knock'] = num(pr.get('ImpactVelocity')) * K
        p['barrel'] = num(w.get('BarrelLength')) * K
        break
    return p


def point_in_poly(pt, poly):
    x, y = pt
    inside = False
    j = len(poly) - 1
    for i in range(len(poly)):
        xi, yi = poly[i]; xj, yj = poly[j]
        if (yi > y) != (yj > y) and x < (xj - xi) * (y - yi) / (yj - yi) + xi:
            inside = not inside
        j = i
    return inside


def lava_fraction(hole, lava):
    """Доля дыры в контуре пола, занятая лавой: такие дыры — не стены, по магме в Hades можно ходить."""
    if not lava:
        return 0.0
    xs = [p[0] for p in hole]; ys = [p[1] for p in hole]
    n = inside = hit = 0
    for i in range(12):
        for k in range(12):
            pt = (min(xs) + (max(xs) - min(xs)) * (i + 0.5) / 12, min(ys) + (max(ys) - min(ys)) * (k + 0.5) / 12)
            if not point_in_poly(pt, hole):
                continue
            inside += 1
            if any(point_in_poly(pt, l) for l in lava):
                hit += 1
    return hit / inside if inside else 0.0


def convert(src, name, enabled_intro):
    xf = Xf(src['bounds'])
    hs = src['heroStart']
    out = {
        'name': name, 'biome': src['biome'], 'source': 'Hades ' + name,
        'zoom': num(src['zoom'].get('ZoomFraction'), 1.0),
        'heroStart': xf.pt(hs['x'], hs['y']), 'heroAngle': xf.angle(hs.get('angle', 0)),
        'floor': [], 'walls': [], 'hazards': [], 'spawns': [], 'exits': [], 'traps': [], 'breakables': [], 'decor': [],
    }
    # магма вокруг комнат тянется до края карты; берём только ту, что внутри рамки пола комнаты (+300 ед.)
    wx = [p[0] for w in src['walkable'] for p in w['outer']]
    wy = [p[1] for w in src['walkable'] for p in w['outer']]
    box = (min(wx) - 300, min(wy) - 150, max(wx) + 300, max(wy) + 150)

    def in_box(poly):
        cx = sum(p[0] for p in poly) / len(poly); cy = sum(p[1] for p in poly) / len(poly)
        return box[0] <= cx <= box[2] and box[1] <= cy <= box[3]

    lava = [h['polygon'] for h in src['hazards'] if h.get('polygon') and h['type'] == 'lava' and in_box(h['polygon'])]
    for w in src['walkable']:
        out['floor'].append({'p': xf.poly(w['outer']), 'type': 'Floor', 'h': 0})
        for h in w.get('holes') or []:
            if lava_fraction(h, lava) >= 0.5:
                continue
            out['walls'].append({'p': xf.poly(h), 'type': 'Hole', 'h': 0})
    # магма проходима (урон каждые 0.19 с): её треугольники — тоже пол, острова Асфоделя связаны через неё
    for l in lava:
        out['floor'].append({'p': xf.poly(l), 'type': 'Lava', 'h': 0})
    for o in src['obstacles']:
        if not (o.get('solid') and o.get('blocksUnits') and o.get('active', True)) or not o.get('polygon'):
            continue
        out['walls'].append({'p': xf.poly(o['polygon']), 'type': o['type'], 'h': wall_height(o)})
    for h in src['hazards']:
        if h.get('polygon') and in_box(h['polygon']):
            out['hazards'].append({'type': 'Lava' if h['type'] == 'lava' else h['type'], 'p': xf.poly(h['polygon']), 'damage': round(LAVA_PER_TICK, 3)})
    for sp in src['spawnPoints']:
        out['spawns'].append({'pos': xf.pt(sp['x'], sp['y']), 'angle': 0, 'role': sp.get('role') or ''})
    for e in src['exits']:
        out['exits'].append({'pos': xf.pt(e['x'], e['y']), 'angle': xf.angle(e.get('angle', 0))})
    ids = {t['id']: i for i, t in enumerate(src['traps'])}
    types = src.get('trapTypes', {})
    for t in src['traps']:
        tt = types.get(t['type'], {})
        link = ids.get(t.get('firesEmitterId'), -1)
        chain = [ids[c] for ch in (t.get('triggersChains') or []) for c in ch if c in ids]
        if t['type'] == 'DartTrap' and link < 0:
            # пластина стреляет ближайшим излучателем в пределах 2000
            best, bd = -1, 2000.0
            for i, e in enumerate(src['traps']):
                if e['type'] == 'DartTrapEmitter':
                    d = math.hypot(e['x'] - t['x'], (e['y'] - t['y']) * 2)
                    if d < bd:
                        best, bd = i, d
            link = best
        if t['type'] == 'DartTrap' and link >= 0:
            tt = dict(tt, weapons=types.get('DartTrapEmitter', {}).get('weapons'))
        if t['type'] == 'AxeTrapTrigger' and not chain:
            chain = [i for i, e in enumerate(src['traps']) if e['type'] == 'AxeTrap']
        out['traps'].append({
            'type': t['type'], 'pos': xf.pt(t['x'], t['y']), 'angle': xf.angle(t.get('angle', 0)),
            'p': xf.poly(t['polygon']) if t.get('polygon') else [], 'link': link, 'chain': chain,
            'solid': bool(t.get('blocksMovement')), 'scale': num(t.get('scale'), 1.0),
            'param': trap_params(tt),
        })
    for b in src['breakables']:
        out['breakables'].append({'type': b['type'], 'pos': xf.pt(b['x'], b['y']), 'r': round(radius_of(b.get('polygon'), b['x'], b['y']), 3), 'h': 0.6})
    # границы в мире
    xs, ys = [], []
    for f in out['floor']:
        for p in f['p']:
            xs.append(p['x']); ys.append(p['y'])
    out['min'] = {'x': min(xs), 'y': min(ys)}
    out['max'] = {'x': max(xs), 'y': max(ys)}
    enc = None
    if enabled_intro:
        enc = INTRO
    else:
        le = (src.get('roomData') or {}).get('legalEncounters') or []
        enc = {'name': le[0]['name'] if le else 'Generated', 'activeCap': 8, 'generated': True, 'waves': []}
    out['encounter'] = enc
    return out


def main(argv):
    src_dir, out_dir = argv[1], argv[2]
    enable, intro = [], []
    rest = argv[3:]
    i = 0
    while i < len(rest):
        if rest[i] == '--enable':
            enable.append(rest[i + 1]); i += 2
        elif rest[i] == '--intro':
            intro.append(rest[i + 1]); i += 2
        else:
            i += 1
    with open(os.path.join(src_dir, 'index.json')) as f:
        index = json.load(f)
    names = [r['name'] for r in index['rooms']]
    os.makedirs(out_dir, exist_ok=True)
    for n in names:
        with open(os.path.join(src_dir, n + '.json')) as f:
            src = json.load(f)
        room = convert(src, n, n in intro)
        with open(os.path.join(out_dir, n + '.json'), 'w') as f:
            json.dump(room, f, separators=(',', ':'))
        size = (room['max']['x'] - room['min']['x'], room['max']['y'] - room['min']['y'])
        print(f"{n:14s} {room['biome']:9s} floor {len(room['floor'])} walls {len(room['walls']):4d} traps {len(room['traps']):3d} "
              f"spawns {len(room['spawns']):3d} size {size[0]:.1f}x{size[1]:.1f}")
    with open(os.path.join(out_dir, 'index.json'), 'w') as f:
        json.dump({'rooms': names, 'enabled': enable}, f, indent=1)


if __name__ == '__main__':
    main(sys.argv)
