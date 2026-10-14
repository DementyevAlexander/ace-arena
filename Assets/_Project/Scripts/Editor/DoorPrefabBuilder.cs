using System.IO;
using Arena.View;
using UnityEditor;
using UnityEngine;

namespace Arena.EditorTools
{
    /// <summary>Arena → Setup → Rebuild Door Prefab: собирает префаб двери (каркас + полотно) в Resources/Prefabs/Door
    /// и его материалы в Art/Door. Из командной строки: -executeMethod Arena.EditorTools.DoorPrefabBuilder.Run</summary>
    public static class DoorPrefabBuilder
    {
        const string PrefabFile = "Assets/_Project/Resources/Prefabs/Door.prefab";
        const string MatDir = "Assets/_Project/Art/Door";

        [MenuItem("Arena/Setup/Rebuild Door Prefab")]
        public static void Run()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PrefabFile));
            Directory.CreateDirectory(MatDir);
            var frame = Mat("DoorFrame", new Color(0.55f, 0.5f, 0.52f));
            var leaf = Mat("DoorLeaf", new Color(0.33f, 0.22f, 0.17f));
            var trim = Mat("DoorTrim", new Color(0.62f, 0.5f, 0.3f));
            var go = DoorKit.Build(frame, leaf, trim);
            // меш куба — из кода (MeshKit), в префаб его надо положить ассетом
            var box = AssetDatabase.LoadAssetAtPath<Mesh>(MatDir + "/DoorBox.asset");
            if (box == null)
            {
                box = Object.Instantiate(MeshKit.Box());
                box.name = "DoorBox";
                AssetDatabase.CreateAsset(box, MatDir + "/DoorBox.asset");
            }
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>()) mf.sharedMesh = box;
            PrefabUtility.SaveAsPrefabAsset(go, PrefabFile);
            Object.DestroyImmediate(go);
            AssetDatabase.SaveAssets();
            Debug.Log("[Arena] Door prefab: " + PrefabFile);
        }

        static Material Mat(string name, Color c)
        {
            string path = $"{MatDir}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m != null) return m;
            var keep = Resources.Load<Material>("ShaderKeep/Lit");
            m = keep != null ? new Material(keep) : new Material(Art.LitShader);
            m.name = name;
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Smoothness", 0.12f);
            AssetDatabase.CreateAsset(m, path);
            return m;
        }
    }
}
