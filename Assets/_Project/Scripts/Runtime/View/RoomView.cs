using Arena.Sim;
using UnityEngine;

namespace Arena.View
{
    /// <summary>Комната в духе TUNIC: каменная площадка, трава, круглые деревья, кусты, руины, цветы.</summary>
    public static class RoomView
    {
        public static Transform Build(ArenaSim sim)
        {
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
