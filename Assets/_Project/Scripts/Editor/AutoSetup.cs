using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.Rendering;

namespace Arena.EditorTools
{
    /// <summary>Если сцены Game ещё нет или URP не подключён — один раз выполняет Arena → Setup и открывает сцену.</summary>
    [InitializeOnLoad]
    static class AutoSetup
    {
        const string ScenePath = "Assets/_Project/Scenes/Game.unity";

        static AutoSetup()
        {
            if (UnityEngine.Application.isBatchMode) return;
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                bool noScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null;
                bool noPipeline = GraphicsSettings.defaultRenderPipeline == null;
                if (!noScene && !noPipeline) return;
                ProjectSetup.Run();
                EditorSceneManager.OpenScene(ScenePath);
            };
        }
    }
}
