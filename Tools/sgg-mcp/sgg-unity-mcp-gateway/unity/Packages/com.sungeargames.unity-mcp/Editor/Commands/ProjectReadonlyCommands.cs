using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class ProjectReadonlyCommands
    {
        const string EmptySchemaJson = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
        const string OutputLimitJson = "{\"max_fields\":8}";

        [McpCommand(
            "project.settings.summary",
            Title = "Project settings summary",
            Description = "Read compact Unity project settings for Unity AI Assistant compatibility without host filesystem paths",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = EmptySchemaJson,
            Tags = new string[] { "project", "settings", "unity-ai-assistant" },
            Capability = "project_readonly",
            ResultShape = "status",
            OutputLimitJson = OutputLimitJson)]
        public static CommandExecutionResult ProjectSettingsSummary()
        {
            var json = "{\"active_rendering_pipeline\":" + JsonUtil.String(ActiveRenderingPipeline())
                + ",\"target_platform\":" + JsonUtil.String(EditorUserBuildSettings.activeBuildTarget.ToString())
                + ",\"build_target_group\":" + JsonUtil.String(EditorUserBuildSettings.selectedBuildTargetGroup.ToString())
                + ",\"api_compatibility_level\":" + JsonUtil.String(ApiCompatibilityLevel())
                + ",\"input_system\":" + JsonUtil.String(InputSystem())
                + ",\"unity_version\":" + JsonUtil.String(Application.unityVersion)
                + ",\"official_path_fields_omitted\":true"
                + ",\"compatible_with\":\"Unity.GetStaticProjectSettingsTool\"}";
            return CommandExecutionResult.Ok(json);
        }

        static string ActiveRenderingPipeline()
        {
            var renderingPipeline = GraphicsSettings.currentRenderPipeline;
            return renderingPipeline == null
                ? "No rendering pipeline is currently selected, the built-in pipeline is used."
                : renderingPipeline.name;
        }

        static string ApiCompatibilityLevel()
        {
            var buildTargetGroup = EditorUserBuildSettings.selectedBuildTargetGroup;
            var namedBuildTarget = buildTargetGroup != BuildTargetGroup.Standalone
                ? NamedBuildTarget.FromBuildTargetGroup(buildTargetGroup)
                : EditorUserBuildSettings.standaloneBuildSubtarget == StandaloneBuildSubtarget.Server
                    ? NamedBuildTarget.Server
                    : NamedBuildTarget.Standalone;
            return PlayerSettings.GetApiCompatibilityLevel(namedBuildTarget).ToString();
        }

        static string InputSystem()
        {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            return "New Input System";
#elif ENABLE_LEGACY_INPUT_MANAGER && ENABLE_INPUT_SYSTEM
            return "Both New Input System and Legacy Input Manager";
#elif ENABLE_LEGACY_INPUT_MANAGER && !ENABLE_INPUT_SYSTEM
            return "Legacy Input Manager";
#else
            return "None";
#endif
        }
    }
}
