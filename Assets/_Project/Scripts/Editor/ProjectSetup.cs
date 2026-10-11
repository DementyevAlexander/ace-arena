using Arena.View;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Arena.EditorTools
{
    /// <summary>Arena → Setup: подключает URP-ассет и собирает сцену Game (камера, свет, GameController).
    /// Из командной строки: -executeMethod Arena.EditorTools.ProjectSetup.Run</summary>
    public static class ProjectSetup
    {
        const string RpAsset = "Assets/Settings/PC_RPAsset.asset";
        const string ScenePath = "Assets/_Project/Scenes/Game.unity";

        [MenuItem("Arena/Setup/Rebuild Game Scene")]
        public static void Run()
        {
            AssignPipeline();
            BuildScene();
            AssetDatabase.SaveAssets();
            Debug.Log("[Arena] Setup done");
        }

        static void AssignPipeline()
        {
            var rp = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(RpAsset);
            if (rp == null) { Debug.LogError($"[Arena] Нет {RpAsset}"); return; }
            GraphicsSettings.defaultRenderPipeline = rp;
            int current = QualitySettings.GetQualityLevel();
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = rp;
            }
            QualitySettings.SetQualityLevel(current, false);
            PlayerSettings.colorSpace = ColorSpace.Linear;
        }

        static void BuildScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            camGo.AddComponent<Camera>();
            camGo.AddComponent<AudioListener>();
            var light = new GameObject("Sun").AddComponent<Light>();
            light.type = LightType.Directional;
            new GameObject("Game").AddComponent<GameController>();
            System.IO.Directory.CreateDirectory("Assets/_Project/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }
    }
}
