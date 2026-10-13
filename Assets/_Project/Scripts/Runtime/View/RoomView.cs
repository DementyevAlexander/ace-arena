using System.Collections.Generic;
using Arena.Core;
using Arena.Sim;
using UnityEngine;

namespace Arena.View
{
    /// <summary>Комната из карты Hades в low-poly стиле TUNIC: плато пола над тёмной бездной, стены и колонны призмами,
    /// лава и вода, ловушки, урны. Форма и размеры — из данных комнаты, цвета — по биому.</summary>
    public static class RoomView
    {
        public sealed class Palette
        {
            public Color Floor, FloorDark, Wall, WallTop, Void, Hazard, Accent;
        }

        public static Palette For(string biome) => biome switch
        {
            "Asphodel" => new Palette
            {
                Floor = new Color(0.62f, 0.47f, 0.38f), FloorDark = new Color(0.5f, 0.36f, 0.3f), Wall = new Color(0.36f, 0.25f, 0.23f),
                WallTop = new Color(0.48f, 0.34f, 0.3f), Void = new Color(0.16f, 0.06f, 0.05f), Hazard = new Color(1f, 0.42f, 0.08f), Accent = new Color(1f, 0.6f, 0.25f),
            },
            "Elysium" => new Palette
            {
                Floor = new Color(0.78f, 0.82f, 0.68f), FloorDark = new Color(0.62f, 0.72f, 0.52f), Wall = new Color(0.86f, 0.86f, 0.8f),
                WallTop = new Color(0.55f, 0.74f, 0.5f), Void = new Color(0.22f, 0.38f, 0.42f), Hazard = new Color(0.35f, 0.75f, 0.95f), Accent = new Color(0.95f, 0.85f, 0.45f),
            },
            "Styx" => new Palette
            {
                Floor = new Color(0.46f, 0.32f, 0.33f), FloorDark = new Color(0.36f, 0.24f, 0.26f), Wall = new Color(0.25f, 0.17f, 0.2f),
                WallTop = new Color(0.4f, 0.24f, 0.28f), Void = new Color(0.1f, 0.03f, 0.05f), Hazard = new Color(0.55f, 0.85f, 0.35f), Accent = new Color(0.85f, 0.2f, 0.25f),
            },
            "Test" => new Palette
            {
                Floor = Art.Stone, FloorDark = Art.StoneDark, Wall = Art.StoneDark, WallTop = Art.Moss,
                Void = new Color(0.62f, 0.78f, 0.74f), Hazard = new Color(1f, 0.45f, 0.1f), Accent = Art.Yellow,
            },
            _ => new Palette // Тартар
            {
                Floor = new Color(0.62f, 0.58f, 0.68f), FloorDark = new Color(0.5f, 0.46f, 0.58f), Wall = new Color(0.34f, 0.3f, 0.42f),
                WallTop = new Color(0.46f, 0.4f, 0.56f), Void = new Color(0.1f, 0.07f, 0.14f), Hazard = new Color(1f, 0.4f, 0.1f), Accent = new Color(0.7f, 0.45f, 1f),
            },
        };

        public static Transform Build(ArenaSim sim)
        {
            // арена Hades II — ночной город по своей раскладке
            if (sim.Layout != null) return BuildLayout(sim.Layout);
            var room = sim.Room;
            var pal = For(room.biome);
            var root = new GameObject("Room " + room.name).transform;
            var rnd = new System.Random(room.name?.GetHashCode() ?? 7);
            float R(float a, float b) => a + (float)rnd.NextDouble() * (b - a);

            // плато пола: верх на y = 0, толщина 0.6 — края комнаты видны обрывом над бездной
            var floorMat = Art.LitMat(pal.Floor);
            var cliffMat = Art.LitMat(Color.Lerp(pal.Wall, pal.Void, 0.35f));
            foreach (var f in room.floor)
            {
                if (f.p == null || f.p.Length < 3 || f.type == "Lava") continue; // магму рисует опасный пол
                Mesh(root, MeshKit.Slab(f.p, 0f, -0.6f), cliffMat, false);
                Mesh(root, MeshKit.Polygon(f.p, 0.001f), floorMat, true);
            }

            // плиты на полу: случайные тёмные плитки внутри контуров (не во всю комнату — экономно)
            var tile = Art.LitMat(pal.FloorDark);
            var size = room.max - room.min;
            int tiles = Mathf.Clamp((int)(size.x * size.y * 0.08f), 20, 500);
            for (int i = 0; i < tiles; i++)
            {
                var p = new Vector2(R(room.min.x, room.max.x), R(room.min.y, room.max.y));
                if (!sim.InsideRoom(p, 0.5f) || OnHazard(sim, p)) continue;
                float s = R(0.5f, 1.1f);
                Art.Part(root, MeshKit.Box(), tile, new Vector3(p.x, 0.01f, p.y), new Vector3(s, 0.03f, s * R(0.7f, 1.2f)), new Vector3(0f, R(0f, 90f), 0f), false);
            }

            // стены и препятствия. Невидимые блокираторы Hades (_Impassable_*) не рисуются — только тёмные провалы-дыры.
            // Стены между камерой и полом (ближний край) низкие, иначе закрывают Ace — в Hades их тоже не видно.
            var wallMat = Art.LitMat(pal.Wall);
            var topMat = Art.LitMat(pal.WallTop);
            var pitMat = Art.LitMat(Color.Lerp(pal.Void, Color.black, 0.3f));
            var camFwd = new Vector2(Mathf.Sin(CameraRig.Yaw * Mathf.Deg2Rad), Mathf.Cos(CameraRig.Yaw * Mathf.Deg2Rad));
            foreach (var o in sim.Obstacles)
            {
                if (o.Height <= 0f)
                {
                    if (o.Type == "Hole" && o.Poly != null) Mesh(root, MeshKit.Polygon(o.Poly, 0.004f), pitMat, false);
                    continue;
                }
                float h = Mathf.Min(o.Height, MaxWall);
                if (OccludesFloor(sim, o, camFwd)) h = Mathf.Min(h, FrontWall);
                if (o.Round)
                {
                    Art.Part(root, MeshKit.Prism(8), wallMat, Art.W(o.Pos), new Vector3(o.R * 2f, h, o.R * 2f));
                    continue;
                }
                Mesh(root, MeshKit.Slab(o.Poly, h), wallMat, true);
                // мох или трава на крыше стены — мягкий силуэт как в TUNIC
                if (h > 0.8f) Mesh(root, MeshKit.Polygon(o.Poly, h + 0.02f), topMat, false);
            }

            // опасный пол
            foreach (var h in sim.Hazards)
            {
                var col = h.Type == "Water" ? Color.Lerp(pal.Hazard, new Color(0.2f, 0.5f, 0.6f), 0.5f) : pal.Hazard;
                Mesh(root, MeshKit.Polygon(h.Poly, -0.02f), Art.LitMat(col, 1.2f), false);
                Mesh(root, MeshKit.Slab(h.Poly, -0.02f, -0.6f), Art.LitMat(Color.Lerp(col, Color.black, 0.6f)), false);
            }

            // декор без столкновений
            var decorMat = Art.LitMat(Color.Lerp(pal.Wall, pal.WallTop, 0.5f));
            foreach (var d in room.decor)
                Art.Part(root, MeshKit.Ico(1, 0.1f, rnd.Next(99)), decorMat, Art.W(d.pos) + Vector3.up * (d.h * 0.4f), new Vector3(d.r * 2f, Mathf.Max(0.3f, d.h), d.r * 2f), default, false);

            // бездна под комнатой
            var abyss = Art.Part(root, MeshKit.Box(), Art.LitMat(pal.Void), new Vector3((room.min.x + room.max.x) * 0.5f, -3f, (room.min.y + room.max.y) * 0.5f),
                new Vector3(size.x + 80f, 0.5f, size.y + 80f), default, false);
            abyss.name = "Abyss";
            return root;
        }

        /// <summary>Арена из конфига — ночной город в духе Hades: тёмная брусчатка, кварталы домов вдоль улиц, жаровни.
        /// Невидимые стены Hades (_Impassable) — тёмные цоколи, за ними дома; вода и обрывы — бездна.
        /// Здания, стены, колонны, бочки, ящики, амфоры, обломки — на своих местах и в размер своих многоугольников.
        /// Между площадкой и камерой всё низкое, чтобы не закрывать бой.</summary>
        static Transform BuildLayout(ArenaLayout a)
        {
            var root = new GameObject("Room " + a.id).transform;
            var rnd = new System.Random(StableHash(a.id));
            float R(float lo, float hi) => lo + (float)rnd.NextDouble() * (hi - lo);
            var center = (a.boundsMin + a.boundsMax) * 0.5f;

            Art.Part(root, MeshKit.Box(), Art.LitMat(Art.Night.Ground), new Vector3(center.x, -0.26f, center.y), new Vector3(140f, 0.5f, 140f), default, false);

            // брусчатка
            var flags = System.Array.ConvertAll(Art.Night.Flags, c => Art.LitMat(c));
            for (int row = 0; row < a.floor.Count; row++)
            for (int col = 0; col < a.floor[row].Length; col++)
            {
                if (!a.IsFloorCell(col, row)) continue;
                var c = new Vector3(a.floorOrigin.x + col + 0.5f, 0f, a.floorOrigin.y + row + 0.5f);
                if (rnd.NextDouble() < 0.03) continue; // выбитая плита
                float h = R(0.02f, 0.07f);
                Art.Part(root, MeshKit.Box(), flags[rnd.Next(flags.Length)], c + Vector3.up * (h * 0.5f - 0.02f), new Vector3(0.94f, h, 0.94f), new Vector3(0f, R(-3f, 3f), 0f), false);
            }

            var plinth = Art.LitMat(Art.Night.Plinth);
            var abyss = Art.LitMat(Art.Night.Abyss);
            var abyssRim = Art.LitMat(Art.Night.AbyssGlow, 0.6f);
            var plaster = Art.LitMat(Art.Night.Plaster);
            var roof = Art.LitMat(Art.Night.Roof);
            var masonry = Art.LitMat(Art.Night.Masonry);
            var marble = Art.LitMat(Art.Night.Marble);
            var wood = Art.LitMat(Art.Night.Wood);
            var terracotta = Art.LitMat(Art.Night.Terracotta);
            var deadWood = Art.LitMat(Art.Night.DeadWood);
            foreach (var w in a.walls)
            {
                var c2 = Centroid(w.points);
                var c = new Vector3(c2.x, 0f, c2.y);
                var size = w.Max - w.Min;
                float rad = Mathf.Max(size.x, size.y) * 0.5f;
                bool front = InFrontOfFloor(a, c2);
                // как в Hades: за краем площадки — тишина; предметы дальше 3 юнитов от пола не рисуются
                if (w.kind is not ("wall" or "gap") && FloorDistance(a, c2, 4f, out _) > 3f) continue;
                switch (w.kind)
                {
                    case "wall": // невидимая стена Hades — цоколь квартала
                        Solid(root, MeshKit.Extrude(w.points, R(0.18f, 0.26f)), plinth, false);
                        break;
                    case "gap": // вода, обрыв — бездна с лиловым отсветом по краю
                        Solid(root, MeshKit.Extrude(w.points, 0.015f, false, true), abyss, false);
                        Solid(root, MeshKit.Extrude(w.points, 0.03f, true, false), abyssRim, false);
                        break;
                    case "building":
                    {
                        float h = front ? 0.6f : Mathf.Clamp(w.height, 1.8f, 3.2f);
                        Solid(root, MeshKit.Extrude(w.points, h, true, false), plaster, true);
                        Solid(root, MeshKit.Extrude(w.points, h, false, true), roof, false);
                        break;
                    }
                    case "masonry":
                        Solid(root, MeshKit.Extrude(w.points, front ? 0.45f : Mathf.Clamp(w.height, 0.6f, 1.4f)), masonry, true);
                        break;
                    case "column":
                        Column(root, c, Mathf.Clamp(rad, 0.2f, 0.45f), front ? 0.5f : 2.4f, marble);
                        break;
                    case "barrel":
                        Art.Part(root, MeshKit.Prism(10, 0.9f), wood, c, new Vector3(rad * 1.7f, 0.7f, rad * 1.7f), new Vector3(0f, R(0f, 40f), 0f));
                        Art.Part(root, MeshKit.Prism(10), Art.LitMat(Art.Night.Iron), c + Vector3.up * 0.45f, new Vector3(rad * 1.75f, 0.05f, rad * 1.75f));
                        break;
                    case "crate":
                        Art.Part(root, MeshKit.Box(), wood, c + Vector3.up * 0.3f, new Vector3(Mathf.Max(0.4f, size.x * 0.85f), 0.6f, Mathf.Max(0.4f, size.y * 0.85f)), new Vector3(0f, R(-4f, 4f), 0f));
                        break;
                    case "vase":
                        Art.Part(root, MeshKit.Prism(7, 0.55f), terracotta, c, new Vector3(rad * 1.6f, 0.7f, rad * 1.6f), new Vector3(0f, R(0f, 50f), 0f));
                        Art.Part(root, MeshKit.Prism(7, 1.3f), terracotta, c + Vector3.up * 0.7f, new Vector3(rad * 0.7f, 0.12f, rad * 0.7f));
                        break;
                    case "tree": // мёртвое дерево
                        DeadTree(root, c, front ? 0.45f : Mathf.Clamp(rad * 1.4f, 0.8f, 1.5f), deadWood, rnd);
                        break;
                    case "bush": // сухой куст
                        for (int i = 0; i < 4; i++)
                            Art.Part(root, MeshKit.Prism(4, 0f), deadWood, c + new Vector3(R(-0.2f, 0.2f), 0f, R(-0.2f, 0.2f)) * rad,
                                new Vector3(0.06f, R(0.4f, 0.8f) * Mathf.Clamp(rad, 0.5f, 1f), 0.06f), new Vector3(R(-35f, 35f), R(0f, 180f), R(-35f, 35f)));
                        break;
                    case "door": // вход и выход Hades — низкий порог, чтобы не закрывать героя на старте
                        Solid(root, MeshKit.Extrude(w.points, 0.15f), masonry, true);
                        break;
                    default: // rubble: обломки, кучи
                    {
                        int n = Mathf.Clamp(Mathf.CeilToInt(rad * 2f), 1, 4);
                        for (int i = 0; i < n; i++)
                        {
                            var p = c + new Vector3(R(-0.5f, 0.5f) * size.x, 0f, R(-0.5f, 0.5f) * size.y) * 0.6f;
                            float s = Mathf.Clamp(rad, 0.25f, 0.7f) * R(0.6f, 1.1f);
                            Art.Part(root, MeshKit.Ico(0, 0.3f, rnd.Next(99)), masonry, p + Vector3.up * (s * 0.25f), new Vector3(s, s * 0.6f, s));
                        }
                        break;
                    }
                }
            }

            StreetFronts(a, root, rnd);
            Braziers(a, root, rnd);
            return root;
        }

        /// <summary>Дома вдоль улиц: на клетках без пола рядом с площадкой, фасадом (окна, дверь) к улице.
        /// Со стороны камеры — только низкий парапет.</summary>
        static void StreetFronts(ArenaLayout a, Transform root, System.Random rnd)
        {
            float R(float lo, float hi) => lo + (float)rnd.NextDouble() * (hi - lo);
            var taken = new System.Collections.Generic.HashSet<Vector2Int>();
            var cand = new System.Collections.Generic.List<(Vector2 p, Vector2 toFloor, float dist)>();
            for (int z = Mathf.FloorToInt(a.boundsMin.y) - 7; z <= Mathf.CeilToInt(a.boundsMax.y) + 7; z++)
            for (int x = Mathf.FloorToInt(a.boundsMin.x) - 7; x <= Mathf.CeilToInt(a.boundsMax.x) + 7; x++)
            {
                var p = new Vector2(x + 0.5f, z + 0.5f);
                if (IsFloor(a, p) || InGap(a, p)) continue;
                float d = FloorDistance(a, p, 3f, out var dir);
                if (d > 1.6f) continue; // только ряд вдоль улицы
                cand.Add((p, dir, d));
            }
            cand.Sort((u, v) => u.dist.CompareTo(v.dist));
            var plaster = new[] { Art.LitMat(Art.Night.Plaster), Art.LitMat(Art.Night.PlasterDark) };
            var roofs = new[] { Art.LitMat(Art.Night.Roof), Art.LitMat(Art.Night.RoofDark) };
            var masonry = Art.LitMat(Art.Night.Masonry);
            var lit = Art.LitMat(Art.Night.Window, 2.2f);
            var dark = Art.LitMat(new Color(0.05f, 0.04f, 0.05f));
            foreach (var (p, dir, dist) in cand)
            {
                if (taken.Contains(Cell(p)) || rnd.NextDouble() < 0.35) continue; // с просветами, не сплошной стеной
                bool front = InFrontOfFloor(a, p);
                if (front && dist > 1.6f) continue; // перед площадкой высокое ставить нельзя
                // фасад смотрит на ближайший пол; дом — прямоугольник по осям, отодвинутый от улицы
                bool alongX = Mathf.Abs(dir.y) > Mathf.Abs(dir.x);
                float wid = front ? R(1f, 2f) : R(1.8f, 3.2f), dep = front ? 0.6f : R(1.8f, 3f);
                var half = alongX ? new Vector2(wid, dep) * 0.5f : new Vector2(dep, wid) * 0.5f;
                var face = alongX ? new Vector2(0f, Mathf.Sign(dir.y)) : new Vector2(Mathf.Sign(dir.x), 0f);
                var ctr = p - face * (half.x * Mathf.Abs(face.x) + half.y * Mathf.Abs(face.y) - 0.45f);
                if (!FootprintFree(a, taken, ctr, half)) continue;
                Take(taken, ctr, half);
                var c3 = new Vector3(ctr.x, 0f, ctr.y);
                if (front)
                {
                    Art.Part(root, MeshKit.Box(), masonry, c3 + Vector3.up * 0.22f, new Vector3(half.x * 2f, 0.45f, half.y * 2f));
                    continue;
                }
                float h = R(1.3f, 2.1f);
                Art.Part(root, MeshKit.Box(), plaster[rnd.Next(2)], c3 + Vector3.up * (h * 0.5f), new Vector3(half.x * 2f, h, half.y * 2f));
                // вальмовая крыша: пирамида из Prism(4), повёрнутая на 45° к осям
                float rh = R(0.5f, 0.9f);
                Art.Part(root, MeshKit.Prism(4, 0f), roofs[rnd.Next(2)], c3 + Vector3.up * h, new Vector3(half.x * 2.9f, rh, half.y * 2.9f), new Vector3(0f, 45f, 0f));
                if (dist >= 1.6f) continue;
                // фасад к улице: дверь и окна, часть окон светится
                var f3 = new Vector3(face.x, 0f, face.y);
                var side = new Vector3(-face.y, 0f, face.x);
                float faceOff = half.x * Mathf.Abs(face.x) + half.y * Mathf.Abs(face.y) + 0.02f;
                float span = alongX ? half.x : half.y;
                var front3 = c3 + f3 * faceOff;
                var flat = new Vector3(Mathf.Abs(face.x) > 0f ? 0.05f : 1f, 1f, Mathf.Abs(face.y) > 0f ? 0.05f : 1f);
                Art.Part(root, MeshKit.Box(), dark, front3 + side * R(-span * 0.4f, span * 0.4f) + Vector3.up * 0.55f, Vector3.Scale(flat, new Vector3(0.55f, 1.1f, 0.55f)), default, false);
                int windows = span > 1.2f ? 2 : 1;
                for (int i = 0; i < windows; i++)
                {
                    float off = windows == 1 ? 0f : (i == 0 ? -0.5f : 0.5f) * span;
                    Art.Part(root, MeshKit.Box(), rnd.NextDouble() < 0.15 ? lit : dark, front3 + side * off + Vector3.up * (h * 0.72f),
                        Vector3.Scale(flat, new Vector3(0.32f, 0.42f, 0.32f)), default, false);
                }
            }
        }

        /// <summary>Жаровни вдоль края площадки, не чаще чем через 6 юнитов: огонь и тёплый точечный свет.</summary>
        static void Braziers(ArenaLayout a, Transform root, System.Random rnd)
        {
            var placed = new System.Collections.Generic.List<Vector2>();
            var iron = Art.LitMat(Art.Night.Iron);
            var fire = Art.LitMat(Art.Night.Fire, 1.6f);
            for (int z = Mathf.FloorToInt(a.boundsMin.y) - 2; z <= Mathf.CeilToInt(a.boundsMax.y) + 2; z++)
            for (int x = Mathf.FloorToInt(a.boundsMin.x) - 2; x <= Mathf.CeilToInt(a.boundsMax.x) + 2; x++)
            {
                if (placed.Count >= 8) return;
                var p = new Vector2(x + 0.5f, z + 0.5f);
                if (IsFloor(a, p) || InGap(a, p) || FloorDistance(a, p, 2f, out _) > 1.1f) continue;
                if (rnd.NextDouble() < 0.5 || placed.Exists(q => (q - p).sqrMagnitude < 81f)) continue;
                placed.Add(p);
                var c = new Vector3(p.x, 0f, p.y);
                Art.Part(root, MeshKit.Prism(6, 0.6f), iron, c, new Vector3(0.22f, 0.85f, 0.22f));
                Art.Part(root, MeshKit.Prism(8, 1.5f), iron, c + Vector3.up * 0.85f, new Vector3(0.4f, 0.16f, 0.4f));
                var flame = Art.Part(root, MeshKit.Ico(0, 0.2f, rnd.Next(99)), fire, c + Vector3.up * 1.08f, new Vector3(0.36f, 0.42f, 0.36f), default, false);
                var lightGo = new GameObject("BrazierLight");
                lightGo.transform.SetParent(root, false);
                lightGo.transform.localPosition = c + Vector3.up * 1.4f;
                var l = lightGo.AddComponent<Light>();
                l.type = LightType.Point; l.color = Art.Night.Fire; l.range = 7.5f; l.intensity = 3f; l.shadows = LightShadows.None;
                var fl = lightGo.AddComponent<Flicker>();
                fl.Flame = flame.transform; fl.Seed = (float)rnd.NextDouble() * 10f;
            }
        }

        static void Column(Transform root, Vector3 c, float r, float h, Material mat)
        {
            Art.Part(root, MeshKit.Box(), mat, c + Vector3.up * 0.08f, new Vector3(r * 2.6f, 0.16f, r * 2.6f));
            Art.Part(root, MeshKit.Prism(8), mat, c + Vector3.up * 0.16f, new Vector3(r * 2f, h - 0.32f, r * 2f));
            if (h > 1f) Art.Part(root, MeshKit.Box(), mat, c + Vector3.up * (h - 0.08f), new Vector3(r * 2.6f, 0.16f, r * 2.6f));
        }

        static void DeadTree(Transform root, Vector3 pos, float s, Material mat, System.Random rnd)
        {
            float R(float lo, float hi) => lo + (float)rnd.NextDouble() * (hi - lo);
            Art.Part(root, MeshKit.Prism(5, 0.4f), mat, pos, new Vector3(0.28f * s, 1.6f * s, 0.28f * s), new Vector3(0f, R(0f, 70f), R(-5f, 5f)));
            for (int i = 0; i < 4; i++)
                Art.Part(root, MeshKit.Prism(4, 0.2f), mat, pos + Vector3.up * (R(0.8f, 1.4f) * s), new Vector3(0.09f * s, R(0.6f, 1f) * s, 0.09f * s),
                    new Vector3(R(30f, 60f), i * 90f + R(-20f, 20f), 0f));
        }

        static Vector2Int Cell(Vector2 p) => new(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y));

        static bool IsFloor(ArenaLayout a, Vector2 p) =>
            a.IsFloorCell(Mathf.FloorToInt(p.x - a.floorOrigin.x), Mathf.FloorToInt(p.y - a.floorOrigin.y));

        static bool InGap(ArenaLayout a, Vector2 p) => a.walls.Exists(w => w.kind == "gap" && w.Contains(p));

        /// <summary>Расстояние до ближайшей клетки пола (по центрам, не дальше max) и направление к ней.</summary>
        static float FloorDistance(ArenaLayout a, Vector2 p, float max, out Vector2 dir)
        {
            float best = float.MaxValue; dir = Vector2.zero;
            int m = Mathf.CeilToInt(max);
            for (int dz = -m; dz <= m; dz++)
            for (int dx = -m; dx <= m; dx++)
            {
                var q = p + new Vector2(dx, dz);
                if (!IsFloor(a, q)) continue;
                float d = new Vector2(dx, dz).sqrMagnitude;
                if (d < best) { best = d; dir = new Vector2(dx, dz); }
            }
            if (dir.sqrMagnitude > 0f) dir.Normalize();
            return Mathf.Sqrt(best);
        }

        static bool FootprintFree(ArenaLayout a, System.Collections.Generic.HashSet<Vector2Int> taken, Vector2 c, Vector2 half)
        {
            for (float x = -half.x + 0.25f; x <= half.x - 0.25f + 0.001f; x += 0.5f)
            for (float z = -half.y + 0.25f; z <= half.y - 0.25f + 0.001f; z += 0.5f)
            {
                var q = c + new Vector2(x, z);
                if (IsFloor(a, q) || InGap(a, q) || taken.Contains(Cell(q))) return false;
            }
            return true;
        }

        static void Take(System.Collections.Generic.HashSet<Vector2Int> taken, Vector2 c, Vector2 half)
        {
            for (float x = -half.x + 0.25f; x <= half.x - 0.25f + 0.001f; x += 0.5f)
            for (float z = -half.y + 0.25f; z <= half.y - 0.25f + 0.001f; z += 0.5f)
                taken.Add(Cell(c + new Vector2(x, z)));
        }

        static int StableHash(string s)
        {
            int h = 17;
            foreach (char ch in s) h = h * 31 + ch;
            return h;
        }

        /// <summary>Точка ближе к камере, чем пол за ней: вдоль «вверх по экрану» (1, 1)/√2 в пределах 6 юнитов есть пол.</summary>
        static bool InFrontOfFloor(ArenaLayout a, Vector2 p)
        {
            var up = new Vector2(0.7071f, 0.7071f);
            for (float t = 0.5f; t <= 6f; t += 0.5f)
                if (IsFloor(a, p + up * t)) return true;
            return false;
        }

        static Vector2 Centroid(System.Collections.Generic.List<Vector2> pts)
        {
            var s = Vector2.zero;
            foreach (var p in pts) s += p;
            return s / Mathf.Max(1, pts.Count);
        }

        static void Solid(Transform root, Mesh mesh, Material mat, bool shadows) =>
            Art.Part(root, mesh, mat, Vector3.zero, Vector3.one, default, shadows);

        static void Tree(Transform root, Vector3 pos, float s, System.Random rnd)
        {
            var t = new GameObject("Tree").transform;
            t.SetParent(root, false); t.localPosition = pos;
            Art.Part(t, MeshKit.Prism(6, 0.7f), Art.LitMat(Art.Trunk), Vector3.zero, new Vector3(0.35f * s, 1.3f * s, 0.35f * s));
            var leaf = Art.LitMat(rnd.NextDouble() < 0.25 ? Art.FoliageWarm : Art.Foliage);
            Art.Part(t, MeshKit.Ico(1, 0.12f, rnd.Next(99)), leaf, new Vector3(0f, 1.9f * s, 0f), Vector3.one * (1.9f * s));
            Art.Part(t, MeshKit.Ico(1, 0.12f, rnd.Next(99)), leaf, new Vector3(0.5f * s, 1.5f * s, 0.2f * s), Vector3.one * (1.2f * s));
            Art.Part(t, MeshKit.Ico(1, 0.12f, rnd.Next(99)), leaf, new Vector3(-0.4f * s, 2.5f * s, -0.2f * s), Vector3.one * (1.1f * s));
        }

        static void Bush(Transform root, Vector3 pos, float s, System.Random rnd)
        {
            var mat = Art.LitMat(rnd.NextDouble() < 0.5 ? Art.GrassDark : Art.Moss);
            Art.Part(root, MeshKit.Ico(1, 0.15f, rnd.Next(99)), mat, pos + Vector3.up * (0.3f * s), new Vector3(1.2f * s, 0.8f * s, 1.1f * s));
            Art.Part(root, MeshKit.Ico(1, 0.15f, rnd.Next(99)), mat, pos + new Vector3(0.5f * s, 0.25f * s, 0.3f * s), Vector3.one * (0.7f * s));
        }

        static void Ruin(Transform root, Vector3 pos, System.Random rnd)
        {
            var stone = Art.LitMat(Art.Stone);
            float h = 0.6f + (float)rnd.NextDouble() * 1.6f;
            Art.Part(root, MeshKit.Box(), stone, pos + Vector3.up * (h * 0.5f), new Vector3(0.8f, h, 0.8f), new Vector3(0f, (float)rnd.NextDouble() * 90f, 0f));
            Art.Part(root, MeshKit.Ico(0, 0.2f, rnd.Next(99)), Art.LitMat(Art.Moss), pos + Vector3.up * h, new Vector3(0.9f, 0.3f, 0.9f));
        }

        const float MaxWall = 1.8f, FrontWall = 0.4f;

        static bool OnHazard(ArenaSim sim, Vector2 p)
        {
            foreach (var h in sim.Hazards) if (Geo.InPoly(h.Poly, p)) return true;
            return false;
        }

        /// <summary>Стена ближнего края: за ней (дальше от камеры) пол комнаты, а перед ней (к камере) пола нет.
        /// Колонны посреди комнаты под это не попадают — перед ними тоже пол.</summary>
        static bool OccludesFloor(ArenaSim sim, Obstacle o, Vector2 camFwd)
        {
            bool behind = false;
            for (float d = 0.6f; d <= 3f && !behind; d += 0.6f)
                if (sim.InsideRoom(o.Pos + camFwd * (o.R + d), 0f)) behind = true;
            if (!behind) return false;
            for (float d = 0.6f; d <= 3f; d += 0.6f)
                if (sim.InsideRoom(o.Pos - camFwd * (o.R + d), 0f)) return false;
            return true;
        }

        static void Mesh(Transform root, Mesh mesh, Material mat, bool shadows)
        {
            var go = new GameObject(mesh.name);
            go.transform.SetParent(root, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
        }
    }

    /// <summary>Урны (разбиваются ударом) и двери (загораются, когда комната пройдена).</summary>
    public sealed class RoomProps
    {
        readonly ArenaSim sim;
        readonly List<(Breakable b, GameObject go)> urns = new();
        readonly List<Material> doorMats = new();
        readonly Color doorColor, doorClosed;

        public RoomProps(ArenaSim sim, Transform parent)
        {
            this.sim = sim;
            var pal = RoomView.For(sim.Room.biome);
            var urn = Art.LitMat(Color.Lerp(pal.Accent, pal.Wall, 0.5f));
            foreach (var b in sim.Breakables)
                urns.Add((b, Art.Part(parent, MeshKit.Prism(7, 0.7f), urn, Art.W(b.Pos), new Vector3(b.R * 2f, 0.6f, b.R * 2f))));
            doorColor = pal.Accent;
            doorClosed = Color.Lerp(pal.Wall, Color.black, 0.4f);
            foreach (var e in sim.Room.exits)
            {
                var p = Art.W(e.pos);
                // перекладина двери — неосвещаемая: тёмная, пока закрыто, и ярче 1 (bloom), когда открыто
                var lit = new Material(Art.LitMat(Color.Lerp(pal.Wall, Color.black, 0.2f), 0.01f));
                lit.SetColor("_BaseColor", Color.Lerp(pal.Wall, Color.black, 0.4f));
                doorMats.Add(lit);
                var d = new GameObject("Door").transform;
                d.SetParent(parent, false);
                d.position = p;
                d.rotation = Quaternion.Euler(0f, -e.angle, 0f);
                Art.Part(d, MeshKit.Box(), lit, new Vector3(0f, 1.25f, 0f), new Vector3(1.7f, 0.25f, 0.35f)).GetComponent<Renderer>().sharedMaterial = lit;
                Art.Part(d, MeshKit.Box(), Art.LitMat(pal.Wall), new Vector3(-0.75f, 0.6f, 0f), new Vector3(0.3f, 1.2f, 0.3f));
                Art.Part(d, MeshKit.Box(), Art.LitMat(pal.Wall), new Vector3(0.75f, 0.6f, 0f), new Vector3(0.3f, 1.2f, 0.3f));
            }
        }

        public void Sync(float time)
        {
            foreach (var (b, go) in urns) if (go.activeSelf == b.Broken) go.SetActive(!b.Broken);
            // открытые двери светятся цветом биома и пульсируют — туда идти дальше
            foreach (var m in doorMats)
            {
                m.SetColor("_BaseColor", sim.DoorsOpen ? Art.Glow(doorColor, 1.2f + 0.6f * Mathf.Sin(time * 4f)) : doorClosed);
            }
        }
    }

    /// <summary>Ловушки комнаты. Шипы выскакивают из площадки; статуи, топоры, пилы и лучники в замахе белеют (PreAttackFlash);
    /// газ и яд перед выбросом показывают растущий круг облака (LobWarningDecal); взрывной куб с горящим фитилём —
    /// красный круг ровно радиуса взрыва (BlastWarningDecal).</summary>
    public sealed class TrapViews
    {
        sealed class V
        {
            public Trap T;
            public Transform Root, Moving;
            public GameObject Warn, Cloud;
            public Material WarnMat, CloudMat;
            public Material[] Mats;
            public Color[] Base;
        }

        readonly List<V> views = new();
        readonly ArenaSim sim;

        public TrapViews(ArenaSim sim, Transform parent)
        {
            this.sim = sim;
            var pad = Art.LitMat(new Color(0.35f, 0.32f, 0.36f));
            var metal = Art.LitMat(new Color(0.75f, 0.74f, 0.8f));
            var marble = Art.LitMat(new Color(0.88f, 0.86f, 0.8f));
            var bronze = Art.LitMat(new Color(0.72f, 0.52f, 0.3f));
            foreach (var t in sim.Traps)
            {
                var v = new V { T = t, Root = new GameObject(t.Type).transform };
                v.Root.SetParent(parent, false);
                v.Root.position = Art.W(t.Pos);
                var face = Quaternion.LookRotation(new Vector3(t.Dir.x, 0f, t.Dir.y));
                switch (t.Kind)
                {
                    case TrapKind.Spike:
                    case TrapKind.DartPlate:
                    case TrapKind.AxeTrigger:
                        PadMesh(v, t, pad);
                        if (t.Kind == TrapKind.Spike)
                        {
                            v.Moving = new GameObject("Spikes").transform;
                            v.Moving.SetParent(v.Root, false);
                            float r = PadRadius(t);
                            int n = Mathf.Clamp(Mathf.RoundToInt(r * 4f), 3, 7);
                            for (int i = 0; i < n; i++)
                            for (int k = 0; k < n; k++)
                            {
                                var lp = new Vector3((i + 0.5f) / n * 2f - 1f, 0f, (k + 0.5f) / n * 2f - 1f) * r * 0.75f;
                                if (t.Poly != null && !Geo.InPoly(t.Poly, t.Pos + new Vector2(lp.x, lp.z))) continue;
                                Art.Part(v.Moving, MeshKit.Prism(4, 0f), metal, lp, new Vector3(0.12f, 0.35f, 0.12f), default, false);
                            }
                            v.Moving.localPosition = new Vector3(0f, -0.3f, 0f);
                        }
                        else Art.Part(v.Root, MeshKit.Prism(6), Art.LitMat(new Color(0.75f, 0.6f, 0.3f), 0.3f), new Vector3(0f, 0.03f, 0f), new Vector3(0.5f, 0.04f, 0.5f), default, false);
                        break;
                    case TrapKind.DartEmitter:
                        Art.Part(v.Root, MeshKit.Box(), bronze, new Vector3(0f, 0.5f, 0f), new Vector3(0.5f, 1f, 0.5f), face.eulerAngles);
                        break;
                    case TrapKind.Axe:
                        Art.Part(v.Root, MeshKit.Prism(6), Art.LitMat(new Color(0.3f, 0.26f, 0.3f)), new Vector3(0f, 0.6f, 0f), new Vector3(0.25f, 1.2f, 0.25f));
                        v.Moving = new GameObject("Blade").transform;
                        v.Moving.SetParent(v.Root, false);
                        v.Moving.localPosition = new Vector3(0f, 1.1f, 0f);
                        Art.Part(v.Moving, MeshKit.Box(), metal, new Vector3(0.75f, 0f, 0f), new Vector3(1.3f, 0.12f, 0.7f));
                        break;
                    case TrapKind.BlastCube:
                        Art.Part(v.Root, MeshKit.Box(), Art.LitMat(new Color(0.85f, 0.25f, 0.18f), 0.4f), new Vector3(0f, 0.3f, 0f), Vector3.one * 0.6f);
                        v.WarnMat = Art.DecalInstance(new Color(1f, 0.1f, 0.05f, 0.7f), 1, 4);
                        v.Warn = Art.Decal(v.Root, MeshKit.FloorQuad(), v.WarnMat, 0.03f);
                        v.Warn.transform.localScale = Vector3.one * ((t.P.radius > 0f ? t.P.radius : Arena.Core.Hades.U(400f)) * 2f);
                        break;
                    case TrapKind.Gas:
                    case TrapKind.Poison:
                    {
                        var col = t.Kind == TrapKind.Gas ? new Color(0.55f, 0.75f, 0.35f) : new Color(0.35f, 0.85f, 0.45f);
                        if (t.Poly != null) PadMesh(v, t, Art.LitMat(new Color(0.18f, 0.16f, 0.14f)));
                        else Art.Part(v.Root, MeshKit.Prism(7), Art.LitMat(new Color(0.18f, 0.16f, 0.14f)), Vector3.zero, new Vector3(1f, 0.05f, 0.6f), default, false);
                        float d = sim.GasRadius(t) * 2f;
                        v.WarnMat = Art.DecalInstance(new Color(col.r, col.g, col.b, 0.6f), 1, 4);
                        v.Warn = Art.Decal(v.Root, MeshKit.FloorQuad(), v.WarnMat, 0.04f);
                        v.Warn.transform.localScale = Vector3.one * d;
                        v.CloudMat = Art.DecalInstance(new Color(col.r, col.g, col.b, 0.35f), 3, 5);
                        v.Cloud = Art.Decal(v.Root, MeshKit.FloorQuad(), v.CloudMat, 0.3f);
                        v.Cloud.transform.localScale = Vector3.one * d;
                        break;
                    }
                    case TrapKind.Phalanx:
                        Art.Part(v.Root, MeshKit.Prism(6), marble, new Vector3(0f, 0.15f, 0f), new Vector3(0.9f, 0.3f, 0.9f));
                        Art.Part(v.Root, MeshKit.Prism(8, 0.7f), marble, new Vector3(0f, 0.3f, 0f), new Vector3(0.55f, 1.3f, 0.5f), face.eulerAngles);
                        v.Moving = new GameObject("Spear").transform;
                        v.Moving.SetParent(v.Root, false);
                        v.Moving.localRotation = face;
                        v.Moving.localPosition = new Vector3(0f, 0.9f, 0f);
                        Art.Part(v.Moving, MeshKit.Prism(4, 0f), bronze, new Vector3(0f, 0f, 0.5f), new Vector3(0.08f, 1.1f, 0.08f), new Vector3(90f, 0f, 0f));
                        break;
                    case TrapKind.Saw:
                    case TrapKind.Archer:
                        Art.Part(v.Root, MeshKit.Prism(6), t.Kind == TrapKind.Saw ? Art.LitMat(new Color(0.35f, 0.22f, 0.24f)) : marble, new Vector3(0f, 0.5f, 0f), new Vector3(0.9f, 1f, 0.9f));
                        Art.Part(v.Root, MeshKit.Box(), bronze, new Vector3(0f, 0.6f, 0f) + new Vector3(t.Dir.x, 0f, t.Dir.y) * 0.45f, new Vector3(0.35f, 0.35f, 0.35f), face.eulerAngles);
                        break;
                    default:
                        break;
                }
                var rs = v.Root.GetComponentsInChildren<Renderer>();
                var mats = new List<Material>();
                foreach (var r in rs)
                {
                    if (v.Warn != null && r.gameObject == v.Warn) continue;
                    if (v.Cloud != null && r.gameObject == v.Cloud) continue;
                    mats.Add(r.material);
                }
                v.Mats = mats.ToArray();
                v.Base = new Color[v.Mats.Length];
                for (int i = 0; i < v.Mats.Length; i++) v.Base[i] = v.Mats[i].GetColor("_BaseColor");
                views.Add(v);
            }
        }

        static float PadRadius(Trap t)
        {
            float r = 0.4f;
            if (t.Poly != null) foreach (var p in t.Poly) r = Mathf.Max(r, (p - t.Pos).magnitude);
            return r;
        }

        static void PadMesh(V v, Trap t, Material mat)
        {
            if (t.Poly == null)
            {
                Art.Part(v.Root, MeshKit.Box(), mat, new Vector3(0f, 0.02f, 0f), new Vector3(1.2f, 0.06f, 1.2f), default, false);
                return;
            }
            var local = new Vector2[t.Poly.Length];
            for (int i = 0; i < local.Length; i++) local[i] = t.Poly[i] - t.Pos;
            var go = new GameObject("Pad");
            go.transform.SetParent(v.Root, false);
            go.AddComponent<MeshFilter>().sharedMesh = MeshKit.Slab(local, 0.05f, -0.02f);
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        }

        public void Sync(float time)
        {
            foreach (var v in views)
            {
                var t = v.T;
                v.Root.gameObject.SetActive(t.Phase != TrapPhase.Gone);
                float warn = t.Warn;
                // белая вспышка замаха у статуй, топоров, пил и лучников
                bool flashes = t.Kind is TrapKind.Phalanx or TrapKind.Axe or TrapKind.Saw or TrapKind.Archer;
                float white = flashes ? 0.8f * warn : 0f;
                for (int i = 0; i < v.Mats.Length; i++) v.Mats[i].SetColor("_BaseColor", Color.Lerp(v.Base[i], Color.white, white));
                switch (t.Kind)
                {
                    case TrapKind.Spike:
                    {
                        // площадка щёлкает (0.2 с), шипы выскакивают и уходят за время перезарядки
                        float y = -0.3f;
                        if (t.Phase == TrapPhase.Pre) y = -0.22f;
                        else if (t.Phase == TrapPhase.Cooldown) y = Mathf.Lerp(-0.3f, 0.05f, Mathf.Clamp01((t.T - (t.Total - 0.8f)) / 0.8f));
                        v.Moving.localPosition = new Vector3(0f, y, 0f);
                        break;
                    }
                    case TrapKind.BlastCube:
                    {
                        bool lit = t.Phase == TrapPhase.Pre;
                        v.Warn.SetActive(lit);
                        float pulse = Mathf.Repeat(time * 6f, 1f) < 0.5f ? 1f : 0.55f;
                        if (lit) v.WarnMat.SetColor("_BaseColor", new Color(1f, 0.12f, 0.05f, 0.75f * pulse));
                        v.Root.localScale = Vector3.one * (lit ? 1f + 0.08f * pulse : t.Phase == TrapPhase.Cooldown ? 0.6f : 1f);
                        break;
                    }
                    case TrapKind.Gas:
                    case TrapKind.Poison:
                    {
                        // LobWarningDecal: появляется за 0.1 с, заполняется к выбросу
                        bool pre = t.Phase == TrapPhase.Pre;
                        v.Warn.SetActive(pre);
                        if (pre)
                        {
                            var c = v.WarnMat.GetColor("_BaseColor");
                            c.a = 0.25f + 0.55f * warn;
                            v.WarnMat.SetColor("_BaseColor", c);
                        }
                        v.Cloud.SetActive(t.Cloud > 0f);
                        if (t.Cloud > 0f)
                        {
                            var c = v.CloudMat.GetColor("_BaseColor");
                            c.a = 0.4f * Mathf.Clamp01(t.Cloud / 1f);
                            v.CloudMat.SetColor("_BaseColor", c);
                        }
                        break;
                    }
                    case TrapKind.Axe:
                        if (t.Phase == TrapPhase.Post) v.Moving.localRotation = Quaternion.Euler(0f, Mathf.Repeat(time * 900f, 360f), 0f);
                        else v.Moving.localRotation = Quaternion.Euler(0f, t.Phase == TrapPhase.Pre ? -30f * warn : 0f, 0f);
                        break;
                    case TrapKind.Phalanx:
                        v.Moving.localPosition = new Vector3(0f, 0.9f, 0f) + v.Moving.localRotation * Vector3.forward * (t.Phase == TrapPhase.Fire ? 1.1f : t.Phase == TrapPhase.Pre ? -0.2f * warn : 0f);
                        break;
                }
            }
        }
    }
}
