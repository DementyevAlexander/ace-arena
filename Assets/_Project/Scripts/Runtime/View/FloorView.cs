using System.Collections.Generic;
using Arena.Core;
using Arena.Sim;
using UnityEngine;
using UnityEngine.Rendering;

namespace Arena.View
{
    /// <summary>Сплошной этаж Enter the Gungeon в стиле TUNIC: пол плитками палитры этажа, провалы в бездну, стены блоками
    /// (дальние от камеры высокие, ближние — низкие, чтобы не закрывать бой), решётки в дверях — поднимаются, пока комната
    /// заперта; в комнате выхода — светящийся круг. 1 клетка = 1 юнит, ось y клеток — ось z мира.</summary>
    public sealed class FloorView
    {
        const float TallWall = 1.6f, LowWall = 0.4f;
        const int FrontReach = 3;

        public readonly Transform Root;
        readonly ArenaSim sim;
        readonly List<(int room, Transform bars)> gates = new();
        Transform exitPad;
        Material exitMat;

        /// <summary>cells = false — этаж из арен: пол и стены рисует RoomView, здесь только решётки дверей и круг выхода.</summary>
        public FloorView(ArenaSim sim, bool cells = true)
        {
            this.sim = sim;
            var f = sim.Floor;
            var pal = RoomView.For(f.Polygonal ? sim.Room.biome : f.FloorId);
            Root = new GameObject("Floor " + f.FloorId + " " + f.Flow).transform;
            var rnd = new System.Random(f.Seed);
            if (cells) BuildCells(f, pal, rnd);
            BuildGates(f, pal);
        }

        void BuildCells(EtgFloorLayout f, RoomView.Palette pal, System.Random rnd)
        {

            var floor = new MeshBuilder();
            var floorDark = new MeshBuilder();
            var pits = new MeshBuilder();
            var walls = new MeshBuilder();
            var tops = new MeshBuilder();
            var b = f.Bounds;
            for (int y = b.yMin; y < b.yMax; y++)
                for (int x = b.xMin; x < b.xMax; x++)
                {
                    var c = f.At(x, y);
                    switch (c)
                    {
                        case EtgCell.Floor:
                        case EtgCell.Door:
                            // плитки: крупный шум — тёмные пятна, мелкий — отдельные плиты
                            bool dark = ((x * 7 + y * 13) % 11 == 0) || rnd.NextDouble() < 0.08;
                            (dark ? floorDark : floor).Quad(x, y, 0f);
                            break;
                        case EtgCell.Pit:
                            pits.Quad(x, y, -0.6f);
                            break;
                        case EtgCell.Wall:
                            // камера смотрит на +x +y: если за стеной (в сторону взгляда, до FrontReach клеток) есть пол — стена
                            // его закрывает, её делаем низкой; толстые стены ETG (2–3 клетки) иначе прячут Смерть
                            float h = OpensFloor(f, x, y) ? LowWall : TallWall;
                            walls.Box(x, y, h);
                            tops.Quad(x, y, h + 0.005f);
                            break;
                    }
                }
            Add(floor, Art.LitMat(pal.Floor), true);
            Add(floorDark, Art.LitMat(pal.FloorDark), true);
            Add(pits, Art.LitMat(Color.Lerp(pal.Void, Color.black, 0.4f)), false);
            Add(walls, Art.LitMat(pal.Wall), true);
            Add(tops, Art.LitMat(pal.WallTop), false);

            // бездна под этажом
            var center = new Vector3(b.center.x, -3f, b.center.y);
            Art.Part(Root, MeshKit.Box(), Art.LitMat(pal.Void), center, new Vector3(b.width + 80f, 0.5f, b.height + 80f), default, false).name = "Abyss";
        }

        void BuildGates(EtgFloorLayout f, RoomView.Palette pal)
        {

            // решётки дверей: столбики и прутья поперёк проёма, своя комната — та, в которой дверь
            var iron = Art.LitMat(Color.Lerp(pal.Wall, Color.black, 0.45f));
            var glow = Art.LitMat(pal.Accent, 1.2f);
            foreach (var r in f.Rooms)
                foreach (var d in r.Doors)
                {
                    var gate = new GameObject("Gate").transform;
                    gate.SetParent(Root, false);
                    var bars = new GameObject("Bars").transform;
                    bars.SetParent(gate, false);
                    foreach (var c in d.Cells)
                    {
                        var at = new Vector3(c.x + 0.5f, 0f, c.y + 0.5f);
                        bool alongX = d.Dir is EtgData.North or EtgData.South; // проём поперёк оси x
                        for (int k = 0; k < 3; k++)
                        {
                            float o = -0.33f + 0.33f * k;
                            var pos = at + (alongX ? new Vector3(o, 0.6f, 0f) : new Vector3(0f, 0.6f, o));
                            Art.Part(bars, MeshKit.Box(), iron, pos, new Vector3(0.09f, 1.2f, 0.09f), default, false);
                        }
                        Art.Part(bars, MeshKit.Box(), glow, at + new Vector3(0f, 1.22f, 0f), alongX ? new Vector3(1f, 0.06f, 0.12f) : new Vector3(0.12f, 0.06f, 1f), default, false);
                    }
                    bars.gameObject.SetActive(false);
                    gates.Add((r.Index, bars));
                }

            // выход с этажа — круг света в центре комнаты выхода
            if (f.Exit >= 0)
            {
                var ex = f.Rooms[f.Exit];
                exitMat = Art.DecalInstance(pal.Accent, 1, 6);
                exitPad = Art.Decal(Root, MeshKit.FloorQuad(), exitMat, 0.06f).transform;
                exitPad.position = new Vector3(ex.Center.x, Art.FloorTop + 0.06f, ex.Center.y);
                exitPad.localScale = Vector3.one * 4f;
                var l = new GameObject("ExitLight").AddComponent<Light>();
                l.transform.SetParent(exitPad, true);
                l.transform.position = exitPad.position + Vector3.up * 1.5f;
                l.type = LightType.Point; l.color = pal.Accent; l.range = 6f; l.intensity = 2.5f; l.shadows = LightShadows.None;
            }
        }

        static bool OpensFloor(EtgFloorLayout f, int x, int y)
        {
            for (int j = 0; j <= FrontReach; j++)
                for (int i = 0; i <= FrontReach; i++)
                {
                    if (i == 0 && j == 0) continue;
                    var c = f.At(x + i, y + j);
                    if (EtgFloorLayout.Walkable(c) || c == EtgCell.Pit) return true;
                }
            return false;
        }

        void Add(MeshBuilder mb, Material mat, bool shadows)
        {
            if (mb.Empty) return;
            var go = new GameObject(mat.name);
            go.transform.SetParent(Root, false);
            go.AddComponent<MeshFilter>().sharedMesh = mb.Build();
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
        }

        public void Sync(float time)
        {
            foreach (var (room, bars) in gates)
            {
                bool locked = sim.ActiveRoom == room;
                if (bars.gameObject.activeSelf != locked) bars.gameObject.SetActive(locked);
            }
            if (exitMat != null)
                exitMat.SetColor("_BaseColor", FadeTo(exitMat.GetColor("_BaseColor"), 0.55f + 0.35f * Mathf.Sin(time * 3f)));
        }

        public void Destroy() => Object.Destroy(Root.gameObject);

        static Color FadeTo(Color c, float a) => new(c.r, c.g, c.b, a);

        /// <summary>Сборщик одного меша из квадратов пола и коробок стен (индексы 32-бит — этаж большой).</summary>
        sealed class MeshBuilder
        {
            readonly List<Vector3> v = new();
            readonly List<int> t = new();
            public bool Empty => v.Count == 0;

            public void Quad(float x, float z, float y)
            {
                int i = v.Count;
                v.Add(new Vector3(x, y, z)); v.Add(new Vector3(x, y, z + 1)); v.Add(new Vector3(x + 1, y, z + 1)); v.Add(new Vector3(x + 1, y, z));
                t.Add(i); t.Add(i + 1); t.Add(i + 2); t.Add(i); t.Add(i + 2); t.Add(i + 3);
            }

            public void Box(float x, float z, float h)
            {
                Side(new Vector3(x, 0, z), new Vector3(x + 1, 0, z), h);         // юг
                Side(new Vector3(x + 1, 0, z), new Vector3(x + 1, 0, z + 1), h); // восток
                Side(new Vector3(x + 1, 0, z + 1), new Vector3(x, 0, z + 1), h); // север
                Side(new Vector3(x, 0, z + 1), new Vector3(x, 0, z), h);         // запад
            }

            void Side(Vector3 a, Vector3 b, float h)
            {
                int i = v.Count;
                v.Add(a); v.Add(a + Vector3.up * h); v.Add(b + Vector3.up * h); v.Add(b);
                t.Add(i); t.Add(i + 1); t.Add(i + 2); t.Add(i); t.Add(i + 2); t.Add(i + 3);
            }

            public Mesh Build()
            {
                var m = new Mesh { indexFormat = IndexFormat.UInt32 };
                m.SetVertices(v);
                m.SetTriangles(t, 0);
                m.RecalculateNormals();
                m.RecalculateBounds();
                return m;
            }
        }
    }
}
