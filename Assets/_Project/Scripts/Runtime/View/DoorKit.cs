using UnityEngine;

namespace Arena.View
{
    /// <summary>Дверь выхода: каркас (два косяка, перемычка, порог) и полотно (две створки с накладками и кольцами).
    /// Префаб — Resources/Prefabs/Door (его собирает Arena → Setup → Rebuild Door Prefab); полотно — дочерний объект "Leaf",
    /// каркас — "Frame". Свой арт: замените префаб, сохранив эти два имени. Опора — середина порога на полу, +Z смотрит в комнату.</summary>
    public static class DoorKit
    {
        public const string PrefabPath = "Prefabs/Door";
        public const string FrameName = "Frame", LeafName = "Leaf";

        // проём 1.5 × 2.2 юнита — герой (рост ~1.3) проходит с запасом, как в дверях Hades
        public const float Width = 1.5f, Height = 2.2f, Post = 0.32f, Depth = 0.5f;

        /// <summary>Собирает дверь из примитивов: frame — материал каркаса, leaf — материал полотна, trim — накладки и кольца.</summary>
        public static GameObject Build(Material frame, Material leaf, Material trim)
        {
            var root = new GameObject("Door");
            var f = new GameObject(FrameName).transform;
            f.SetParent(root.transform, false);
            float halfW = Width * 0.5f;
            Part(f, frame, new Vector3(-halfW - Post * 0.5f, Height * 0.5f, 0f), new Vector3(Post, Height, Depth));         // левый косяк
            Part(f, frame, new Vector3(halfW + Post * 0.5f, Height * 0.5f, 0f), new Vector3(Post, Height, Depth));          // правый косяк
            Part(f, frame, new Vector3(0f, Height + 0.18f, 0f), new Vector3(Width + Post * 2f + 0.2f, 0.36f, Depth + 0.08f)); // перемычка
            Part(f, frame, new Vector3(0f, Height + 0.42f, -0.02f), new Vector3(Width * 0.55f, 0.14f, Depth * 0.8f));       // замковый камень
            Part(f, frame, new Vector3(0f, 0.03f, 0.05f), new Vector3(Width + Post * 2f, 0.06f, Depth + 0.25f));            // порог

            var l = new GameObject(LeafName).transform;
            l.SetParent(root.transform, false);
            float leafW = halfW - 0.02f;
            for (int side = -1; side <= 1; side += 2)
            {
                float cx = side * (leafW * 0.5f + 0.01f);
                Part(l, leaf, new Vector3(cx, Height * 0.5f, 0f), new Vector3(leafW, Height - 0.04f, 0.12f));                  // створка
                Part(l, trim, new Vector3(cx, Height * 0.72f, 0.07f), new Vector3(leafW * 0.8f, 0.07f, 0.03f));                // накладки
                Part(l, trim, new Vector3(cx, Height * 0.3f, 0.07f), new Vector3(leafW * 0.8f, 0.07f, 0.03f));
                Part(l, trim, new Vector3(side * 0.12f, Height * 0.48f, 0.09f), new Vector3(0.09f, 0.09f, 0.05f));            // кольцо
            }
            return root;
        }

        static void Part(Transform parent, Material mat, Vector3 pos, Vector3 scale)
        {
            var go = new GameObject("Part");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = MeshKit.Box();
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        }
    }
}
