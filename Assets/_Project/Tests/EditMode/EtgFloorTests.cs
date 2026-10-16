using System.Collections.Generic;
using System.IO;
using Arena.Core;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests
{
    /// <summary>Этажи Enter the Gungeon: данные загружаются, каждый этаж собирается по разным зёрнам, комнаты не налезают
    /// друг на друга, от входа пешком доступны все, есть вход и босс; одно зерно — один этаж.
    /// Если задана ARENA_CAPTURE_DIR — планы этажей в PNG (floors/*.png).</summary>
    public class EtgFloorTests
    {
        static readonly string[] Floors = { "Castle", "Gungeon", "Mines", "Hollow", "Forge" };

        [Test]
        public void DataLoads()
        {
            Assert.AreEqual(5, EtgData.Floors.Count);
            foreach (var f in Floors)
            {
                var floor = EtgData.GetFloor(f);
                Assert.IsNotNull(floor, f);
                Assert.Greater(floor.flows.Length, 0, f);
                Assert.Greater(floor.bossTables.Length, 0, f);
                foreach (var flow in floor.flows) Assert.IsTrue(EtgData.Flows.ContainsKey(flow), flow);
            }
            Assert.Greater(EtgData.Rooms.Count, 1000);
        }

        [Test]
        public void EveryFloorGeneratesConnected()
        {
            var dir = System.Environment.GetEnvironmentVariable("ARENA_CAPTURE_DIR");
            foreach (var f in Floors)
                for (int seed = 1; seed <= 20; seed++)
                {
                    var l = EtgFloorGenerator.Generate(f, seed);
                    Assert.Greater(l.Rooms.Count, 10, $"{f}/{seed}");
                    Assert.AreEqual(EtgRoomKind.Entrance, l.Rooms[l.Entrance].Kind, $"{f}/{seed}: вход");
                    Assert.GreaterOrEqual(l.Boss, 0, $"{f}/{seed}: босс");
                    AssertNoOverlap(l, $"{f}/{seed}");
                    if (seed <= 2 && !string.IsNullOrEmpty(dir)) SavePlan(l, Path.Combine(dir, "floors", $"{f}_{seed}.png"));
                }
        }

        [Test]
        public void SameSeedSameFloor()
        {
            var a = EtgFloorGenerator.Generate("Mines", 42);
            var b = EtgFloorGenerator.Generate("Mines", 42);
            Assert.AreEqual(a.Flow, b.Flow);
            Assert.AreEqual(a.Rooms.Count, b.Rooms.Count);
            for (int i = 0; i < a.Rooms.Count; i++)
            {
                Assert.AreEqual(a.Rooms[i].Room.name, b.Rooms[i].Room.name);
                Assert.AreEqual(a.Rooms[i].Pos, b.Rooms[i].Pos);
            }
        }

        static void AssertNoOverlap(EtgFloorLayout l, string tag)
        {
            for (int i = 0; i < l.Rooms.Count; i++)
                for (int j = i + 1; j < l.Rooms.Count; j++)
                    Assert.IsFalse(l.Rooms[i].Rect.Overlaps(l.Rooms[j].Rect), $"{tag}: {l.Rooms[i].Room.name} и {l.Rooms[j].Room.name}");
        }

        /// <summary>План: пол серый, стены тёмные, провалы чёрные, двери жёлтые, комнаты подкрашены по виду.</summary>
        static void SavePlan(EtgFloorLayout l, string path)
        {
            const int k = 4;
            var b = l.Bounds;
            var tex = new Texture2D(b.width * k, b.height * k, TextureFormat.RGB24, false);
            var kindColor = new Dictionary<EtgRoomKind, Color>
            {
                [EtgRoomKind.Entrance] = new(0.4f, 0.8f, 0.4f), [EtgRoomKind.Exit] = new(0.4f, 0.6f, 1f), [EtgRoomKind.Boss] = new(0.9f, 0.3f, 0.3f),
                [EtgRoomKind.BossFoyer] = new(0.8f, 0.5f, 0.4f), [EtgRoomKind.Reward] = new(1f, 0.85f, 0.3f), [EtgRoomKind.Hub] = new(0.75f, 0.7f, 0.85f),
                [EtgRoomKind.Connector] = new(0.6f, 0.6f, 0.6f), [EtgRoomKind.Normal] = new(0.7f, 0.68f, 0.62f),
            };
            for (int y = 0; y < b.height; y++)
                for (int x = 0; x < b.width; x++)
                {
                    int gx = b.x + x, gy = b.y + y;
                    var c = l.At(gx, gy);
                    int r = l.RoomAt(gx, gy);
                    var col = c switch
                    {
                        EtgCell.Wall => new Color(0.18f, 0.16f, 0.2f),
                        EtgCell.Pit => Color.black,
                        EtgCell.Door => new Color(1f, 0.9f, 0.2f),
                        EtgCell.Floor => r >= 0 ? kindColor[l.Rooms[r].Kind] : new Color(0.5f, 0.48f, 0.45f),
                        _ => new Color(0.08f, 0.08f, 0.1f),
                    };
                    for (int dy = 0; dy < k; dy++) for (int dx = 0; dx < k; dx++) tex.SetPixel(x * k + dx, y * k + dy, col);
                }
            tex.Apply();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
        }
    }
}
