using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using SGG.UnityMCP.Editor.Commands;
using SGG.UnityMCP.Editor.Paths;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Bridge
{
    public sealed class HeartbeatWriter
    {
        readonly string m_instancesDirectory;
        readonly string m_token;
        readonly BridgeRuntimeState m_state;
        readonly Func<int> m_bridgePort;
        readonly ICanonicalPathService m_paths = new CanonicalPathService();
        readonly int m_processId;
        readonly string m_processStartUtc;
        double m_nextWriteTime;
        DateTime m_nextWriteWarningUtc = DateTime.MinValue;
        bool m_cleanupAttempted;
        long m_writeGeneration;

        internal string LastFailureKind { get; private set; } = string.Empty;

        public HeartbeatWriter(string instancesDirectory, string token, BridgeRuntimeState state, Func<int> bridgePort)
        {
            m_instancesDirectory = instancesDirectory;
            m_token = token;
            m_state = state;
            m_bridgePort = bridgePort;
            using (var process = Process.GetCurrentProcess())
            {
                m_processId = process.Id;
                try
                {
                    m_processStartUtc = process.StartTime.ToUniversalTime().ToString("O");
                }
                catch
                {
                    m_processStartUtc = string.Empty;
                }
            }
        }

        public bool WriteIfDue(double now, double intervalSeconds)
        {
            if (now < m_nextWriteTime)
            {
                return false;
            }

            m_nextWriteTime = now + intervalSeconds;
            return Write();
        }

        public bool Write()
        {
            try
            {
                LastFailureKind = string.Empty;
                Directory.CreateDirectory(m_instancesDirectory);
                if (!m_cleanupAttempted)
                {
                    m_cleanupAttempted = true;
                    RemovePreviousHeartbeatsFromThisProcess();
                }

                var heartbeatGeneration = m_writeGeneration + 1;
                var record = BuildRecord(heartbeatGeneration);
                var json = JsonUtility.ToJson(record, true);
                WriteHeartbeatFile(Path.Combine(m_instancesDirectory, m_state.InstanceId + ".json"), json);
                m_writeGeneration = heartbeatGeneration;
                return true;
            }
            catch (IOException exception)
            {
                LastFailureKind = "io";
                LogWriteWarning(exception);
                return false;
            }
            catch (UnauthorizedAccessException exception)
            {
                LastFailureKind = "unauthorized";
                LogWriteWarning(exception);
                return false;
            }
            catch (CanonicalPathDependencyMissingException exception)
            {
                LastFailureKind = "canonical_path_dependency_missing";
                LogWriteWarning(exception);
                return false;
            }
        }

        static void WriteHeartbeatFile(string targetPath, string json)
        {
            var tempPath = targetPath + ".tmp";
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(json);
                }

                if (File.Exists(targetPath))
                {
                    File.Replace(tempPath, targetPath, null);
                }
                else
                {
                    File.Move(tempPath, targetPath);
                }
            }
            finally
            {
                TryDelete(tempPath);
            }
        }

        HeartbeatRecord BuildRecord(long heartbeatGeneration)
        {
            var projectRoot = ProjectPaths.ProjectRoot;
            var canonicalProjectRoot = m_paths.NormalizeProjectRoot(projectRoot).Value;
            var compilation = ConsoleLogBuffer.CompilationLifecycleSnapshot();
            var backgroundCompilerProgress = m_state.CapturePackageLifecycleSnapshot().BackgroundCompilerProgress ?? BackgroundCompilerProgressSnapshot.Unobserved;
            var quit = EditorQuitLifecycle.Current;
            return new HeartbeatRecord
            {
                schema_version = 1,
                instance_id = m_state.InstanceId,
                project = m_state.Project,
                display_name = ProjectPaths.ProjectDisplayName,
                package_version = UnityBridgeRuntime.PackageVersion,
                project_root = projectRoot,
                canonical_project_root = canonicalProjectRoot,
                aliases = m_paths.ProjectRootAliases(projectRoot),
                unity_version = Application.unityVersion,
                unity_pid = m_processId,
                unity_process_start_utc = m_processStartUtc,
                bridge_host = "127.0.0.1",
                bridge_port = m_bridgePort(),
                token = m_token,
                status = m_state.Status,
                has_unsaved_scene_changes = m_state.HasUnsavedSceneChanges,
                dirty_scene_count = m_state.DirtySceneCount,
                dirty_scenes = m_state.DirtyScenes,
                commands_hash = m_state.CommandsHash,
                catalog_status = m_state.CatalogStatus,
                catalog_generation = Math.Max(0, m_state.CatalogGeneration),
                heartbeat_generation = Math.Max(1, heartbeatGeneration),
                refresh_generation = UnityBridgeRuntime.AssetRefreshGeneration,
                last_asset_refresh_utc = UnityBridgeRuntime.LastAssetRefreshUtc,
                compilation_generation = compilation.Generation,
                completed_compilation_generation = compilation.CompletedGeneration,
                last_compilation_started_utc = Timestamp(compilation.StartedUtc),
                last_compilation_finished_utc = Timestamp(compilation.FinishedUtc),
                background_compiler_progress_state = backgroundCompilerProgress.State,
                background_compiler_progress_authoritative = backgroundCompilerProgress.Authoritative,
                background_compiler_progress_active = backgroundCompilerProgress.Active,
                background_compiler_progress_failed = backgroundCompilerProgress.Failed,
                background_compiler_progress_category = backgroundCompilerProgress.Category,
                background_compiler_progress_catalog_generation = backgroundCompilerProgress.CatalogGeneration,
                background_compiler_progress_compilation_generation = backgroundCompilerProgress.CompilationGeneration,
                background_compiler_progress_completed_compilation_generation = backgroundCompilerProgress.CompletedCompilationGeneration,
                background_compiler_progress_observed_utc = backgroundCompilerProgress.ObservedUtc,
                quit_operation_id = quit.OperationId,
                quit_caller_args_hash = quit.CallerArgsHash,
                quit_request_state = quit.State,
                quit_requested_utc = quit.RequestedUtc,
                quit_state_updated_utc = quit.UpdatedUtc,
                last_heartbeat = DateTime.UtcNow.ToString("o"),
            };
        }

        static string Timestamp(DateTime value)
        {
            return value == DateTime.MinValue ? string.Empty : value.ToString("O");
        }

        void RemovePreviousHeartbeatsFromThisProcess()
        {
            foreach (var filePath in Directory.GetFiles(m_instancesDirectory, m_state.Project + "-*.json"))
            {
                try
                {
                    var record = JsonUtility.FromJson<HeartbeatRecord>(ReadAllTextShared(filePath));
                    if (record == null || record.instance_id == m_state.InstanceId)
                    {
                        continue;
                    }

                    if (record.project == m_state.Project && record.unity_pid == m_processId)
                    {
                        File.Delete(filePath);
                    }
                }
                catch
                {
                    // Heartbeats may be read while another process writes them; leave cleanup to the gateway.
                }
            }
        }

        static string ReadAllTextShared(string filePath)
        {
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                return reader.ReadToEnd();
            }
        }

        static void TryDelete(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch
            {
                // Temporary heartbeat files are best-effort cleanup only.
            }
        }

        void LogWriteWarning(Exception exception)
        {
            var now = DateTime.UtcNow;
            if (now < m_nextWriteWarningUtc)
            {
                return;
            }

            m_nextWriteWarningUtc = now.AddSeconds(30);
            UnityEngine.Debug.LogWarning("SGG Unity MCP could not write bridge heartbeat; discovery will retry on the next heartbeat. Path: " + Path.Combine(m_instancesDirectory, m_state.InstanceId + ".json") + ". " + exception.Message);
        }
    }
}
