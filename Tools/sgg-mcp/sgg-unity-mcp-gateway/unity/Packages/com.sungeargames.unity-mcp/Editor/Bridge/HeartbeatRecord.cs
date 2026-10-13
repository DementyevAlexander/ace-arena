using System;
using SGG.UnityMCP.Editor.Paths;

namespace SGG.UnityMCP.Editor.Bridge
{
    [Serializable]
    public sealed class HeartbeatRecord
    {
        public int schema_version = 1;
        public string instance_id = string.Empty;
        public string project = string.Empty;
        public string display_name = string.Empty;
        public string package_version = string.Empty;
        public string project_root = string.Empty;
        public string canonical_project_root = string.Empty;
        public PathAlias[] aliases = new PathAlias[0];
        public string unity_version = string.Empty;
        public int unity_pid;
        public string unity_process_start_utc = string.Empty;
        public string bridge_host = "127.0.0.1";
        public int bridge_port;
        public string token = string.Empty;
        public string status = "starting";
        public bool has_unsaved_scene_changes;
        public int dirty_scene_count;
        public DirtySceneInfo[] dirty_scenes = new DirtySceneInfo[0];
        public string commands_hash = string.Empty;
        public string catalog_status = "pending";
        public long catalog_generation;
        public long heartbeat_generation;
        public long refresh_generation;
        public string last_asset_refresh_utc = string.Empty;
        public long compilation_generation;
        public long completed_compilation_generation;
        public string last_compilation_started_utc = string.Empty;
        public string last_compilation_finished_utc = string.Empty;
        public string background_compiler_progress_state = "unobserved";
        public bool background_compiler_progress_authoritative;
        public bool background_compiler_progress_active;
        public bool background_compiler_progress_failed;
        public string background_compiler_progress_category = "none";
        public long background_compiler_progress_catalog_generation;
        public long background_compiler_progress_compilation_generation;
        public long background_compiler_progress_completed_compilation_generation;
        public string background_compiler_progress_observed_utc = string.Empty;
        public string quit_operation_id = string.Empty;
        public string quit_caller_args_hash = string.Empty;
        public string quit_request_state = "none";
        public string quit_requested_utc = string.Empty;
        public string quit_state_updated_utc = string.Empty;
        public string last_heartbeat = string.Empty;
    }
}
