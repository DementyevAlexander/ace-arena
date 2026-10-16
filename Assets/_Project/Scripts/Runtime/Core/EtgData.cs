using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arena.Core
{
    /// <summary>Данные Enter the Gungeon (Tools/EtgDungeon/extract_etg.py → Resources/Etg): этажи, их схемы (DungeonFlow),
    /// таблицы комнат (GenericRoomTable) и комнаты (PrototypeDungeonRoom). Клетки комнаты — ряды снизу вверх (ось y вверх):
    /// '.' пол, '#' стена, 'o' провал, ' ' вне комнаты. Данные чужой игры — только для прототипа.</summary>
    public static class EtgData
    {
        /// <summary>Категории комнат и узлов схем (PrototypeDungeonRoom.RoomCategory).</summary>
        public const int Connector = 0, Hub = 1, Normal = 2, Boss = 3, Reward = 4, Special = 5, Secret = 6, Entrance = 7, Exit = 8;

        /// <summary>Направления выходов (DungeonData.Direction): север, восток, юг, запад.</summary>
        public const int North = 0, East = 2, South = 4, West = 6;

        public static Vector2Int DirVec(int d) => d switch { North => Vector2Int.up, East => Vector2Int.right, South => Vector2Int.down, _ => Vector2Int.left };
        public static int Opposite(int d) => (d + 4) % 8;

        [Serializable] public sealed class Cell { public int x, y; }

        [Serializable]
        public sealed class RoomExit
        {
            public int d;
            public Cell[] cells = Array.Empty<Cell>();
        }

        [Serializable]
        public sealed class Room
        {
            public string name;
            public int cat, w, h;
            public string[] cells = Array.Empty<string>();
            public RoomExit[] exits = Array.Empty<RoomExit>();
            public Cell[] spawns = Array.Empty<Cell>();

            public char At(int x, int y) => x < 0 || y < 0 || x >= w || y >= h || x >= cells[y].Length ? ' ' : cells[y][x];
        }

        [Serializable]
        public sealed class Node
        {
            public string id;
            public int cat;
            public string room, table, loop;
            public float chance = 1f;
            public string[] children = Array.Empty<string>();
            public bool loopOneWay, expands;
            public int chainMin, chainMax;
        }

        [Serializable]
        public sealed class Flow
        {
            public string name, fallbackTable;
            public Node[] nodes = Array.Empty<Node>();
        }

        [Serializable]
        public sealed class TableRoom
        {
            public string room;
            public float weight = 1f;
            public int max;
        }

        [Serializable]
        public sealed class Table
        {
            public string name;
            public TableRoom[] rooms = Array.Empty<TableRoom>();
            public string[] tables = Array.Empty<string>();
        }

        [Serializable]
        public sealed class Floor
        {
            public string id;
            public string[] flows = Array.Empty<string>();
            public string[] bossTables = Array.Empty<string>();
        }

        [Serializable] sealed class FloorsFile { public Floor[] floors; public Flow[] flows; public Table[] tables; }
        [Serializable] sealed class RoomsFile { public Room[] rooms; }

        public static IReadOnlyList<Floor> Floors => Load().floors;
        public static readonly Dictionary<string, Flow> Flows = new();
        public static readonly Dictionary<string, Table> Tables = new();
        public static readonly Dictionary<string, Room> Rooms = new();
        static FloorsFile file;

        static FloorsFile Load()
        {
            if (file != null) return file;
            var f = Resources.Load<TextAsset>("Etg/etg_floors");
            var r = Resources.Load<TextAsset>("Etg/etg_rooms");
            if (f == null || r == null) throw new InvalidOperationException("Нет Resources/Etg — запустите Tools/EtgDungeon/extract_etg.py");
            file = JsonUtility.FromJson<FloorsFile>(f.text);
            foreach (var x in file.flows) Flows[x.name] = x;
            foreach (var x in file.tables) Tables[x.name] = x;
            foreach (var x in JsonUtility.FromJson<RoomsFile>(r.text).rooms) Rooms[x.name] = x;
            return file;
        }

        public static Floor GetFloor(string id)
        {
            foreach (var f in Load().floors) if (f.id == id) return f;
            return null;
        }

        /// <summary>Все комнаты таблицы вместе с вложенными таблицами: (комната, вес, предел копий).</summary>
        public static List<TableRoom> Expand(string table, Func<Room, bool> filter = null)
        {
            Load();
            var list = new List<TableRoom>();
            var seen = new HashSet<string>();
            void Walk(string name)
            {
                if (string.IsNullOrEmpty(name) || !seen.Add(name) || !Tables.TryGetValue(name, out var t)) return;
                foreach (var r in t.rooms)
                    if (Rooms.TryGetValue(r.room, out var room) && (filter == null || filter(room))) list.Add(r);
                foreach (var sub in t.tables) Walk(sub);
            }
            Walk(table);
            return list;
        }
    }
}
