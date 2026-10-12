using UnityEditor;

namespace SGG.UnityMCP.Editor.Bridge
{
    internal sealed class PackageUpdateAssetInvalidator : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            if (TouchesPackageCode(importedAssets) || TouchesPackageCode(deletedAssets)
                || TouchesPackageCode(movedAssets) || TouchesPackageCode(movedFromAssetPaths))
            {
                UnityBridgeRuntime.InvalidatePackageUpdateMonitor("asset_database_package_change");
            }
        }

        static bool TouchesPackageCode(string[] paths)
        {
            if (paths == null) return false;
            for (var index = 0; index < paths.Length; index++)
            {
                var path = paths[index];
                if (string.IsNullOrEmpty(path) || !path.StartsWith("Packages/", System.StringComparison.Ordinal)) continue;
                var extension = System.IO.Path.GetExtension(path);
                if (extension.Equals(".asmdef", System.StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".asmref", System.StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".cs", System.StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".dll", System.StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".rsp", System.StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
