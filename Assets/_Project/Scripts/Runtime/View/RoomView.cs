using Arena.Core;
using Arena.Sim;
using UnityEngine;

namespace Arena.View
{
    /// <summary>Комната в духе TUNIC: каменная площадка, трава, круглые деревья, кусты, руины, цветы.
    /// Арена из Hades II строится по своему конфигу как ночной город (см. BuildLayout).</summary>
    public static class RoomView
    {
        public static Transform Build(ArenaSim sim)
        {
            if (sim.Layout != null) return BuildLayout(sim.Layout);
            var root = new GameObject("Room").transform;
            var rnd = new System.Random(7);
            float R(float a, float b) => a + (float)rnd.NextDouble() * (b - a);
            float hw = sim.ArenaCfg.halfWidth, hd = sim.ArenaCfg.halfDepth;

            // трава до горизонта
            Art.Part(root, MeshKit.Box(), Art.LitMat(Art.Grass), new Vector3(0f, -0.26f, 0f), new Vector3(90f, 0.5f, 90f), default, false);

            // каменные плиты площадки с зазорами и разными оттенками
            var stones = new[] { Art.LitMat(Art.Stone), Art.LitMat(Art.StoneDark), Art.LitMat(Color.Lerp(Art.Stone, Art.StoneDark, 0.5f)), Art.LitMat(new Color(0.84f, 0.80f, 0.70f)) };
            for (float x = -hw; x < hw - 0.01f; x += 1f)
            for (float z = -hd; z < hd - 0.01f; z += 1f)
            {
                if (rnd.NextDouble() < 0.04) continue; // выпавшая плита — видна трава
                var mat = stones[rnd.Next(stones.Length)];
                float h = R(0.02f, 0.07f);
                Art.Part(root, MeshKit.Box(), mat, new Vector3(x + 0.5f, h * 0.5f - 0.02f, z + 0.5f), new Vector3(0.94f, h, 0.94f), new Vector3(0f, R(-2f, 2f), 0f), false);
            }

            // бордюр из камней по краю площадки
            var curb = Art.LitMat(Art.StoneDark);
            for (float x = -hw; x <= hw; x += 1f)
            {
                Art.Part(root, MeshKit.Box(), curb, new Vector3(x, 0.15f, -hd - 0.35f), new Vector3(1.02f, 0.3f, 0.5f));
                Art.Part(root, MeshKit.Box(), curb, new Vector3(x, 0.15f, hd + 0.35f), new Vector3(1.02f, 0.3f, 0.5f));
            }
            for (float z = -hd; z <= hd; z += 1f)
            {
                Art.Part(root, MeshKit.Box(), curb, new Vector3(-hw - 0.35f, 0.15f, z), new Vector3(0.5f, 0.3f, 1.02f));
                Art.Part(root, MeshKit.Box(), curb, new Vector3(hw + 0.35f, 0.15f, z), new Vector3(0.5f, 0.3f, 1.02f));
            }

            // колонны-препятствия (сломанные, с мхом)
            foreach (var o in sim.Obstacles)
            {
                var p = new Vector3(o.Pos.x, 0f, o.Pos.y);
                float h = R(1.4f, 2.4f);
                Art.Part(root, MeshKit.Prism(8), Art.LitMat(Art.StoneDark), p, new Vector3(o.R * 2.3f, 0.35f, o.R * 2.3f));
                Art.Part(root, MeshKit.Prism(8), Art.LitMat(Art.Stone), p + Vector3.up * 0.35f, new Vector3(o.R * 1.8f, h, o.R * 1.8f), new Vector3(0f, R(0f, 45f), 0f));
                Art.Part(root, MeshKit.Ico(0, 0.25f, rnd.Next(99)), Art.LitMat(Art.Moss), p + Vector3.up * (h + 0.3f), new Vector3(o.R * 1.9f, 0.5f, o.R * 1.9f));
            }

            // деревья, кусты, руины вокруг площадки
            for (int i = 0; i < 70; i++)
            {
                float a = R(0f, Mathf.PI * 2f), dist = R(1.6f, 9f);
                var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                // точка снаружи прямоугольника площадки
                var edge = new Vector2(Mathf.Clamp(dir.x * 100f, -hw, hw), Mathf.Clamp(dir.y * 100f, -hd, hd));
                var pos2 = edge + dir * dist;
                var pos = new Vector3(pos2.x, 0f, pos2.y);
                double kind = rnd.NextDouble();
                if (kind < 0.45) Tree(root, pos, R(0.8f, 1.4f), rnd);
                else if (kind < 0.8) Bush(root, pos, R(0.5f, 1.1f), rnd);
                else Ruin(root, pos, rnd);
            }

            // трава и цветы
            var tuft = Art.LitMat(Art.GrassDark);
            var flowers = new[] { Art.LitMat(new Color(1f, 0.95f, 0.85f)), Art.LitMat(new Color(1f, 0.8f, 0.3f)), Art.LitMat(new Color(0.95f, 0.55f, 0.7f)) };
            for (int i = 0; i < 260; i++)
            {
                var p = new Vector3(R(-hw - 9f, hw + 9f), 0f, R(-hd - 9f, hd + 9f));
                if (Mathf.Abs(p.x) < hw + 0.7f && Mathf.Abs(p.z) < hd + 0.7f) continue;
                for (int k = 0; k < 3; k++)
                    Art.Part(root, MeshKit.Prism(3, 0f, false), tuft, p + new Vector3(R(-0.15f, 0.15f), 0f, R(-0.15f, 0.15f)), new Vector3(0.12f, R(0.25f, 0.45f), 0.12f), new Vector3(R(-15f, 15f), R(0f, 120f), R(-15f, 15f)), false);
                if (rnd.NextDouble() < 0.3)
                    Art.Part(root, MeshKit.Ico(0), flowers[rnd.Next(flowers.Length)], p + new Vector3(0.2f, 0.25f, 0.1f), Vector3.one * 0.12f, default, false);
            }
            // трава в щелях площадки
            for (int i = 0; i < 40; i++)
            {
                var p = new Vector3(R(-hw + 0.5f, hw - 0.5f), 0f, R(-hd + 0.5f, hd - 0.5f));
                Art.Part(root, MeshKit.Prism(3, 0f, false), tuft, p, new Vector3(0.1f, R(0.12f, 0.22f), 0.1f), new Vector3(0f, R(0f, 120f), 0f), false);
            }
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
    }
}
