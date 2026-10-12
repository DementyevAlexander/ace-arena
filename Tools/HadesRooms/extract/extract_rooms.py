#!/usr/bin/env python3
"""Extract gameplay geometry for Hades rooms into JSON (+ PNG previews).

Usage:
  python3 -I extract_rooms.py <HadesContentDir> <outDir> [RoomName ...]
  python3 -I extract_rooms.py <HadesContentDir> <outDir> --survey     # stats for all combat rooms

Only geometry/gameplay metadata is extracted; no art, audio or code.
All coordinates are in ORIGINAL Hades map space (see FORMAT.md).
"""
import copy
import glob
import json
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import geom  # noqa: E402
import luadata  # noqa: E402
import parse_thing_bin  # noqa: E402
import png  # noqa: E402
import sjson  # noqa: E402
import typedb  # noqa: E402

COORD_NOTE = (
    "Coordinates are original Hades map space (screen-plane / isometric-projected ground). "
    "x right, y DOWN (towards camera). Ground is squashed 2:1 vertically: a circle on the floor is an "
    "ellipse with ry = rx/2 (e.g. iso tile _Impassable_Tile64Iso = diamond 128x64). "
    "To get a true top-down ground plane: groundX = x, groundZ = -2*y (Unity: X=x, Z=-2y; an orthographic "
    "camera pitched 30 deg reproduces the Hades view). Optional: rotate 45 deg (u=(x+2y)/sqrt2, v=(2y-x)/sqrt2) "
    "to make the iso walls axis-aligned. Angles are degrees, 0 = +x (screen right), counter-clockwise on screen "
    "(90 = screen up / away from camera); ground direction = normalize(cos a, 2*sin a) in (groundX, groundZ). "
    "Rooms may be mirrored horizontally at runtime (FlipHorizontalChance / EntranceDirection)."
)

BIOME = {'A': 'Tartarus', 'B': 'Asphodel', 'C': 'Elysium', 'D': 'Styx'}

TRAP_TYPES = {
    'SpikeTrap', 'DartTrap', 'DartTrapEmitter', 'AxeTrap', 'AxeTrapTrigger', 'GasTrapPassive', 'PoisonTrap',
    'PhalanxTrap', 'PhalanxTrapPassive', 'SawTrap', 'ArcherTrap', 'BlastCubeFused', 'BlastCubeFusedRegenerating',
    'TrapPadDisabled', 'TrapFissureDisabled', 'SpikeCube', 'ReflectiveMirror',
}
HAZARD_TYPES = {'LavaTile', 'LavaTileTriangle01', 'LavaTileTriangle02'}
SPAWN_TYPES = {'EnemyPoint', 'EnemyPointMelee', 'EnemyPointRanged', 'EnemyPointSupport'}

TRAP_AI_FIELDS = (
    'PreAttackDuration', 'FireDuration', 'PostAttackDuration', 'PostAttackCooldown', 'PostAttackCooldownMin',
    'PostAttackCooldownMax', 'AIAttackDistance', 'AIResetDistance', 'AttackDistanceScaleY', 'WakeUpDelayMin',
    'WakeUpDelayMax', 'FuseDuration', 'FuseDormantDuration', 'PostFuseRevive', 'ToggleTrap', 'GroupTrigger',
    'TriggerDistance', 'SearchDistanceOffset', 'LinkedEnemy', 'MaxHealth', 'AddUnitCollisionDuringAttack',
    'TargetOffsetForward', 'PointOnlyCollision', 'SkipAngleTowardTarget',
)
PROJ_FIELDS = (
    'Type', 'DamageLow', 'DamageHigh', 'Damage', 'DamagePerConsecutiveHit', 'ConsecutiveHitWindow', 'DamageRadius',
    'DamageRadiusScaleX', 'DamageRadiusScaleY', 'Range', 'Speed', 'Fuse', 'ImpactVelocity', 'ImmunityDuration',
    'BlastSpeed', 'MoveInterval', 'UnitCollisionCheck', 'CheckObstacleImpact',
)
WEAPON_FIELDS = ('Type', 'Cooldown', 'ChargeTime', 'ClipSize', 'ReloadTime', 'BarrelLength', 'NumProjectiles',
                 'FireInterval', 'FullyAutomatic')


def rnd(v, n=1):
    return round(v, n) if isinstance(v, float) else v


# ------------------------------------------------------------------ data loading
class GameData:
    def __init__(self, content):
        self.content = content
        self.types = typedb.load_types(content)
        S = os.path.join(content, 'Scripts')
        self.rooms = {}
        for f in sorted(glob.glob(os.path.join(S, 'RoomData*.lua'))):
            for k, v in luadata.load(f).items():
                if k.startswith('RoomSetData.') and isinstance(v, dict):
                    self.rooms.update(v)
        self.enemy_lua = luadata.load(os.path.join(S, 'EnemyData.lua')).get('UnitSetData.Enemies', {})
        self.obstacle_lua = luadata.load(os.path.join(S, 'ObstacleData.lua')).get('ObstacleData', {})
        self.encounters = luadata.load(os.path.join(S, 'EncounterData.lua')).get('EncounterData', {})
        self.enemy_sets = luadata.load(os.path.join(S, 'EnemySets.lua')).get('EnemySets', {})
        self.encounter_sets = luadata.load(os.path.join(S, 'EncounterSets.lua')).get('EncounterSets', {})
        self.weapon_lua = luadata.load(os.path.join(S, 'WeaponData.lua')).get('WeaponData', {})
        self.weapons = self._load_sjson_named('Weapons', 'Weapons')
        self.projectiles = self._load_sjson_named('Projectiles', 'Projectiles')
        self.door_types = {n for n in self.obstacle_lua if self._lua_inherits(self.obstacle_lua, n, 'ExitDoor')}

    def _load_sjson_named(self, sub, key):
        raw = {}
        for f in glob.glob(os.path.join(self.content, 'Game', sub, '*.sjson')):
            for e in sjson.load(f).get(key, []):
                if isinstance(e, dict) and 'Name' in e:
                    raw[e['Name']] = e
        out = {}

        def res(n, d=0):
            if n in out:
                return out[n]
            e = raw.get(n)
            if e is None or d > 20:
                return {}
            p = e.get('InheritFrom')
            r = typedb._merge(res(p, d + 1), e) if p else copy.deepcopy(e)
            out[n] = r
            return r
        for n in raw:
            res(n)
        return out

    @staticmethod
    def _lua_inherits(table, name, base, d=0):
        if name == base:
            return True
        e = table.get(name)
        if not isinstance(e, dict) or d > 20:
            return False
        parents = e.get('InheritFrom') or []
        if isinstance(parents, str):
            parents = [parents]
        return any(GameData._lua_inherits(table, p, base, d + 1) for p in parents if isinstance(p, str))

    def resolve_lua(self, table, name, d=0):
        e = table.get(name)
        if not isinstance(e, dict) or d > 20:
            return {}
        parents = e.get('InheritFrom') or []
        if isinstance(parents, str):
            parents = [parents]
        out = {}
        for p in reversed([p for p in parents if isinstance(p, str)]):  # earlier parent wins
            out.update(self.resolve_lua(table, p, d + 1))
        out.update(e)
        return out

    def ref(self, v):
        """Resolve '@EnemySets.X' / '@EncounterSets.X' references."""
        if isinstance(v, str) and v.startswith('@'):
            parts = v[1:].split('.')
            src = {'EnemySets': self.enemy_sets, 'EncounterSets': self.encounter_sets}.get(parts[0])
            if src is not None and len(parts) == 2:
                return src.get(parts[1], v)
        return v

    # ---- traps
    def trap_params(self, name):
        e = self.resolve_lua(self.enemy_lua, name)
        p = {}
        ai = dict(e)
        if isinstance(e.get('DefaultAIData'), dict):
            ai.update(e['DefaultAIData'])
        for k in TRAP_AI_FIELDS:
            if k in ai and not isinstance(ai[k], (dict, list)):
                p[k] = ai[k]
        mods = e.get('OutgoingDamageModifiers')
        if isinstance(mods, list):
            for m in mods:
                if isinstance(m, dict) and 'NonPlayerMultiplier' in m:
                    p['damageMultiplierVsEnemies'] = m['NonPlayerMultiplier']
        weapons = e.get('WeaponOptions') or []
        if isinstance(weapons, str):
            weapons = [weapons]
        # also death/fuse weapons from unit sjson / lua
        ut = self.types.get(name, {})
        extra = [e.get('FuseWarningWeapon'), e.get('PostFuseWeapon'), ut.get('OnDeathWeapon')]
        wl = []
        for wn in list(weapons) + [x for x in extra if isinstance(x, str) and x not in ('null', '')]:
            if not isinstance(wn, str) or wn in [w.get('name') for w in wl]:
                continue
            w = self.weapons.get(wn, {})
            entry = {'name': wn}
            for k in WEAPON_FIELDS:
                if k in w:
                    entry[k] = w[k]
            wlua = self.resolve_lua(self.weapon_lua, wn)
            aid = wlua.get('AIData') if isinstance(wlua.get('AIData'), dict) else {}
            for k in ('AttackDistance', 'PreAttackDuration', 'FireDuration', 'PostAttackCooldown', 'PostAttackDuration'):
                if k in aid and not isinstance(aid[k], (dict, list)):
                    entry['ai' + k] = aid[k]
            pn = w.get('Projectile', wn)
            pr = self.projectiles.get(pn, {})
            if pr:
                entry['projectile'] = {k: pr[k] for k in PROJ_FIELDS if k in pr}
            wl.append(entry)
        if wl:
            p['weapons'] = wl
        return p

    # ---- rooms
    def room_data(self, name, d=0):
        return self.resolve_lua(self.rooms, name)

    def encounter_info(self, enc_name):
        e = self.resolve_lua(self.encounters, enc_name)
        info = {'name': enc_name}
        for k in ('MinWaves', 'MaxWaves', 'MinTypes', 'MaxTypes', 'ActiveEnemyCapBase', 'ActiveEnemyCapMax',
                  'BaseDifficulty', 'SpawnHazards', 'EncounterType', 'DifficultyModifier'):
            if k in e and not isinstance(e[k], (dict, list)):
                info[k] = e[k]
        es = self.ref(e.get('EnemySet'))
        if isinstance(es, list):
            info['enemySet'] = es
        spawns = []
        waves = e.get('SpawnWaves')
        if isinstance(waves, list):
            info['scriptedWaves'] = len(waves)
            for wv in waves:
                if isinstance(wv, dict) and isinstance(wv.get('Spawns'), list):
                    for s in wv['Spawns']:
                        if isinstance(s, dict) and isinstance(s.get('Name'), str) and s['Name'] not in spawns:
                            spawns.append(s['Name'])
        if spawns:
            info['scriptedEnemies'] = spawns
        return info


# ------------------------------------------------------------------ per-thing helpers
def type_points(gd, t):
    if t['points']:
        return [tuple(p) for p in t['points']], 'instance'
    th = gd.types.get(t['name'], {}).get('Thing', {})
    pts = th.get('Points') or []
    out = []
    for p in pts:
        if isinstance(p, dict) and 'X' in p and 'Y' in p:
            out.append((float(p['X']), float(p['Y'])))
    return out, 'type'


def eff_scale(gd, t):
    """Instance 'scale' is a multiplier on the type's Thing.Scale (validated: see FORMAT.md)."""
    ds = gd.types.get(t['name'], {}).get('Thing', {}).get('Scale', 1.0)
    try:
        ds = float(ds)
    except (TypeError, ValueError):
        ds = 1.0
    return t['scale'] * ds


def world_poly(gd, t):
    pts, src = type_points(gd, t)
    s = eff_scale(gd, t)
    if len(pts) < 3:
        return None
    xs = [p[0] for p in pts]
    ys = [p[1] for p in pts]
    if (max(xs) - min(xs)) * s < 8 and (max(ys) - min(ys)) * s < 8:
        return None  # base-class placeholder triangle
    # a closing duplicate vertex is common in the sjson; drop it
    if len(pts) > 3 and pts[0] == pts[-1]:
        pts = pts[:-1]
    return geom.transform_points(pts, t['x'], t['y'], s, t['angle'],
                                 t['flipHorizontal'], t['flipVertical'], True)


def thing_props(gd, t):
    d = gd.types.get(t['name'], {})
    th = d.get('Thing', {})
    active = bool(t['flags'] & 0x10000)
    collision_override = {0: None, 1: False, 2: True}.get(t['triStateA'])
    stops_units = th.get('StopsUnits', True)
    stops_proj = th.get('StopsProjectiles', True)
    if collision_override is False:
        stops_units = stops_proj = False
    return {
        'def': d, 'thing': th, 'active': active, 'collisionOverride': collision_override,
        'stopsUnits': bool(stops_units), 'stopsProjectiles': bool(stops_proj),
        'invisible': bool(th.get('Invisible', False)),
        'tallness': t['tallness'] or th.get('Tallness'),
        'vulnerable': not d.get('Life', {}).get('Invulnerable', False) if d.get('Life') else False,
        'inherit': d.get('InheritFrom'),
    }


# ------------------------------------------------------------------ room extraction
def extract_room(gd, room, out_dir, png_dir=None, cell=16.0):
    path = os.path.join(gd.content, 'macOS', 'Maps', room + '.thing_bin')
    parsed = parse_thing_bin.parse_file(path)
    things = parsed['things']
    rd = gd.room_data(room)

    res = {
        'name': room,
        'biome': BIOME.get(room[0], 'Other'),
        'coordinateNote': COORD_NOTE,
        'source': {'thingCount': parsed['count'], 'thingBinVersion': parsed['version']},
    }
    hero_end = hero_start = None
    spawn_points, exits, hero_exits, loot, traps, breakables, hazards, obstacles = [], [], [], [], [], [], [], []
    camera_clamps, secret_points = [], []
    seeds = []

    for t in things:
        n = t['name']
        pr = thing_props(gd, t)
        base = {'id': t['id'], 'type': n, 'x': t['x'], 'y': t['y']}
        if n == 'HeroEnd':
            hero_end = dict(base, angle=t['angle'])
        elif n == 'HeroStart':
            hero_start = dict(base, angle=t['angle'])
        elif n in SPAWN_TYPES:
            if not pr['active']:
                continue
            role = {'EnemyPointMelee': 'melee', 'EnemyPointRanged': 'ranged',
                    'EnemyPointSupport': 'support'}.get(n, 'any')
            spawn_points.append({'id': t['id'], 'x': t['x'], 'y': t['y'], 'role': role, 'groups': t['groups']})
        elif n == 'HeroExit' or n == 'BoatMovePoint':
            hero_exits.append(dict(base, angle=t['angle']))
        elif n == 'LootPoint':
            loot.append({'id': t['id'], 'x': t['x'], 'y': t['y']})
        elif n == 'CameraClamp':
            camera_clamps.append([t['x'], t['y']])
        elif n == 'SecretPoint':
            secret_points.append({'id': t['id'], 'x': t['x'], 'y': t['y']})
        elif n in gd.door_types:
            poly = world_poly(gd, t)
            exits.append(dict(base, angle=t['angle'], scale=round(eff_scale(gd, t), 4), flipped=t['flipHorizontal'],
                              polygon=poly, active=pr['active']))
        elif n in HAZARD_TYPES:
            poly = world_poly(gd, t)
            if poly:
                hazards.append({'id': t['id'], 'type': 'lava', 'thingType': n, 'polygon': poly,
                                'active': pr['active'], 'params': {'weapon': 'LavaTileWeapon'}})
        elif n in TRAP_TYPES:
            poly = world_poly(gd, t)
            tr = dict(base, angle=t['angle'], scale=round(eff_scale(gd, t), 4), active=pr['active'],
                      blocksMovement=pr['stopsUnits'], polygon=poly, groups=t['groups'])
            if n in ('TrapPadDisabled', 'TrapFissureDisabled'):
                opts = gd.ref(rd.get('TrapOptions'))
                tr['note'] = 'placeholder swapped at runtime by RandomizeTrapTypes into one of TrapOptions'
                tr['runtimeOptions'] = opts if isinstance(opts, list) else None
            if t['attachedIds']:
                tr['attachedIds'] = t['attachedIds']
            traps.append(tr)
        elif n.startswith('Breakable'):
            poly = world_poly(gd, t)
            breakables.append(dict(base, scale=round(eff_scale(gd, t), 4), polygon=poly, active=pr['active']))
        elif t['dataType'] == 'Unit':
            continue  # player unit placeholder / bosses placed in map
        else:
            if not pr['stopsUnits'] and not pr['stopsProjectiles']:
                continue
            if pr['def'].get('Name') is None:
                continue
            poly = world_poly(gd, t)
            if not poly:
                continue
            raised = (t['offsetZ'] or 0) >= 200
            kind = 'impassable' if n.startswith('_Impassable') else 'obstacle'
            o = {
                'id': t['id'], 'type': n, 'kind': kind, 'x': t['x'], 'y': t['y'], 'angle': t['angle'],
                'scaleX': round(eff_scale(gd, t) * (-1 if t['flipHorizontal'] else 1), 4),
                'scaleY': round(eff_scale(gd, t) * (-1 if t['flipVertical'] else 1), 4),
                'instanceScale': t['scale'],
                'polygon': poly,
                'solid': pr['stopsUnits'] and pr['active'] and not raised,
                'blocksUnits': pr['stopsUnits'], 'blocksProjectiles': pr['stopsProjectiles'],
                'active': pr['active'],
            }
            if pr['invisible']:
                o['invisible'] = True
            if raised:
                o['offsetZ'] = t['offsetZ']
            if pr['tallness'] is not None:
                o['tallnessUnscaled'] = rnd(float(pr['tallness']))
            life = pr['def'].get('Life', {}) or {}
            if life.get('DieOnCollision'):
                o['destructible'] = True
                o['breaksOnContactVelocity'] = life.get('DieOnCollisionRequiredVelocity', 0)
                if o['breaksOnContactVelocity'] < 500:
                    o['solid'] = False  # crumbles when anything walks into it (webs, ghosts, small stalagmites)
            elif pr['vulnerable']:
                o['destructible'] = True  # blocks until destroyed by damage
            obstacles.append(o)

    # ---------------- walkable area (rasterize in iso-aligned ground frame)
    solids = [o['polygon'] for o in obstacles if o['solid']]
    solids += [e['polygon'] for e in exits if e['polygon']]
    solids += [h['polygon'] for h in hazards if h['active']]  # lava = damaging floor; excluded from safe walkable
    all_pts = [p for poly in solids for p in poly]
    seeds_xy = []
    if hero_end:
        seeds_xy.append((hero_end['x'], hero_end['y']))
    seeds_xy += [(s['x'], s['y']) for s in spawn_points]
    seeds_xy += [(l['x'], l['y']) for l in loot]
    walk = None
    leaks = None
    if all_pts and seeds_xy:
        uvs = [geom.to_uv(x, y) for x, y in all_pts + seeds_xy]
        us = [p[0] for p in uvs]
        vs = [p[1] for p in uvs]
        g = geom.Grid(min(us) - 4 * cell, min(vs) - 4 * cell, max(us) + 4 * cell, max(vs) + 4 * cell, cell)
        for poly in solids:
            puv = [geom.to_uv(x, y) for x, y in poly]
            g.fill_poly_uv(puv)
            g.mark_poly_edges(puv)
        reach, leaks = g.flood([geom.to_uv(x, y) for x, y in seeds_xy])
        loops = geom.trace_regions(reach, g.w, g.h)
        regions = []
        for lp in loops:
            pts = [geom.from_uv(g.u0 + i * cell, g.v0 + j * cell) for i, j in lp]
            # simplify in uv (axis-aligned) then convert
            uvp = [(g.u0 + i * cell, g.v0 + j * cell) for i, j in lp]
            uvs_s = geom.simplify_closed(uvp, cell * 0.75)
            pts = [[round(a, 1), round(b, 1)] for a, b in (geom.from_uv(u, v) for u, v in uvs_s)]
            ar = geom.poly_area(pts)
            if abs(ar) < cell * cell:
                continue
            regions.append((ar, pts))
        # (i,j)->(u,v)->(x,y) has positive Jacobian (0.5): outer loops keep positive shoelace area, holes negative
        outers = [p for a, p in regions if a > 0]
        holes = [p for a, p in regions if a < 0]
        # assign holes to outers
        wl = []
        for op in sorted(outers, key=lambda p: -abs(geom.poly_area(p))):
            hs = [h for h in holes if geom.point_in_poly(h[0][0], h[0][1], op)]
            wl.append({'outer': op, 'holes': hs, 'area': round(abs(geom.poly_area(op)) - sum(abs(geom.poly_area(h)) for h in hs))})
        walk = wl
        cells = sum(reach)
        res['walkableStats'] = {'cellSize_uv': cell, 'reachableCells': cells,
                                'areaMapUnits2': round(cells * cell * cell / 2.0),
                                'leaksToGridBorder': bool(leaks)}

        # relevance: keep only collision things that touch (or lie within ~3 cells of) the reachable floor
        near = bytearray(reach)
        R = 3
        w_, h_ = g.w, g.h
        for _ in range(R):
            nn = bytearray(near)
            for k in range(w_ * h_):
                if near[k]:
                    i, j = k % w_, k // w_
                    if i > 0:
                        nn[k - 1] = 1
                    if i < w_ - 1:
                        nn[k + 1] = 1
                    if j > 0:
                        nn[k - w_] = 1
                    if j < h_ - 1:
                        nn[k + w_] = 1
            near = nn

        def relevant(poly):
            if not poly:
                return False
            n = len(poly)
            for a in range(n):
                x1, y1 = poly[a]
                x2, y2 = poly[(a + 1) % n]
                L = math.hypot(x2 - x1, y2 - y1)
                m = max(1, int(L / (cell * 0.5)))
                for s_ in range(m + 1):
                    tt = s_ / m
                    u, v = geom.to_uv(x1 + (x2 - x1) * tt, y1 + (y2 - y1) * tt)
                    i = int((u - g.u0) / cell)
                    j = int((v - g.v0) / cell)
                    if 0 <= i < w_ and 0 <= j < h_ and near[j * w_ + i]:
                        return True
            cx = sum(p[0] for p in poly) / n
            cy = sum(p[1] for p in poly) / n
            u, v = geom.to_uv(cx, cy)
            i = int((u - g.u0) / cell)
            j = int((v - g.v0) / cell)
            return 0 <= i < w_ and 0 <= j < h_ and bool(near[j * w_ + i])

        before = len(obstacles)
        obstacles[:] = [o for o in obstacles if relevant(o['polygon'])]
        hazards[:] = [h for h in hazards if relevant(h['polygon'])]
        res['walkableStats']['droppedFarObstacles'] = before - len(obstacles)

    # ---------------- bounds
    bpts = []
    if walk:
        for r in walk:
            bpts += r['outer']
    else:
        bpts = [(s['x'], s['y']) for s in spawn_points]
    if bpts:
        xs = [p[0] for p in bpts]
        ys = [p[1] for p in bpts]
        res['bounds'] = {'minX': round(min(xs), 1), 'minY': round(min(ys), 1),
                         'maxX': round(max(xs), 1), 'maxY': round(max(ys), 1)}
        res['size'] = {'width': round(max(xs) - min(xs)), 'height': round(max(ys) - min(ys)),
                       'groundDepth': round(2 * (max(ys) - min(ys)))}

    # ---------------- room metadata
    zoom = rd.get('ZoomFraction')
    res['zoom'] = {'ZoomFraction': zoom if zoom is not None else 1.0,
                   'ZoomFractionSwitch': rd.get('ZoomFractionSwitch'),
                   'note': 'Hades camera zoom multiplier (1.0 = default, <1 = zoomed out to fit bigger room)'}
    enc = gd.ref(rd.get('LegalEncounters'))
    encs = []
    if isinstance(enc, list):
        for en in dict.fromkeys(e for e in enc if isinstance(e, str)):
            encs.append(gd.encounter_info(en))
    res['roomData'] = {
        'inheritFrom': rd.get('InheritFrom') if 'InheritFrom' in gd.rooms.get(room, {}) else None,
        'entranceDirection': rd.get('EntranceDirection'),
        'numExits': rd.get('NumExits'),
        'flipHorizontalChance': rd.get('FlipHorizontalChance'),
        'isMiniBossRoom': bool(rd.get('IsMiniBossRoom')),
        'debugOnly': bool(gd.rooms.get(room, {}).get('DebugOnly')),
        'remoteTrapChains': rd.get('RemoteTrapChains'),
        'trapOptions': gd.ref(rd.get('TrapOptions')),
        'legalEncounters': encs,
    }
    res['roomData'] = {k: v for k, v in res['roomData'].items() if v is not None}

    he = hero_end or hero_start
    res['heroStart'] = {'x': he['x'], 'y': he['y'], 'angle': he['angle'], 'id': he['id'],
                        'note': 'HeroEnd = where the hero lands inside the room after the entrance run'} if he else None
    if hero_start:
        res['heroEntrance'] = {'x': hero_start['x'], 'y': hero_start['y'], 'angle': hero_start['angle'],
                               'note': 'HeroStart = entrance point outside the playable area (behind entry door)'}
    # pair HeroExit points with doors
    for e in exits:
        best = None
        for h in hero_exits:
            d = math.hypot(h['x'] - e['x'], h['y'] - e['y'])
            if best is None or d < best[0]:
                best = (d, h)
        if best and best[0] < 900:
            e['heroExitPoint'] = {'x': best[1]['x'], 'y': best[1]['y']}
    res['walkable'] = walk
    res['obstacles'] = obstacles
    res['spawnPoints'] = spawn_points
    res['lootPoints'] = loot
    res['exits'] = exits
    # trap linkage (EnemyAI.lua RemoteAttack): plate -> closest LinkedEnemy within 2000; or RoomData RemoteTrapChains
    chains = rd.get('RemoteTrapChains') if isinstance(rd.get('RemoteTrapChains'), dict) else {}
    for tr in traps:
        ch = chains.get(tr['id']) or chains.get(str(tr['id']))
        if isinstance(ch, dict) and isinstance(ch.get('Chains'), list):
            tr['triggersChains'] = ch['Chains']
            continue
        if tr['type'] in ('DartTrap', 'AxeTrapTrigger'):
            linked = gd.trap_params(tr['type']).get('LinkedEnemy')
            best = None
            for o in traps:
                if o['type'] == linked:
                    dd = math.hypot(o['x'] - tr['x'], o['y'] - tr['y'])
                    if dd <= 2000 and (best is None or dd < best[0]):
                        best = (dd, o['id'])
            if best:
                tr['firesEmitterId'] = best[1]
    res['traps'] = traps
    res['trapTypes'] = {}
    for tr in traps:
        if tr['type'] not in res['trapTypes']:
            res['trapTypes'][tr['type']] = gd.trap_params(tr['type'])
    res['breakables'] = breakables
    res['hazards'] = hazards
    if hazards:
        res['hazardParams'] = {'LavaTileWeapon': gd.trap_params('LavaTile')}
    res['cameraClampPoints'] = camera_clamps
    res['secretDoorPoints'] = secret_points
    res['counts'] = {
        'obstaclesSolid': sum(1 for o in obstacles if o['solid']),
        'impassable': sum(1 for o in obstacles if o['kind'] == 'impassable'),
        'spawnPoints': len(spawn_points), 'exits': len(exits), 'traps': len(traps),
        'breakables': len(breakables), 'hazards': len(hazards), 'lootPoints': len(loot),
    }
    with open(os.path.join(out_dir, room + '.json'), 'w') as f:
        json.dump(res, f, indent=1)
    if png_dir:
        render(res, os.path.join(png_dir, room + '.png'))
    return res


# ------------------------------------------------------------------ preview
def render(res, path, maxdim=1100):
    b = res.get('bounds')
    if not b:
        return
    pad = 300
    x0, y0, x1, y1 = b['minX'] - pad, b['minY'] - pad, b['maxX'] + pad, b['maxY'] + pad
    s = maxdim / max(x1 - x0, y1 - y0)
    W, H = int((x1 - x0) * s) + 1, int((y1 - y0) * s) + 1
    c = png.Canvas(W, H, (40, 40, 46))

    def tp(poly):
        return [((x - x0) * s, (y - y0) * s) for x, y in poly]
    for r in res['walkable'] or []:
        c.poly(tp(r['outer']), (200, 200, 190))
        for h in r['holes']:
            c.poly(tp(h), (40, 40, 46))
    for hz in res['hazards']:
        c.poly(tp(hz['polygon']), (230, 110, 30), 0.6)
    for o in res['obstacles']:
        if not o['polygon']:
            continue
        if o['kind'] == 'impassable':
            col = (90, 60, 120) if o['solid'] else (70, 70, 90)
            c.poly(tp(o['polygon']), col, 0.45)
            c.outline(tp(o['polygon']), (150, 110, 190))
        elif o['solid']:
            c.poly(tp(o['polygon']), (70, 90, 110) if not o.get('destructible') else (140, 120, 70), 0.85)
            c.outline(tp(o['polygon']), (20, 20, 20))
    for br in res['breakables']:
        if br['polygon']:
            c.poly(tp(br['polygon']), (150, 95, 50))
    for tr in res['traps']:
        if tr['polygon']:
            c.poly(tp(tr['polygon']), (220, 30, 30), 0.7)
        c.disc((tr['x'] - x0) * s, (tr['y'] - y0) * s, 3, (255, 0, 0))
    for e in res['exits']:
        if e['polygon']:
            c.poly(tp(e['polygon']), (240, 210, 40), 0.8)
        c.disc((e['x'] - x0) * s, (e['y'] - y0) * s, 5, (255, 230, 0))
    for sp in res['spawnPoints']:
        col = {'melee': (40, 90, 255), 'ranged': (40, 200, 255), 'support': (150, 60, 255)}.get(sp['role'], (60, 60, 255))
        c.disc((sp['x'] - x0) * s, (sp['y'] - y0) * s, 3, col)
    for l in res['lootPoints']:
        c.disc((l['x'] - x0) * s, (l['y'] - y0) * s, 4, (255, 0, 255))
    if res.get('heroStart'):
        h = res['heroStart']
        c.disc((h['x'] - x0) * s, (h['y'] - y0) * s, 6, (0, 220, 0))
    if res.get('heroEntrance'):
        h = res['heroEntrance']
        c.disc((h['x'] - x0) * s, (h['y'] - y0) * s, 4, (0, 120, 0))
    c.save(path)


def main(argv):
    content, out = argv[1], argv[2]
    rooms = argv[3:]
    os.makedirs(out, exist_ok=True)
    gd = GameData(content)
    pngd = os.path.join(out, 'preview')
    os.makedirs(pngd, exist_ok=True)
    index = []
    for r in rooms:
        res = extract_room(gd, r, out, pngd)
        print(r, res.get('size'), res['counts'], 'leak' if res.get('walkableStats', {}).get('leaksToGridBorder') else '')
        index.append({
            'name': r, 'biome': res['biome'], 'file': r + '.json', 'preview': 'preview/' + r + '.png',
            'size': res.get('size'), 'bounds': res.get('bounds'),
            'walkableArea': res.get('walkableStats', {}).get('areaMapUnits2'),
            'walkableRegions': len(res.get('walkable') or []),
            'zoomFraction': res['zoom']['ZoomFraction'],
            'isMiniBoss': res['roomData'].get('isMiniBossRoom', False),
            'encounters': [e['name'] for e in res['roomData'].get('legalEncounters', [])],
            'trapTypes': sorted(res['trapTypes']),
            'hazardTypes': sorted({h['type'] for h in res['hazards']}),
            'counts': res['counts'],
        })
    with open(os.path.join(out, 'index.json'), 'w') as f:
        json.dump({'coordinateNote': COORD_NOTE, 'rooms': index}, f, indent=1)


if __name__ == '__main__':
    main(sys.argv)
