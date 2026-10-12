#!/usr/bin/env python3
"""Validation of the thing_bin reverse engineering + geometry conventions.

Usage: python3 -I validate.py <HadesContentDir>

Checks:
 1. every SGB1 map parses exactly to EOF; thing ids unique per map
 2. group (id,name) pairs in thing_bin agree with the map_text ThingGroups
 3. collision-polygon conventions: counts how many logic points (spawn points, HeroEnd, loot points,
    breakables) fall inside solid obstacle polygons under competing hypotheses
    (scale source, which byte is FlipHorizontal, rotation direction). Lower = better.
"""
import collections
import glob
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import geom  # noqa: E402
import parse_thing_bin as P  # noqa: E402
import typedb  # noqa: E402

C = sys.argv[1]
maps = sorted(glob.glob(os.path.join(C, 'macOS', 'Maps', '*.thing_bin')))
parsed = {}
fails = []
for f in maps:
    n = os.path.basename(f)[:-10]
    try:
        parsed[n] = P.parse_file(f)
    except Exception as e:  # noqa: BLE001
        fails.append((n, str(e)))
print('1. parsed to EOF: %d / %d maps; failures: %s' % (len(parsed), len(maps), fails))
dup = [n for n, r in parsed.items() if len({t['id'] for t in r['things']}) != len(r['things'])]
print('   maps with duplicate thing ids:', dup or 'none')

mismatch = 0
checked = 0
for n, r in parsed.items():
    mt = os.path.join(C, 'Maps', n + '.map_text')
    if not os.path.exists(mt):
        continue
    groups = {}
    for g in json.load(open(mt)).get('ThingGroups', []):
        groups.setdefault(g['Id'].get('Name'), set()).add(g['Id'].get('Id', 0))
    for t in r['things']:
        for gid, gname in zip(t['groupIds'], t['groups']):
            checked += 1
            if gname not in groups or gid not in groups[gname]:
                mismatch += 1
print('2. group refs checked: %d, not matching map_text ThingGroups: %d' % (checked, mismatch))

T = typedb.load_types(C)
PTS = {'EnemyPoint', 'EnemyPointMelee', 'EnemyPointRanged', 'EnemyPointSupport', 'HeroEnd', 'LootPoint',
       'Breakable', 'BreakableAsphodel', 'BreakableElysium', 'BreakableStyx'}
rooms = [r['things'] for n, r in parsed.items() if n[:2] in ('A_', 'B_', 'C_', 'D_')]


def count(scale_mode, flip_mode, rot_sign, use_tri=True):
    bad = 0
    for things in rooms:
        pts = [(t['x'], t['y']) for t in things if t['name'] in PTS]
        for t in things:
            d = T.get(t['name'], {})
            th = d.get('Thing', {})
            if t['dataType'] != 'Obstacle' or not th.get('StopsUnits', True) or not (t['flags'] & 0x10000):
                continue
            if use_tri and t['triStateA'] == 1:
                continue
            if (d.get('Life') or {}).get('DieOnCollision'):
                continue
            p0 = [(p['X'], p['Y']) for p in th.get('Points', []) if isinstance(p, dict)]
            if len(p0) < 3:
                continue
            s = {'instance': t['scale'], 'instance*typeScale': t['scale'] * float(th.get('Scale', 1.0)),
                 'none': 1.0}[scale_mode]
            fh = {'byteA': t['flipHorizontal'], 'byteB': t['flipVertical'], 'none': False}[flip_mode]
            poly = geom.transform_points(p0, t['x'], t['y'], s, rot_sign * t['angle'], fh, False, True)
            xs = [p[0] for p in poly]
            ys = [p[1] for p in poly]
            for (x, y) in pts:
                if min(xs) <= x <= max(xs) and min(ys) <= y <= max(ys) and geom.point_in_poly(x, y, poly):
                    bad += 1
    return bad


print('3. logic points inside solid polygons (lower = better):')
for args in [('none', 'byteA', 1), ('instance', 'byteA', 1), ('instance*typeScale', 'byteA', 1),
             ('instance*typeScale', 'byteB', 1), ('instance*typeScale', 'none', 1),
             ('instance*typeScale', 'byteA', -1)]:
    print('   scale=%-20s flipH=%-5s rotSign=%+d -> %d' % (args + (count(*args),)))
print('   (triStateA=1 treated as collision OFF; ignoring it ->', count('instance*typeScale', 'byteA', 1, False), ')')
