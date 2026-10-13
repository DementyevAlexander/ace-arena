using UnityEditor;

namespace SGG.UnityMCP.Editor.Commands
{
    public sealed class AssetIndexCacheInvalidator : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (HasChanges(importedAssets) || HasChanges(deletedAssets) || HasChanges(movedAssets) || HasChanges(movedFromAssetPaths))
            {
                AssetIndexCommands.InvalidateCache();
            }

            if (HasSerializeReferenceAssetChanges(importedAssets)
                || HasSerializeReferenceAssetChanges(deletedAssets)
                || HasSerializeReferenceAssetChanges(movedAssets)
                || HasSerializeReferenceAssetChanges(movedFromAssetPaths))
            {
                SerializeReferenceCommands.InvalidateCache();
            }
        }

        static bool HasChanges(string[] paths)
        {
            return paths != null && paths.Length > 0;
        }

        static bool HasSerializeReferenceAssetChanges(string[] paths)
        {
            if (paths == null)
            {
                return false;
            }

            foreach (var path in paths)
            {
                if (IsSerializeReferenceAssetPath(path))
                {
                    return true;
                }
            }

            return false;
        }

        static bool IsSerializeReferenceAssetPath(string path)
        {
            return !string.IsNullOrEmpty(path)
                && path.StartsWith("Assets/", System.StringComparison.Ordinal)
                && (path.EndsWith(".unity", System.StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".asset", System.StringComparison.OrdinalIgnoreCase));
        }
    }
}
