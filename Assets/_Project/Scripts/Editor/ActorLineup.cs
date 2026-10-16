using Arena.Sim;
using Arena.View;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Arena.EditorTools
{
    /// <summary>Arena → Art → Actor Lineup: герой и враги Hades в ряд, как на концепте «Enemy caste»
    /// (Screenshots/mockups/enemy_caste_reference.webp) — спокойные, в замахе и сверху. Снимки — Screenshots/lineup_*.png.
    /// Без окна: Unity -batchmode -executeMethod Arena.EditorTools.ActorLineup.Capture -quit.</summary>
    public static class ActorLineup
    {
        static readonly EnemyKind[] Kinds = { EnemyKind.Thug, EnemyKind.Wastrel, EnemyKind.Witch, EnemyKind.Casket };

        [MenuItem("Arena/Art/Actor Lineup")]
        public static void Capture()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            bool async = ShaderUtil.allowAsyncCompilation;
            ShaderUtil.allowAsyncCompilation = false; // иначе первый кадр — серые заглушки шейдеров
            RenderSettings.fog = false;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.53f, 0.52f);
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.25f;
            sun.color = new Color(1f, 0.95f, 0.88f);
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(40f, 200f, 0f);
            var world = new GameObject("World").transform;
            Art.Part(world, MeshKit.Box(), Art.LitMat(new Color(0.62f, 0.58f, 0.55f)), new Vector3(0f, -0.05f, 0f), new Vector3(40f, 0.1f, 40f));

            var player = new PlayerView(world);
            var views = new EnemyView[Kinds.Length * 2];
            for (int row = 0; row < 2; row++)
                for (int i = 0; i < Kinds.Length; i++)
                {
                    var e = new Enemy { Kind = Kinds[i], Hp = 10f, MaxHp = 10f, State = AiState.Wander, Face = Vector2.up };
                    e.Pos = new Vector2(-4.2f + 2.1f * i + row * 14f, 0f);
                    if (row == 1)
                    {
                        // замах: зона атаки на полпути — враг красный, Casket открыт
                        e.State = AiState.Windup;
                        e.Tele = new Telegraph { Shape = TeleShape.Ellipse, Center = e.Pos + Vector2.up, Dir = Vector2.up, Forward = 0.6f, Side = 0.6f, Total = 1f, T = 0.4f };
                    }
                    views[row * Kinds.Length + i] = new EnemyView(e, world);
                }
            player.Root.position = new Vector3(4.3f, 0f, 0f);
            for (int k = 0; k < 40; k++)
                foreach (var v in views) v.Sync(0.03f, k * 0.03f);

            var cam = new GameObject("Cam").AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.66f, 0.63f, 0.60f);
            cam.fieldOfView = 26f;
            Shot(cam, new Vector3(0f, 1.6f, 14f), new Vector3(0f, 0.85f, 0f), "lineup_front.png");
            Shot(cam, new Vector3(12f, 1.6f, 14f), new Vector3(12f, 0.85f, 0f), "lineup_windup.png");
            Shot(cam, new Vector3(0f, 17f, 8f), new Vector3(0f, 0.3f, 0f), "lineup_top.png");
            cam.fieldOfView = 20f;
            Shot(cam, new Vector3(4.3f, 1.2f, 5f), new Vector3(4.3f, 0.75f, 0f), "lineup_hero.png");
            Shot(cam, new Vector3(4.3f + 3.4f, 1.6f, 3.6f), new Vector3(4.3f, 0.7f, 0f), "lineup_hero_34.png");
            ShaderUtil.allowAsyncCompilation = async;
        }

        static void Shot(Camera cam, Vector3 pos, Vector3 look, string file)
        {
            cam.transform.position = pos;
            cam.transform.LookAt(look);
            var rt = new RenderTexture(1800, 760, 24);
            cam.targetTexture = rt;
            cam.Render(); // первый кадр URP после новой сцены бывает без освещения — снимаем второй
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            cam.targetTexture = null;
            var path = System.IO.Path.Combine(Application.dataPath, "..", "Screenshots", file);
            System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            rt.Release();
            Debug.Log($"ActorLineup: {System.IO.Path.GetFullPath(path)}");
        }
    }
}
