using System.IO;
using Arena.View;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Arena.EditorTools
{
    /// <summary>Arena → Build → Windows: сборка под Windows x64 в Builds/Windows/AceArena.exe.
    /// Из командной строки: -executeMethod Arena.EditorTools.BuildTools.Windows [-buildOut &lt;папка&gt;]</summary>
    public static class BuildTools
    {
        const string KeepDir = "Assets/_Project/Resources/ShaderKeep";

        [MenuItem("Arena/Build/Windows")]
        public static void Windows()
        {
            EnsureShaderKeep();
            string dir = "Builds/Windows";
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "-buildOut") dir = args[i + 1];
            Directory.CreateDirectory(dir);
            var options = new BuildPlayerOptions
            {
                scenes = new[] { "Assets/_Project/Scenes/Game.unity" },
                locationPathName = Path.Combine(dir, "AceArena.exe"),
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
            };
            var report = BuildPipeline.BuildPlayer(options);
            var s = report.summary;
            Debug.Log($"[Arena] Build {s.result}: {s.outputPath}, {s.totalSize / (1024 * 1024)} MB, {s.totalTime.TotalSeconds:0} s, ошибок {s.totalErrors}");
            if (Application.isBatchMode) EditorApplication.Exit(s.result == BuildResult.Succeeded ? 0 : 1);
        }

        /// <summary>Материалы-образцы в Resources/ShaderKeep (Art.KeepNames): все материалы игры — их копии; без образцов сборка
        /// вырезает шейдеры URP Lit/Unlit — в билде чёрный экран.</summary>
        [MenuItem("Arena/Build/Ensure Shader Assets")]
        public static void EnsureShaderKeep()
        {
            Directory.CreateDirectory(KeepDir);
            for (int i = 0; i < Art.KeepNames.Length; i++)
            {
                string path = $"{KeepDir}/{Art.KeepNames[i]}.mat";
                if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) continue;
                bool lit = i < 2, transparent = i % 2 == 1;
                var m = new Material(lit ? Art.LitShader : Art.UnlitShader) { name = Art.KeepNames[i] };
                if (transparent) Art.MakeTransparent(m, 3000);
                AssetDatabase.CreateAsset(m, path); // при импорте URP сам выставит ключевые слова — их и унаследуют копии
            }
            AssetDatabase.SaveAssets();
        }
    }
}
