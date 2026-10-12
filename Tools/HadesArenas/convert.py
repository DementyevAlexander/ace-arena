"""Арены Hades II -> конфиги Ace Arena (Assets/_Project/Resources/Arenas/*.json).

    python Tools/HadesArenas/convert.py                      # 30 арен из списка ARENAS + index.json
    python Tools/HadesArenas/convert.py F_Combat03 G_Combat05 # выбранные комнаты
    python Tools/HadesArenas/convert.py --survey --preview p  # метрики и PNG всех боевых комнат
    --hades "<папка Hades II>"  --preview <папка для PNG-превью (нужен Pillow)>

Что берётся из игры:
  * Content/Maps/bin/<комната>.thing_bin - расстановка вещей (sgb.py);
  * Content/Game/Obstacles/*.sjson - формы препятствий (Points), StopsUnits / StopsProjectiles, Tallness.
Препятствие = вещь с Points и явным StopsUnits = true (невидимые _Impassable_*, деревья, камни, кусты, урны, двери).
Форма масштабируется на Scale и поворачивается на Angle, если у типа RotateGeometry.

Перевод координат. В Hades позиции - пиксели экрана (Y вниз), пол - изометрия 2:1:
ромб тайла 128x64 - квадрат на полу, поэтому глубина на полу = 2 x экранный Y.
Камера Ace Arena (наклон 30, поворот 45) рисует пол тем же ромбом, так что комната на экране выглядит как в Hades.
Масштаб - по скорости: Мелиноя бежит 540 пикс/с (PlayerUnits.sjson), Ace - 4.1 юнит/с,
значит 1 пиксель = 4.1 / 540 юнита, и комната пробегается за то же время.
"""
import argparse, glob, json, math, os, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import sgb, sjson

HADES = r"C:\Program Files (x86)\Steam\steamapps\common\Hades II"
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'Assets', '_Project', 'Resources', 'Arenas')

ACE_SPEED, MEL_SPEED = 4.1, 540.0
K = ACE_SPEED / MEL_SPEED          # юнитов на пиксель
PLAYER_R = 0.3                      # PlayerTuning.radius
CELL = 0.5                          # шаг сетки проходимости
TALL_CAP = 3.0

BIOMES = {'F': 'Erebus', 'G': 'Oceanus', 'H': 'Fields of Mourning', 'I': 'Tartarus',
          'N': 'Ephyra', 'O': 'Thessaly', 'P': 'Olympus', 'Q': 'Summit'}

# 30 арен (отобраны по превью --survey): замкнутые, 100-420 кв. юнитов, почти все точки спавна в одной области.
# Поля скорби (H) пропущены - их границы не замыкаются препятствиями; на Олимпе (P) нет EnemyPoint.
ARENAS = [
    'F_Combat01', 'F_Combat03', 'F_Combat05', 'F_Combat06', 'F_Combat08', 'F_Combat13', 'F_Combat15',
    'G_Combat01', 'G_Combat02', 'G_Combat06', 'G_Combat07', 'G_Combat11',
    'I_Combat03', 'I_Combat05', 'I_Combat12', 'I_Combat13', 'I_Combat16',
    'N_Combat02', 'N_Combat06', 'N_Combat08', 'N_Combat14', 'N_Combat23',
    'O_Combat03', 'O_Combat05', 'O_Combat06', 'O_Combat09',
    'Q_Combat04', 'Q_Combat10', 'Q_Combat11', 'Q_Combat16',
]
ACTIVE = 'N_Combat06'  # подключённая арена: рыночная улица Эфиры с колоннадой


# ---------- данные игры ----------

def _merge(base, over):
    out = dict(base)
    for k, v in over.items():
        out[k] = _merge(out[k], v) if isinstance(v, dict) and isinstance(out.get(k), dict) else v
    return out


def load_obstacle_types(hades):
    raw = {}
    for f in glob.glob(os.path.join(hades, 'Content', 'Game', 'Obstacles', '*.sjson')):
        for o in sjson.loads(open(f, encoding='utf-8-sig').read()).get('Obstacles', []) or []:
            if isinstance(o, dict) and 'Name' in o:
                raw[o['Name']] = o
    done = {}

    def get(name, depth=0):
        if name in done: return done[name]
        o = raw.get(name)
        if o is None: return None
        parent = o.get('InheritFrom')
        base = get(parent, depth + 1) if parent and depth < 40 else None
        done[name] = _merge(base, o) if base else dict(o)
        return done[name]

    for n in raw: get(n)
    return done


def to_ground(sx, sy):
    """Пиксели экрана Hades -> плоскость пола Ace Arena (x, z). Камера: вправо = (1,0,-1)/√2, вверх по экрану = (1,0,1)/√2."""
    u, v = sx * K, -sy * 2.0 * K
    c = math.sqrt(0.5)
    return (c * (u + v), c * (v - u))


# Тип для вида: по имени вещи Hades. Порядок важен: HouseBarrel01 — бочка, а не дом.
KINDS = [
    ('barrel', ('barrel', 'haybale', 'basket', 'keg')),
    ('vase', ('vase', 'urn', 'amphora', 'breakable', 'candle', 'jar', 'pot')),
    ('crate', ('crate', 'box', 'barricade', 'cart', 'table', 'shelf', 'bench', 'chest', 'beam')),
    ('column', ('pillar', 'column', 'statue', 'post', 'pedestal', 'obelisk', 'lamp', 'brazier', 'torch')),
    ('rubble', ('rubble', 'rock', 'stone', 'corpse', 'debris', 'sheep', 'bone', 'skull', 'ghost', 'cover')),
    ('building', ('building', 'house', 'roof', 'tower', 'temple', 'shrine', 'hut', 'arch', 'gate')),
    ('masonry', ('wall', 'fence', 'rail', 'balust', 'brick', 'cornice', 'stair')),
    ('door', ('door',)),
    ('tree', ('tree', 'stump')),
    ('bush', ('shrub', 'bush', 'hedge', 'plant', 'flower', 'grass')),
]


def kind_of(name, stops_projectiles):
    n = name.lower()
    if n.startswith('_impassable'): return 'wall' if stops_projectiles else 'gap'
    if not stops_projectiles and ('water' in n or 'pit' in n or 'lava' in n): return 'gap'
    for kind, keys in KINDS:
        if any(k in n for k in keys): return kind
    return 'rubble'


# ---------- геометрия ----------

def point_in_poly(x, y, poly):
    inside = False
    j = len(poly) - 1
    for i in range(len(poly)):
        xi, yi = poly[i]; xj, yj = poly[j]
        if (yi > y) != (yj > y) and x < (xj - xi) * (y - yi) / (yj - yi) + xi:
            inside = not inside
        j = i
    return inside


def dist_to_poly_edges(x, y, poly):
    best = 1e9
    j = len(poly) - 1
    for i in range(len(poly)):
        ax, ay = poly[j]; bx, by = poly[i]
        dx, dy = bx - ax, by - ay
        l2 = dx * dx + dy * dy
        t = 0.0 if l2 == 0 else max(0.0, min(1.0, ((x - ax) * dx + (y - ay) * dy) / l2))
        px, py = ax + dx * t - x, ay + dy * t - y
        best = min(best, px * px + py * py)
        j = i
    return math.sqrt(best)


def bbox(poly):
    xs = [p[0] for p in poly]; ys = [p[1] for p in poly]
    return min(xs), min(ys), max(xs), max(ys)


def signed_area(poly):
    a = 0.0
    for i in range(len(poly)):
        x1, y1 = poly[i - 1]; x2, y2 = poly[i]
        a += x1 * y2 - x2 * y1
    return a * 0.5


# ---------- комната ----------

def build_room(room, hades, types):
    things = sgb.read_things(os.path.join(hades, 'Content', 'Maps', 'bin', room + '.thing_bin'))
    blockers, spawns, start, exit_ = [], [], None, None
    for t in things:
        n = t['name']
        if n == 'EnemyPoint': spawns.append(to_ground(t['x'], t['y']))
        elif n == 'HeroStart' and start is None: start = to_ground(t['x'], t['y'])
        elif n == 'HeroExit' and exit_ is None: exit_ = to_ground(t['x'], t['y'])
        d = types.get(n)
        if not d: continue
        th = d.get('Thing', {})
        pts = th.get('Points')
        if not pts or len(pts) < 3 or th.get('StopsUnits') is not True: continue
        a = math.radians(t['angle']) if th.get('RotateGeometry') else 0.0
        ca, sa = math.cos(a), math.sin(a)
        poly = []
        for p in pts:
            x, y = p.get('X', 0.0) * t['scale'], p.get('Y', 0.0) * t['scale']
            poly.append(to_ground(t['x'] + x * ca + y * sa, t['y'] - x * sa + y * ca))
        if abs(signed_area(poly)) < 1e-4: continue
        if signed_area(poly) < 0: poly.reverse()  # против часовой стрелки
        proj = th.get('StopsProjectiles', True) is not False
        tall = float(th.get('Tallness', 300.0))
        blockers.append({'name': n, 'kind': kind_of(n, proj), 'projectiles': proj,
                         'height': round(min(TALL_CAP, max(0.25, tall * K)), 2), 'poly': poly, 'bb': bbox(poly)})
    if not spawns:
        return None, 'нет EnemyPoint'

    # сетка проходимости: центр клетки вне препятствий и не ближе радиуса Ace к их краю
    cx = sum(p[0] for p in spawns) / len(spawns); cz = sum(p[1] for p in spawns) / len(spawns)
    half = 30.0
    x0, z0 = cx - half, cz - half
    n = int(2 * half / CELL)

    def free(x, z):
        for b in blockers:
            x1, y1, x2, y2 = b['bb']
            if x < x1 - PLAYER_R or x > x2 + PLAYER_R or z < y1 - PLAYER_R or z > y2 + PLAYER_R: continue
            if point_in_poly(x, z, b['poly']) or dist_to_poly_edges(x, z, b['poly']) < PLAYER_R: return False
        return True

    cache = {}

    def ok(i, j):
        if (i, j) not in cache:
            cache[(i, j)] = 0 <= i < n and 0 <= j < n and free(x0 + (i + 0.5) * CELL, z0 + (j + 0.5) * CELL)
        return cache[(i, j)]

    def cell_of(p): return int((p[0] - x0) / CELL), int((p[1] - z0) / CELL)

    # заливка от точки спавна, у которой больше всего соседей
    best = None
    for s in spawns:
        c = cell_of(s)
        if not ok(*c): continue
        seen = {c}; stack = [c]; leak = False
        while stack:
            i, j = stack.pop()
            if i in (0, n - 1) or j in (0, n - 1): leak = True
            for q in ((i + 1, j), (i - 1, j), (i, j + 1), (i, j - 1)):
                if q not in seen and ok(*q):
                    seen.add(q); stack.append(q)
        if best is None or len(seen) > len(best[0]): best = (seen, leak)
        if len(seen) * 2 > len(spawns): break
    if best is None:
        return None, 'точки спавна внутри препятствий'
    region, leak = best
    if leak:
        return None, 'комната не замкнута'

    good_spawns = [s for s in spawns if cell_of(s) in region]
    if start is None or cell_of(start) not in region:
        # HeroStart обычно в дверном проёме: берём ближайшую проходимую клетку
        ref = start or (cx, cz)
        c = min(region, key=lambda q: (x0 + (q[0] + 0.5) * CELL - ref[0]) ** 2 + (z0 + (q[1] + 0.5) * CELL - ref[1]) ** 2)
        start = (x0 + (c[0] + 0.5) * CELL, z0 + (c[1] + 0.5) * CELL)

    # центр комнаты — в начало координат
    xs = [i for i, _ in region]; zs = [j for _, j in region]
    mnx, mxx = x0 + min(xs) * CELL, x0 + (max(xs) + 1) * CELL
    mnz, mxz = z0 + min(zs) * CELL, z0 + (max(zs) + 1) * CELL
    ox, oz = (mnx + mxx) / 2, (mnz + mxz) / 2

    def P(p): return {'x': round(p[0] - ox, 3), 'y': round(p[1] - oz, 3)}

    # пол: клетки 1×1, где проходима хотя бы одна из четырёх подклеток
    fx0, fz0 = math.floor(mnx - ox), math.floor(mnz - oz)
    fw, fh = math.ceil(mxx - ox) - fx0, math.ceil(mxz - oz) - fz0
    rows = []
    for r in range(fh):
        row = ''
        for c in range(fw):
            wx, wz = fx0 + c + ox, fz0 + r + oz
            sub = {cell_of((wx + 0.25 + a * 0.5, wz + 0.25 + b * 0.5)) for a in (0, 1) for b in (0, 1)}
            row += '.' if sub & region else '#'
        rows.append(row)

    # оставляем препятствия рядом с комнатой (для коллизии и вида)
    margin = 10.0
    walls = []
    for b in blockers:
        x1, y1, x2, y2 = b['bb']
        if x2 < mnx - margin or x1 > mxx + margin or y2 < mnz - margin or y1 > mxz + margin: continue
        walls.append({'kind': b['kind'], 'source': b['name'], 'projectiles': b['projectiles'], 'height': b['height'],
                      'points': [P(p) for p in b['poly']]})

    letter = room.split('_')[0]
    return {
        'id': room,
        'title': f"{BIOMES.get(letter, letter)} — {room}",
        'biome': BIOMES.get(letter, letter),
        'source': f'Hades II, Content/Maps/bin/{room}.thing_bin',
        'unitsPerPixel': round(K, 6),
        'boundsMin': P((mnx, mnz)), 'boundsMax': P((mxx, mxz)),
        'playerStart': P(start),
        'exit': P(exit_) if exit_ else P(start),
        'spawns': [P(s) for s in good_spawns],
        'floorOrigin': {'x': fx0, 'y': fz0},
        'floor': rows,
        'walls': walls,
    }, f'{len(region) * CELL * CELL:.0f} кв. ю, {len(good_spawns)}/{len(spawns)} спавнов, {len(walls)} препятствий'


def preview(arena, path):
    from PIL import Image, ImageDraw
    s = 20
    mn, mx = arena['boundsMin'], arena['boundsMax']
    pad = 4
    W, H = int((mx['x'] - mn['x'] + 2 * pad) * s), int((mx['y'] - mn['y'] + 2 * pad) * s)

    def Q(p): return ((p['x'] - mn['x'] + pad) * s, H - (p['y'] - mn['y'] + pad) * s)

    im = Image.new('RGB', (W, H), (40, 40, 40))
    g = ImageDraw.Draw(im)
    o = arena['floorOrigin']
    for r, row in enumerate(arena['floor']):
        for c, ch in enumerate(row):
            if ch == '.':
                a = Q({'x': o['x'] + c, 'y': o['y'] + r + 1}); b = Q({'x': o['x'] + c + 1, 'y': o['y'] + r})
                g.rectangle((a, b), fill=(170, 160, 140))
    col = {'wall': (150, 60, 60), 'gap': (60, 90, 160), 'tree': (40, 120, 70), 'bush': (70, 150, 70),
           'rubble': (120, 120, 120), 'vase': (200, 140, 60), 'barrel': (150, 100, 60), 'crate': (170, 120, 70),
           'column': (220, 220, 200), 'building': (110, 80, 140), 'masonry': (160, 150, 140), 'door': (230, 230, 230)}
    for w in arena['walls']:
        g.polygon([Q(p) for p in w['points']], fill=col[w['kind']], outline=(20, 20, 20))
    for sp in arena['spawns']:
        x, y = Q(sp); g.ellipse((x - 4, y - 4, x + 4, y + 4), fill=(255, 220, 0))
    x, y = Q(arena['playerStart']); g.ellipse((x - 6, y - 6, x + 6, y + 6), fill=(0, 255, 0))
    g.text((4, 4), arena['title'], fill=(255, 255, 255))
    im.save(path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('rooms', nargs='*')
    ap.add_argument('--hades', default=HADES)
    ap.add_argument('--out', default=OUT)
    ap.add_argument('--preview')
    ap.add_argument('--survey', action='store_true')
    a = ap.parse_args()
    types = load_obstacle_types(a.hades)
    rooms = a.rooms or ARENAS
    if a.survey:
        rooms = sorted(os.path.basename(f)[:-10] for f in glob.glob(os.path.join(a.hades, 'Content', 'Maps', 'bin', '?_Combat*.thing_bin')))
    os.makedirs(a.out, exist_ok=True)
    made = []
    for room in rooms:
        arena, info = build_room(room, a.hades, types)
        print(f'{room:14} {"OK " if arena else "-- "} {info}', flush=True)
        if not arena: continue
        if a.preview:
            os.makedirs(a.preview, exist_ok=True)
            preview(arena, os.path.join(a.preview, room + '.png'))
        if not a.survey:
            with open(os.path.join(a.out, room + '.json'), 'w', encoding='utf-8') as f:
                json.dump(arena, f, ensure_ascii=False, separators=(',', ':'))
            made.append(room)
    if made and not a.survey and not a.rooms:
        with open(os.path.join(a.out, 'index.json'), 'w', encoding='utf-8') as f:
            json.dump({'active': ACTIVE, 'arenas': made}, f, ensure_ascii=False, indent=2)


if __name__ == '__main__':
    main()
