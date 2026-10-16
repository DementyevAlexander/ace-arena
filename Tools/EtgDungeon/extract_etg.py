#!/usr/bin/env python3
"""Enter the Gungeon -> Ace Arena: схемы этажей (DungeonFlow), таблицы комнат (GenericRoomTable) и комнаты
(PrototypeDungeonRoom) пяти основных этажей из бандлов игры (StreamingAssets/Assets, Unity 2017.4).

Нужен UnityPy (pip install UnityPy). Запуск:
  python3 -I extract_etg.py "<EtG_OSX.app>/Contents/Resources/Data/StreamingAssets/Assets" Assets/_Project/Resources/Etg

Выход:
  etg_floors.json — этажи, их схемы (узлы, связи, петли, цепочки), таблицы комнат;
  etg_rooms.json  — комнаты: категория, размер, клетки ('.' пол, '#' стена, 'o' провал), выходы, позиции врагов.
Данные чужой игры — только для прототипа.
"""
import json
import os
import sys

import UnityPy

FLOORS = [  # порядок забега: этаж, бандл, короткое имя в компоненте Dungeon
    ('Castle', 'base_castle', '#CASTLE_SHORTNAME'),
    ('Gungeon', 'base_gungeon', '#GUNGEON_SHORTNAME'),
    ('Mines', 'base_mines', '#MINES_SHORTNAME'),
    ('Hollow', 'base_catacombs', '#CATACOMBS_SHORTNAME'),
    ('Forge', 'base_forge', '#FORGE_SHORTNAME'),
]
# узлы босса в схемах без таблицы: в игре комнату босса выбирает менеджер боссов из BossTable_0N_* своего этажа
BOSS_TABLES = {
    'Castle': ['BossTable_01_BulletKing', 'BossTable_01_GatlingGull', 'BossTable_01_BulletBros'],
    'Gungeon': ['BossTable_02_Beholster', 'BossTable_02_Meduzi', 'BossTable_02_Bashellisk'],
    'Mines': ['BossTable_03_Tank', 'BossTable_03_MineFlayer', 'BossTable_03_Powderskull'],
    'Hollow': ['BossTable_04_Statues', 'BossTable_04_DemonWall'],
    'Forge': ['BossTable_05_Dragun'],
}
# узлы награды без таблицы: комнаты с сундуком категории Reward и клетки-награды
REWARD_ROOMS = ['Castle_Reward_Room_godrays_001', 'Gungeon_RewardRoom_1', 'Reward Room']
REWARD_TABLES = ['LockedRewardCageRoomTable']
CELL = {1: '#', 2: '.', 4: 'o'}


def main(root, out_dir):
    bundles = [os.path.join(root, b) for b in ('shared_auto_001', 'shared_auto_002', 'shared_base_001', 'flows_base_001', 'brave_resources_001')]
    bundles += [os.path.join(root, 'dungeons', b) for _, b, _ in FLOORS]
    env = UnityPy.load(*bundles)

    files = {}  # имя сериализованного файла (cab-...) -> SerializedFile
    for obj in env.objects:
        f = obj.assets_file
        files.setdefault(os.path.basename(f.name).lower(), f)

    def resolve(owner, pptr):
        """PPtr (m_FileID, m_PathID) объекта owner -> ObjectReader или None."""
        if not pptr or not pptr.get('m_PathID'):
            return None
        f = owner.assets_file
        if pptr['m_FileID']:
            ext = f.externals[pptr['m_FileID'] - 1].path
            f = files.get(os.path.basename(ext).lower())
            if f is None:
                return None
        return f.objects.get(pptr['m_PathID'])

    cache = {}

    def tree(obj):
        key = (id(obj.assets_file), obj.path_id)
        if key not in cache:
            cache[key] = obj.read_typetree()
        return cache[key]

    def name_of(owner, pptr):
        o = resolve(owner, pptr)
        return tree(o).get('m_Name') if o is not None else None

    tables, rooms_needed, flows = {}, {}, {}
    by_name = {}  # (класс, имя) -> объект: таблицы боссов и наград, комнаты наград
    for obj in env.objects:
        if obj.type.name != 'MonoBehaviour':
            continue
        try:
            cn = obj.read().m_Script.read().m_ClassName
        except Exception:
            continue
        if cn in ('GenericRoomTable', 'PrototypeDungeonRoom'):
            by_name.setdefault((cn, tree(obj).get('m_Name')), obj)

    def add_table(owner, pptr):
        o = resolve(owner, pptr)
        if o is None:
            return None
        t = tree(o)
        name = t['m_Name']
        if name in tables:
            return name
        tables[name] = entry = {'rooms': [], 'tables': []}
        for e in t.get('includedRooms', {}).get('elements', []):
            r = resolve(o, e.get('room'))
            if r is None:
                continue
            rn = tree(r)['m_Name']
            rooms_needed[rn] = r
            entry['rooms'].append([rn, round(e.get('weight', 1.0), 3), e.get('maxCopies', 0) if e.get('limitedCopies') else 0])
        for sub in t.get('includedRoomTables', []):
            sn = add_table(o, sub)
            if sn:
                entry['tables'].append(sn)
        return name

    dungeons = {}  # короткое имя этажа (#CASTLE_SHORTNAME) -> компонент Dungeon
    for obj in env.objects:
        if obj.type.name != 'MonoBehaviour':
            continue
        try:
            if obj.read().m_Script.read().m_ClassName != 'Dungeon':
                continue
        except Exception:
            continue
        dungeons[tree(obj).get('DungeonShortName', '')] = obj
    print('dungeons', sorted(dungeons))

    floors = []
    for fid, bname, short in FLOORS:
        dungeon = dungeons.get(short)
        if dungeon is None:
            raise SystemExit('no Dungeon ' + short)
        names = []
        for fp in tree(dungeon)['PatternSettings']['flows']:
            fo = resolve(dungeon, fp)
            ft = tree(fo)
            names.append(ft['m_Name'])
            nodes = []
            for n in ft['m_nodes']:
                exact = resolve(fo, n['overrideExactRoom'])
                if exact is not None:
                    rooms_needed[tree(exact)['m_Name']] = exact
                nodes.append({
                    'id': n['guidAsString'], 'cat': n['roomCategory'], 'type': n['nodeType'],
                    'room': tree(exact)['m_Name'] if exact is not None else None,
                    'table': add_table(fo, n['overrideRoomTable']),
                    'chance': round(n['percentChance'], 3), 'priority': n['priority'],
                    'children': n['childNodeGuids'], 'loop': n['loopTargetNodeGuid'] or None, 'loopOneWay': bool(n['loopTargetIsOneWay']),
                    'expands': bool(n['nodeExpands']), 'chain': [n['minChainLength'], n['maxChainLength']],
                    'chainProto': n['initialChainPrototype'], 'subchain': bool(n['isSubchainStandin']),
                })
            flows[ft['m_Name']] = {
                'fallbackTable': add_table(fo, ft['fallbackRoomTable']),
                'subtypeLimits': [[r['baseCategoryRestriction'], r['normalSubcategoryRestriction'], r['maximumRoomsOfSubtype']] for r in ft.get('subtypeRestrictions', [])],
                'nodes': nodes,
            }
        boss = []
        for tn in BOSS_TABLES[fid]:
            o = by_name.get(('GenericRoomTable', tn))
            if o is not None:
                boss.append(add_table(o, {'m_FileID': 0, 'm_PathID': o.path_id}))
        floors.append({'id': fid, 'bundle': bname, 'flows': names, 'bossTables': boss})

    reward_tables = []
    for tn in REWARD_TABLES:
        o = by_name.get(('GenericRoomTable', tn))
        if o is not None:
            reward_tables.append(add_table(o, {'m_FileID': 0, 'm_PathID': o.path_id}))
    tables['Rewards'] = {'rooms': [], 'tables': reward_tables}
    for rn in REWARD_ROOMS:
        o = by_name.get(('PrototypeDungeonRoom', rn))
        if o is not None:
            rooms_needed[rn] = o
            tables['Rewards']['rooms'].append([rn, 1.0, 0])

    rooms = {}
    for rn, r in sorted(rooms_needed.items()):
        t = tree(r)
        w, h = t['m_width'], t['m_height']
        types = t['m_serializedCellType']
        rows = [''.join(CELL.get(types[y * w + x], ' ') for x in range(w)) for y in range(h)]
        # клетки выхода: у северных и восточных — сразу за краем (ось y вверх), приводим к крайнему ряду комнаты
        exits = [{'d': e['exitDirection'], 'cells': [[min(int(c['x']), w - 1), min(int(c['y']), h - 1)] for c in e['containedCells']]} for e in t['exitData']['exits']]
        spawns = [[int(p['contentsBasePosition']['x']), int(p['contentsBasePosition']['y'])] for p in t.get('placedObjects', []) if p.get('enemyBehaviourGuid')]
        for layer in t.get('additionalObjectLayers', []):
            for p in layer.get('placedObjects', []):
                if p.get('enemyBehaviourGuid'):
                    spawns.append([int(p['contentsBasePosition']['x']), int(p['contentsBasePosition']['y'])])
        rooms[rn] = {'cat': t['category'], 'sub': [t['subCategoryNormal'], t['subCategoryBoss'], t['subCategorySpecial'], t['subCategorySecret']],
                     'w': w, 'h': h, 'cells': rows, 'exits': exits, 'spawns': spawns}  # cells[y] — ряд y, y = 0 — юг

    # формат — под JsonUtility Unity: только объекты и массивы объектов
    xy = lambda c: {'x': c[0], 'y': c[1]}
    out_floors = {
        'floors': floors,
        'flows': [{'name': n, 'fallbackTable': f['fallbackTable'] or '',
                   'nodes': [{'id': d['id'], 'cat': d['cat'], 'room': d['room'] or '', 'table': d['table'] or '', 'chance': d['chance'],
                              'children': d['children'], 'loop': d['loop'] or '', 'loopOneWay': d['loopOneWay'], 'expands': d['expands'],
                              'chainMin': d['chain'][0], 'chainMax': d['chain'][1]} for d in f['nodes']]}
                  for n, f in flows.items()],
        'tables': [{'name': n, 'rooms': [{'room': r[0], 'weight': r[1], 'max': r[2]} for r in t['rooms']], 'tables': t['tables']} for n, t in tables.items()],
    }
    out_rooms = {'rooms': [{'name': n, 'cat': r['cat'], 'w': r['w'], 'h': r['h'], 'cells': r['cells'],
                            'exits': [{'d': e['d'], 'cells': [xy(c) for c in e['cells']]} for e in r['exits']],
                            'spawns': [xy(c) for c in r['spawns']]} for n, r in rooms.items()]}
    os.makedirs(out_dir, exist_ok=True)
    with open(os.path.join(out_dir, 'etg_floors.json'), 'w', encoding='utf-8') as f:
        json.dump(out_floors, f, ensure_ascii=False, separators=(',', ':'))
    with open(os.path.join(out_dir, 'etg_rooms.json'), 'w', encoding='utf-8') as f:
        json.dump(out_rooms, f, ensure_ascii=False, separators=(',', ':'))
    print('floors', len(floors), 'flows', len(flows), 'tables', len(tables), 'rooms', len(rooms))


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2])
