using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SGG.UnityMCP.Editor.Bridge;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Commands
{
    internal sealed class DynamicCSharpReadonlyOperation
    {
        internal DynamicCSharpReadonlyOperation(DynamicCSharpReadonlyResultStore.Receipt receipt)
        {
            Receipt = receipt;
        }

        internal DynamicCSharpReadonlyResultStore.Receipt Receipt { get; private set; }

        internal bool TryStartPhase(string phase, out CommandExecutionResult error)
        {
            return DynamicCSharpReadonlyResultStore.TrySetPhase(Receipt, phase, "running", 0, out error);
        }

        internal bool TryFinishPhase(string phase, long durationMs, bool success, out CommandExecutionResult error)
        {
            return DynamicCSharpReadonlyResultStore.TrySetPhase(Receipt, phase, success ? "completed" : "failed", durationMs, out error);
        }

        internal CommandExecutionResult PublishTerminal(CommandExecutionResult result)
        {
            return DynamicCSharpReadonlyResultStore.PublishTerminal(Receipt, result);
        }

        internal CommandExecutionResult PublishFailedPhase(string phase, CommandExecutionResult result)
        {
            if (!TryStartPhase(phase, out var phaseError))
            {
                return phaseError;
            }
            if (!TryFinishPhase(phase, 0, false, out phaseError))
            {
                return phaseError;
            }
            return PublishTerminal(result);
        }
    }

    internal static class DynamicCSharpReadonlyResultStore
    {
        internal const string Contract = "sgg.unity_mcp.dynamic_csharp_readonly_result.v1";
        internal const string ArtifactRoot = "Library/SGGUnityMcp/job-artifacts/dynamic-csharp-readonly";
        internal const string OperationTag = "dynamic_csharp_readonly_operation_correlated";
        internal const string StatusCommand = "editor.execute_csharp.readonly.status";

        const string ExecuteCommand = "editor.execute_csharp.readonly";
        const int MaxArtifactCharacters = 262144;
        const int MaxArtifacts = 64;
        const int MaxArtifactScan = MaxArtifacts * 4;
        const int MaxQueueWaitMs = 300000;
        const uint MoveFileReplaceExisting = 0x1;
        const uint MoveFileWriteThrough = 0x8;
        const int MaxWindowsArtifactCommitAttempts = 8;
        const int WindowsArtifactCommitRetryDelayMs = 10;

        static readonly object Gate = new object();
        static readonly Dictionary<string, Receipt> Active = new Dictionary<string, Receipt>(StringComparer.Ordinal);
        static readonly int ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;
        static readonly string ProcessStartUtc = ReadProcessStartUtc();
        static int s_publishFailuresForTests;

        internal sealed class ArtifactCapacityObservation
        {
            internal readonly int ObservedMatchingFiles;
            internal readonly bool EnumerationComplete;
            internal readonly string FailureReason;

            internal ArtifactCapacityObservation(int observedMatchingFiles, bool enumerationComplete, string failureReason)
            {
                ObservedMatchingFiles = observedMatchingFiles;
                EnumerationComplete = enumerationComplete;
                FailureReason = failureReason;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "MoveFileExW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);

        [Serializable]
        internal sealed class Phase
        {
            public string state = "not_started";
            public long duration_ms;
        }

        [Serializable]
        internal sealed class Phases
        {
            public Phase project_queue_admission = new Phase();
            public Phase reference_resolution = new Phase();
            public Phase compilation = new Phase();
            public Phase main_thread_execution = new Phase();
            public Phase result_serialization = new Phase();
            public Phase result_publication = new Phase();
            public Phase bridge_response_write = new Phase();
        }

        [Serializable]
        internal sealed class Receipt
        {
            public int schema_version = 1;
            public string contract = Contract;
            public string command = ExecuteCommand;
            public string operation_id = string.Empty;
            public string artifact_path = string.Empty;
            public string project = string.Empty;
            public string origin_instance_id = string.Empty;
            public int origin_unity_pid;
            public string origin_unity_process_start_utc = string.Empty;
            public string package_id = "com.sungeargames.unity-mcp";
            public string package_version = string.Empty;
            public string commands_hash = string.Empty;
            public long catalog_generation;
            public string caller_args_hash = string.Empty;
            public string snippet_hash = string.Empty;
            public string created_at_utc = string.Empty;
            public string updated_at_utc = string.Empty;
            public string status = "running";
            public bool terminal;
            public bool result_available;
            public string command_result_json = string.Empty;
            public string command_result_hash = string.Empty;
            public string receipt_hash = string.Empty;
            public Phases phase_evidence = new Phases();
        }

        internal static bool TryBegin(McpCommandContext context, string code, out DynamicCSharpReadonlyOperation operation, out CommandExecutionResult error)
        {
            operation = null;
            error = null;
            var operationId = JsonUtil.ExtractString(context.ArgsJson, "_dynamic_csharp_operation_id").Trim();
            var callerArgsHash = JsonUtil.ExtractString(context.ArgsJson, "_dynamic_csharp_caller_args_hash").Trim();
            var hasQueueWait = JsonUtil.HasProperty(context.ArgsJson, "_dynamic_csharp_queue_wait_ms");
            var queueWaitMs = JsonUtil.ExtractInt(context.ArgsJson, "_dynamic_csharp_queue_wait_ms", -1);
            var hasCorrelation = !string.IsNullOrEmpty(operationId) || !string.IsNullOrEmpty(callerArgsHash) || hasQueueWait;
            if (!hasCorrelation)
            {
                return true;
            }

            if (!IsOperationId(operationId) || !IsSha256(callerArgsHash) || !hasQueueWait || queueWaitMs < 0 || queueWaitMs > MaxQueueWaitMs || context.State == null)
            {
                error = CommandExecutionResult.Error("dynamic_csharp_result_binding_invalid", "Gateway dynamic C# read-only operation binding is invalid", CorrelationFailureJson(operationId, false));
                return false;
            }

            var state = context.State;
            var receipt = new Receipt
            {
                operation_id = operationId,
                artifact_path = ArtifactPath(operationId),
                project = context.Project ?? string.Empty,
                origin_instance_id = state.InstanceId ?? string.Empty,
                origin_unity_pid = ProcessId,
                origin_unity_process_start_utc = ProcessStartUtc,
                package_version = UnityBridgeRuntime.PackageVersion,
                commands_hash = state.CommandsHash ?? string.Empty,
                catalog_generation = state.CatalogGeneration,
                caller_args_hash = callerArgsHash,
                snippet_hash = Sha256Prefixed(code ?? string.Empty),
                created_at_utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            };
            receipt.updated_at_utc = receipt.created_at_utc;
            receipt.phase_evidence.project_queue_admission.state = "completed";
            receipt.phase_evidence.project_queue_admission.duration_ms = queueWaitMs;

            if (string.IsNullOrEmpty(receipt.project)
                || string.IsNullOrEmpty(receipt.origin_instance_id)
                || string.IsNullOrEmpty(receipt.commands_hash)
                || receipt.catalog_generation <= 0
                || string.IsNullOrEmpty(receipt.origin_unity_process_start_utc))
            {
                error = CommandExecutionResult.Error("dynamic_csharp_result_binding_invalid", "Unity dynamic C# read-only owner identity is incomplete", CorrelationFailureJson(operationId, false));
                return false;
            }

            lock (Gate)
            {
                if (!TryPrepareArtifactSlot(receipt, out var storageError, out var capacityObservation))
                {
                    error = StoreError(operationId, storageError, false, capacityObservation);
                    return false;
                }

                Active[operationId] = receipt;
                if (!TryPublish(receipt, out storageError))
                {
                    error = FailPublication(receipt, storageError, false);
                    return false;
                }
            }

            operation = new DynamicCSharpReadonlyOperation(receipt);
            return true;
        }

        internal static CommandExecutionResult Status(McpCommandContext context)
        {
            var operationId = JsonUtil.ExtractString(context.ArgsJson, "operation_id").Trim();
            if (!IsOperationId(operationId))
            {
                return CommandExecutionResult.ValidationError("invalid_field_value", "operation_id has an invalid format", "operation_id", "pattern_mismatch");
            }

            lock (Gate)
            {
                Active.TryGetValue(operationId, out var active);
                var hasActiveTerminal = active != null && active.terminal;
                Receipt receipt;
                if (hasActiveTerminal)
                {
                    if (!Validate(active, operationId, out var activeInvalidReason))
                    {
                        return InvalidReceipt(operationId, activeInvalidReason);
                    }
                    if (!OwnerMatches(active, context.State, context.Project, out var activeOwnerEvidence))
                    {
                        return OwnerMismatch(operationId, activeOwnerEvidence);
                    }

                    if (TryReadStable(operationId, out var durable, out var durableReadError))
                    {
                        if (!Validate(durable, operationId, out var durableInvalidReason))
                        {
                            return InvalidReceipt(operationId, durableInvalidReason);
                        }
                        if (!OwnerMatches(durable, context.State, context.Project, out var durableOwnerEvidence))
                        {
                            return OwnerMismatch(operationId, durableOwnerEvidence);
                        }
                        if (!SameBinding(active, durable))
                        {
                            return InvalidReceipt(operationId, "receipt_binding_mismatch");
                        }
                        if (!string.Equals(durable.receipt_hash, active.receipt_hash, StringComparison.Ordinal)) TryPublish(active, out _);
                    }
                    else if (IsCorruptReadError(durableReadError))
                    {
                        return InvalidReceipt(operationId, durableReadError);
                    }
                    else if (durableReadError == "not_found" || durableReadError == "artifact_read_failed")
                    {
                        TryPublish(active, out _);
                    }
                    else
                    {
                        return StoreError(operationId, durableReadError, true);
                    }
                    return CommandExecutionResult.Ok(StatusJson(active));
                }
                if (!TryReadStable(operationId, out receipt, out var readError))
                {
                    var code = readError == "not_found" ? "dynamic_csharp_result_not_found" : "dynamic_csharp_result_unavailable";
                    return CommandExecutionResult.Error(code, readError == "not_found" ? "Dynamic C# read-only result was not found" : "Dynamic C# read-only result could not be read safely", CorrelationFailureJson(operationId, false));
                }

                if (!Validate(receipt, operationId, out var invalidReason))
                {
                    return InvalidReceipt(operationId, invalidReason);
                }

                if (!OwnerMatches(receipt, context.State, context.Project, out var ownerEvidence))
                {
                    return OwnerMismatch(operationId, ownerEvidence);
                }

                return CommandExecutionResult.Ok(StatusJson(receipt));
            }
        }

        internal static bool TrySetPhase(Receipt receipt, string phase, string state, long durationMs, out CommandExecutionResult error)
        {
            error = null;
            lock (Gate)
            {
                if (receipt == null || !Active.TryGetValue(receipt.operation_id, out var active) || !ReferenceEquals(active, receipt) || !TryPhase(receipt, phase, out var target))
                {
                    error = StoreError(receipt == null ? string.Empty : receipt.operation_id, "operation_state_unavailable", true);
                    return false;
                }

                target.state = state;
                target.duration_ms = Math.Max(0, durationMs);
                receipt.updated_at_utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                if (!TryPublish(receipt, out var storageError))
                {
                    if (state == "running") target.state = "failed";
                    error = FailPublication(receipt, storageError, true);
                    return false;
                }

                return true;
            }
        }

        internal static CommandExecutionResult PublishTerminal(Receipt receipt, CommandExecutionResult result)
        {
            if (receipt == null)
            {
                return result;
            }

            lock (Gate)
            {
                if (!Active.TryGetValue(receipt.operation_id, out var active) || !ReferenceEquals(active, receipt))
                {
                    return StoreError(receipt.operation_id, "operation_state_unavailable", true);
                }

                receipt.command_result_json = CommandResultJson(result);
                receipt.command_result_hash = Sha256Prefixed(receipt.command_result_json);
                receipt.result_available = true;
                receipt.terminal = true;
                receipt.status = result.Status == "ok" ? "completed" : "failed";
                receipt.phase_evidence.result_publication.state = "completed";
                receipt.phase_evidence.result_publication.duration_ms = 0;
                receipt.updated_at_utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                if (!TryPublish(receipt, out var storageError))
                {
                    return FailPublication(receipt, storageError, true);
                }

                return Correlate(result, receipt);
            }
        }

        internal static void MarkResponseWrite(string argsJson, string state, long durationMs)
        {
            var operationId = JsonUtil.ExtractString(argsJson, "_dynamic_csharp_operation_id").Trim();
            var callerArgsHash = JsonUtil.ExtractString(argsJson, "_dynamic_csharp_caller_args_hash").Trim();
            var snippetHash = Sha256Prefixed(JsonUtil.ExtractString(argsJson, "code"));
            if (!IsOperationId(operationId) || !IsSha256(callerArgsHash))
            {
                return;
            }

            lock (Gate)
            {
                if (!Active.TryGetValue(operationId, out var receipt)
                    || !string.Equals(receipt.caller_args_hash, callerArgsHash, StringComparison.Ordinal)
                    || !string.Equals(receipt.snippet_hash, snippetHash, StringComparison.Ordinal))
                {
                    return;
                }

                receipt.phase_evidence.bridge_response_write.state = state;
                receipt.phase_evidence.bridge_response_write.duration_ms = Math.Max(0, durationMs);
                receipt.updated_at_utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                if (!TryPublish(receipt, out var storageError)) FailPublication(receipt, storageError, ExecutionMayHaveStarted(receipt));
            }
        }

        internal static void ResetForTests()
        {
            lock (Gate)
            {
                Active.Clear();
                s_publishFailuresForTests = 0;
            }
        }

        internal static void FailNextPublicationsForTests(int count)
        {
            lock (Gate)
            {
                s_publishFailuresForTests = Math.Max(0, count);
            }
        }

        internal static string ArtifactPath(string operationId)
        {
            return ArtifactRoot + "/" + operationId + ".json";
        }

        static bool TryPrepareArtifactSlot(Receipt receipt, out string error, out ArtifactCapacityObservation capacityObservation)
        {
            error = string.Empty;
            capacityObservation = null;
            if (Active.ContainsKey(receipt.operation_id))
            {
                error = "operation_exists";
                return false;
            }
            if (!TryResolveArtifactPath(receipt.artifact_path, out var fullPath, out error))
            {
                return false;
            }

            try
            {
                var root = Path.GetDirectoryName(fullPath);
                if (string.IsNullOrEmpty(root))
                {
                    error = "artifact_root_unavailable";
                    return false;
                }

                Directory.CreateDirectory(root);
                if (!TryResolveArtifactPath(receipt.artifact_path, out fullPath, out error))
                {
                    return false;
                }
                if (File.Exists(fullPath) || Directory.Exists(fullPath))
                {
                    error = "operation_exists";
                    return false;
                }

                // Age, terminal state and response writing are not consumer
                // acknowledgement. Admission must never retire stored results.
                capacityObservation = ObserveArtifactCapacity(() => Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly));
                error = capacityObservation.FailureReason;
                return string.IsNullOrEmpty(error);
            }
            catch
            {
                error = "artifact_status_unavailable";
                return false;
            }
        }

        internal static ArtifactCapacityObservation ObserveArtifactCapacity(Func<IEnumerable<string>> enumerate)
        {
            var observed = 0;
            try
            {
                // Count matching entries without opening, parsing, classifying
                // or deleting receipts. This is not an atomic directory snapshot.
                foreach (var ignored in enumerate())
                {
                    observed++;
                    if (observed > MaxArtifactScan)
                    {
                        return new ArtifactCapacityObservation(observed, false, "artifact_capacity_unavailable");
                    }
                }
                return new ArtifactCapacityObservation(observed, true, observed >= MaxArtifacts ? "artifact_capacity_exhausted" : string.Empty);
            }
            catch
            {
                return new ArtifactCapacityObservation(observed, false, "artifact_status_unavailable");
            }
        }

        static bool TryPublish(Receipt receipt, out string error)
        {
            error = string.Empty;
            receipt.receipt_hash = ReceiptHash(receipt);
            if (s_publishFailuresForTests > 0)
            {
                s_publishFailuresForTests--;
                error = "artifact_publication_failed";
                return false;
            }
            var content = JsonUtility.ToJson(receipt);
            if (string.IsNullOrEmpty(content) || content.Length > MaxArtifactCharacters)
            {
                error = "artifact_content_out_of_bounds";
                return false;
            }
            if (!TryResolveArtifactPath(receipt.artifact_path, out var fullPath, out error))
            {
                return false;
            }

            string temporaryPath = null;
            try
            {
                var directory = Path.GetDirectoryName(fullPath);
                if (string.IsNullOrEmpty(directory))
                {
                    error = "artifact_root_unavailable";
                    return false;
                }
                Directory.CreateDirectory(directory);
                if (!TryResolveArtifactPath(receipt.artifact_path, out fullPath, out error))
                {
                    return false;
                }
                temporaryPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true))
                {
                    writer.Write(content);
                    writer.Flush();
                    stream.Flush(true);
                }
                if (File.Exists(fullPath) && Path.DirectorySeparatorChar == '\\')
                {
                    if (!TryCommitWindowsArtifact(temporaryPath, fullPath))
                    {
                        error = "artifact_publication_failed";
                        return false;
                    }
                }
                else if (File.Exists(fullPath)) File.Replace(temporaryPath, fullPath, null);
                else File.Move(temporaryPath, fullPath);
                return true;
            }
            catch
            {
                error = "artifact_publication_failed";
                return false;
            }
            finally
            {
                try { if (!string.IsNullOrEmpty(temporaryPath) && File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch { }
            }
        }

        static bool TryCommitWindowsArtifact(string temporaryPath, string fullPath)
        {
            for (var attempt = 1; attempt <= MaxWindowsArtifactCommitAttempts; attempt++)
            {
                if (MoveFileEx(temporaryPath, fullPath, MoveFileReplaceExisting | MoveFileWriteThrough)) return true;
                var errorCode = Marshal.GetLastWin32Error();
                if (!IsTransientWindowsArtifactCommitError(errorCode) || attempt == MaxWindowsArtifactCommitAttempts) return false;
                System.Threading.Thread.Sleep(WindowsArtifactCommitRetryDelayMs);
            }
            return false;
        }

        static bool IsTransientWindowsArtifactCommitError(int errorCode)
        {
            return errorCode == 5 || errorCode == 32 || errorCode == 33 || errorCode == 1175;
        }

        static CommandExecutionResult FailPublication(Receipt receipt, string reason, bool executionMayHaveStarted)
        {
            var failure = StoreError(receipt.operation_id, reason, executionMayHaveStarted);
            receipt.command_result_json = CommandResultJson(failure);
            receipt.command_result_hash = Sha256Prefixed(receipt.command_result_json);
            receipt.result_available = true;
            receipt.terminal = true;
            receipt.status = "failed";
            receipt.phase_evidence.result_publication.state = "failed";
            receipt.phase_evidence.result_publication.duration_ms = 0;
            receipt.updated_at_utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            TryPublish(receipt, out _);
            return Correlate(failure, receipt);
        }

        static CommandExecutionResult InvalidReceipt(string operationId, string reason)
        {
            return CommandExecutionResult.Error("dynamic_csharp_result_invalid", "Dynamic C# read-only result receipt is invalid", "{\"operation_id\":" + JsonUtil.String(operationId) + ",\"reason\":" + JsonUtil.String(reason) + ",\"command_replayed\":false}");
        }

        static CommandExecutionResult OwnerMismatch(string operationId, string evidence)
        {
            return CommandExecutionResult.Error("dynamic_csharp_result_owner_mismatch", "Dynamic C# read-only result belongs to a different Unity owner generation", "{\"operation_id\":" + JsonUtil.String(operationId) + ",\"owner_match\":false," + evidence + ",\"command_replayed\":false}");
        }

        static bool TryReadStable(string operationId, out Receipt receipt, out string error)
        {
            receipt = null;
            error = string.Empty;
            var artifactPath = ArtifactPath(operationId);
            if (!TryResolveArtifactPath(artifactPath, out var fullPath, out error))
            {
                return false;
            }
            try
            {
                if (!File.Exists(fullPath))
                {
                    error = "not_found";
                    return false;
                }
                var before = new FileInfo(fullPath);
                if (before.Length <= 0 || before.Length > MaxArtifactCharacters * 4L)
                {
                    error = "artifact_size_invalid";
                    return false;
                }
                var first = File.ReadAllText(fullPath, new UTF8Encoding(false, true));
                var middle = new FileInfo(fullPath);
                var second = File.ReadAllText(fullPath, new UTF8Encoding(false, true));
                var after = new FileInfo(fullPath);
                if (before.Length != middle.Length || middle.Length != after.Length
                    || before.LastWriteTimeUtc != middle.LastWriteTimeUtc || middle.LastWriteTimeUtc != after.LastWriteTimeUtc
                    || !string.Equals(first, second, StringComparison.Ordinal) || first.Length > MaxArtifactCharacters)
                {
                    error = "artifact_changed_during_read";
                    return false;
                }
                try
                {
                    receipt = JsonUtility.FromJson<Receipt>(first);
                }
                catch
                {
                    error = "artifact_json_invalid";
                    return false;
                }
                if (receipt == null)
                {
                    error = "artifact_json_invalid";
                    return false;
                }
                return true;
            }
            catch (DecoderFallbackException)
            {
                error = "artifact_encoding_invalid";
                return false;
            }
            catch
            {
                error = "artifact_read_failed";
                return false;
            }
        }

        static bool IsCorruptReadError(string error)
        {
            return error == "artifact_size_invalid" || error == "artifact_changed_during_read"
                || error == "artifact_encoding_invalid" || error == "artifact_json_invalid";
        }

        static bool Validate(Receipt receipt, string operationId, out string reason)
        {
            reason = string.Empty;
            if (receipt.schema_version != 1 || receipt.contract != Contract || receipt.command != ExecuteCommand
                || receipt.operation_id != operationId || receipt.artifact_path != ArtifactPath(operationId)
                || !IsOperationId(receipt.operation_id) || !IsSha256(receipt.caller_args_hash) || !IsSha256(receipt.snippet_hash)
                || receipt.package_id != "com.sungeargames.unity-mcp" || string.IsNullOrEmpty(receipt.package_version)
                || string.IsNullOrEmpty(receipt.project) || string.IsNullOrEmpty(receipt.origin_instance_id)
                || receipt.origin_unity_pid <= 0 || string.IsNullOrEmpty(receipt.origin_unity_process_start_utc)
                || string.IsNullOrEmpty(receipt.commands_hash) || receipt.catalog_generation <= 0
                || receipt.phase_evidence == null || receipt.phase_evidence.project_queue_admission == null
                || receipt.phase_evidence.reference_resolution == null || receipt.phase_evidence.compilation == null
                || receipt.phase_evidence.main_thread_execution == null || receipt.phase_evidence.result_serialization == null
                || receipt.phase_evidence.result_publication == null || receipt.phase_evidence.bridge_response_write == null)
            {
                reason = "identity_or_shape_invalid";
                return false;
            }
            if (!IsPhase(receipt.phase_evidence.project_queue_admission) || !IsPhase(receipt.phase_evidence.reference_resolution)
                || !IsPhase(receipt.phase_evidence.compilation) || !IsPhase(receipt.phase_evidence.main_thread_execution)
                || !IsPhase(receipt.phase_evidence.result_serialization) || !IsPhase(receipt.phase_evidence.result_publication)
                || !IsPhase(receipt.phase_evidence.bridge_response_write))
            {
                reason = "phase_evidence_invalid";
                return false;
            }
            if (receipt.terminal != receipt.result_available || (receipt.terminal && receipt.status != "completed" && receipt.status != "failed")
                || (!receipt.terminal && receipt.status != "running"))
            {
                reason = "terminal_state_invalid";
                return false;
            }
            if (receipt.result_available)
            {
                if (string.IsNullOrEmpty(receipt.command_result_json) || !IsSha256(receipt.command_result_hash)
                    || receipt.command_result_hash != Sha256Prefixed(receipt.command_result_json)
                    || (receipt.phase_evidence.result_publication.state != "completed" && receipt.phase_evidence.result_publication.state != "failed")
                    || !TerminalPhasesAreConsistent(receipt))
                {
                    reason = "result_integrity_invalid";
                    return false;
                }
            }
            else if (!string.IsNullOrEmpty(receipt.command_result_json) || !string.IsNullOrEmpty(receipt.command_result_hash))
            {
                reason = "nonterminal_result_present";
                return false;
            }
            if (!IsSha256(receipt.receipt_hash) || receipt.receipt_hash != ReceiptHash(receipt))
            {
                reason = "receipt_hash_mismatch";
                return false;
            }
            return true;
        }

        static bool TerminalPhasesAreConsistent(Receipt receipt)
        {
            var phases = receipt.phase_evidence;
            if (phases.project_queue_admission.state != "completed") return false;
            if (phases.result_publication.state == "failed")
            {
                return receipt.status == "failed" && TerminalExecutionPhasesFormPrefix(phases);
            }
            if (phases.reference_resolution.state == "failed")
            {
                return receipt.status == "failed" && phases.compilation.state == "not_started"
                    && phases.main_thread_execution.state == "not_started" && phases.result_serialization.state == "not_started";
            }
            if (phases.reference_resolution.state != "completed") return false;
            if (phases.compilation.state == "failed")
            {
                return receipt.status == "failed" && phases.main_thread_execution.state == "not_started" && phases.result_serialization.state == "not_started";
            }
            if (phases.compilation.state != "completed") return false;
            if (phases.main_thread_execution.state == "failed")
            {
                return receipt.status == "failed" && phases.result_serialization.state == "not_started";
            }
            if (phases.main_thread_execution.state != "completed") return false;
            if (phases.result_serialization.state == "failed") return receipt.status == "failed";
            return phases.result_serialization.state == "completed";
        }

        static bool TerminalExecutionPhasesFormPrefix(Phases phases)
        {
            var stopped = false;
            foreach (var phase in new[] { phases.reference_resolution, phases.compilation, phases.main_thread_execution, phases.result_serialization })
            {
                if (phase.state == "running" || (stopped && phase.state != "not_started")) return false;
                if (phase.state == "failed" || phase.state == "not_started") stopped = true;
            }
            return true;
        }

        static bool ExecutionMayHaveStarted(Receipt receipt)
        {
            var phases = receipt.phase_evidence;
            return phases.reference_resolution.state != "not_started" || phases.compilation.state != "not_started"
                || phases.main_thread_execution.state != "not_started" || phases.result_serialization.state != "not_started";
        }

        static bool SameBinding(Receipt left, Receipt right)
        {
            return left.schema_version == right.schema_version
                && string.Equals(left.contract, right.contract, StringComparison.Ordinal)
                && string.Equals(left.command, right.command, StringComparison.Ordinal)
                && string.Equals(left.operation_id, right.operation_id, StringComparison.Ordinal)
                && string.Equals(left.artifact_path, right.artifact_path, StringComparison.Ordinal)
                && string.Equals(left.project, right.project, StringComparison.Ordinal)
                && string.Equals(left.origin_instance_id, right.origin_instance_id, StringComparison.Ordinal)
                && left.origin_unity_pid == right.origin_unity_pid
                && string.Equals(left.origin_unity_process_start_utc, right.origin_unity_process_start_utc, StringComparison.Ordinal)
                && string.Equals(left.package_id, right.package_id, StringComparison.Ordinal)
                && string.Equals(left.package_version, right.package_version, StringComparison.Ordinal)
                && string.Equals(left.commands_hash, right.commands_hash, StringComparison.Ordinal)
                && left.catalog_generation == right.catalog_generation
                && string.Equals(left.caller_args_hash, right.caller_args_hash, StringComparison.Ordinal)
                && string.Equals(left.snippet_hash, right.snippet_hash, StringComparison.Ordinal)
                && string.Equals(left.created_at_utc, right.created_at_utc, StringComparison.Ordinal);
        }

        static bool OwnerMatches(Receipt receipt, BridgeRuntimeState state, string project, out string evidence)
        {
            var projectMatch = string.Equals(receipt.project, project ?? string.Empty, StringComparison.Ordinal);
            var instanceMatch = state != null && string.Equals(receipt.origin_instance_id, state.InstanceId, StringComparison.Ordinal);
            var processMatch = receipt.origin_unity_pid == ProcessId && string.Equals(receipt.origin_unity_process_start_utc, ProcessStartUtc, StringComparison.Ordinal);
            var packageMatch = string.Equals(receipt.package_version, UnityBridgeRuntime.PackageVersion, StringComparison.Ordinal);
            var catalogMatch = state != null && receipt.catalog_generation == state.CatalogGeneration && string.Equals(receipt.commands_hash, state.CommandsHash, StringComparison.Ordinal);
            evidence = "\"project_match\":" + JsonUtil.Bool(projectMatch)
                + ",\"instance_match\":" + JsonUtil.Bool(instanceMatch)
                + ",\"process_generation_match\":" + JsonUtil.Bool(processMatch)
                + ",\"package_match\":" + JsonUtil.Bool(packageMatch)
                + ",\"catalog_match\":" + JsonUtil.Bool(catalogMatch);
            return projectMatch && instanceMatch && processMatch && packageMatch && catalogMatch;
        }

        static CommandExecutionResult Correlate(CommandExecutionResult result, Receipt receipt)
        {
            var metadata = "\"operation_id\":" + JsonUtil.String(receipt.operation_id)
                + ",\"artifact_path\":" + JsonUtil.String(receipt.artifact_path)
                + ",\"caller_args_hash\":" + JsonUtil.String(receipt.caller_args_hash)
                + ",\"snippet_hash\":" + JsonUtil.String(receipt.snippet_hash)
                + ",\"terminal\":true,\"result_available\":true"
                + ",\"phase_evidence\":" + PhasesJson(receipt.phase_evidence)
                + ",\"status_lookup\":{\"command\":" + JsonUtil.String(StatusCommand) + ",\"operation_id\":" + JsonUtil.String(receipt.operation_id) + "}"
                + ",\"command_replayed\":false";
            if (result.Status == "ok")
            {
                return CommandExecutionResult.Ok(AppendObjectFields(result.DataJson, metadata));
            }
            var originalDetails = string.IsNullOrEmpty(result.ErrorDetailsJson) ? "null" : result.ErrorDetailsJson;
            return CommandExecutionResult.Error(result.ErrorCode, result.ErrorMessage, "{\"dynamic_csharp_readonly_operation\":{" + metadata + "},\"command_error_details\":" + originalDetails + "}");
        }

        static string StatusJson(Receipt receipt)
        {
            return "{\"operation_id\":" + JsonUtil.String(receipt.operation_id)
                + ",\"artifact_path\":" + JsonUtil.String(receipt.artifact_path)
                + ",\"status\":" + JsonUtil.String(receipt.status)
                + ",\"terminal\":" + JsonUtil.Bool(receipt.terminal)
                + ",\"result_available\":" + JsonUtil.Bool(receipt.result_available)
                + ",\"caller_args_hash\":" + JsonUtil.String(receipt.caller_args_hash)
                + ",\"snippet_hash\":" + JsonUtil.String(receipt.snippet_hash)
                + ",\"owner_binding\":{\"project\":" + JsonUtil.String(receipt.project)
                + ",\"instance_id\":" + JsonUtil.String(receipt.origin_instance_id)
                + ",\"unity_pid\":" + receipt.origin_unity_pid
                + ",\"unity_process_start_utc\":" + JsonUtil.String(receipt.origin_unity_process_start_utc)
                + ",\"package_id\":" + JsonUtil.String(receipt.package_id)
                + ",\"package_version\":" + JsonUtil.String(receipt.package_version)
                + ",\"commands_hash\":" + JsonUtil.String(receipt.commands_hash)
                + ",\"catalog_generation\":" + receipt.catalog_generation + "}"
                + ",\"phase_evidence\":" + PhasesJson(receipt.phase_evidence)
                + ",\"command_result\":" + (receipt.result_available ? receipt.command_result_json : "null")
                + ",\"command_replayed\":false}";
        }

        static string PhasesJson(Phases phases)
        {
            return "{\"project_queue_admission\":" + PhaseJson(phases.project_queue_admission)
                + ",\"reference_resolution\":" + PhaseJson(phases.reference_resolution)
                + ",\"compilation\":" + PhaseJson(phases.compilation)
                + ",\"main_thread_execution\":" + PhaseJson(phases.main_thread_execution)
                + ",\"result_serialization\":" + PhaseJson(phases.result_serialization)
                + ",\"result_publication\":" + PhaseJson(phases.result_publication)
                + ",\"bridge_response_write\":" + PhaseJson(phases.bridge_response_write) + "}";
        }

        static string PhaseJson(Phase phase)
        {
            return "{\"state\":" + JsonUtil.String(phase.state) + ",\"duration_ms\":" + Math.Max(0, phase.duration_ms) + "}";
        }

        static string CommandResultJson(CommandExecutionResult result)
        {
            if (result.Status == "ok")
            {
                return "{\"status\":\"ok\",\"data\":" + result.DataJson + "}";
            }
            return "{\"status\":\"error\",\"error\":{\"code\":" + JsonUtil.String(result.ErrorCode)
                + ",\"message\":" + JsonUtil.String(result.ErrorMessage)
                + (string.IsNullOrEmpty(result.ErrorDetailsJson) ? string.Empty : ",\"details\":" + result.ErrorDetailsJson) + "}}";
        }

        static string AppendObjectFields(string json, string fields)
        {
            var value = string.IsNullOrEmpty(json) ? "{}" : json.Trim();
            if (value.Length >= 2 && value[0] == '{' && value[value.Length - 1] == '}')
            {
                return value.Length == 2 ? "{" + fields + "}" : value.Substring(0, value.Length - 1) + "," + fields + "}";
            }
            return "{\"value\":" + value + "," + fields + "}";
        }

        static CommandExecutionResult StoreError(string operationId, string reason, bool executionMayHaveStarted, ArtifactCapacityObservation capacityObservation = null)
        {
            return CommandExecutionResult.Error("dynamic_csharp_result_unavailable", "Dynamic C# read-only result store is unavailable", "{\"operation_id\":" + JsonUtil.String(operationId ?? string.Empty)
                + ",\"reason\":" + JsonUtil.String(reason ?? "storage_unavailable")
                + ",\"execution_may_have_started\":" + JsonUtil.Bool(executionMayHaveStarted)
                + ",\"command_replayed\":false"
                + (capacityObservation == null ? string.Empty : ",\"capacity_observation\":" + CapacityObservationJson(capacityObservation)) + "}");
        }

        static string CapacityObservationJson(ArtifactCapacityObservation observation)
        {
            return "{\"observation_source\":\"matching_file_enumeration\",\"observed_matching_files\":" + observation.ObservedMatchingFiles
                + ",\"enumeration_complete\":" + JsonUtil.Bool(observation.EnumerationComplete)
                + ",\"atomic_snapshot\":false,\"max_retained_artifacts\":" + MaxArtifacts
                + ",\"max_matching_files_scan\":" + MaxArtifactScan
                + ",\"automatic_retention_cleanup_enabled\":false,\"automatic_release_eligible_count\":0"
                + ",\"automatic_release_eligibility_source\":\"disabled_by_contract\""
                + ",\"receipt_state_classification\":\"not_performed\",\"receipt_age_classification\":\"not_performed\"}";
        }

        static string CorrelationFailureJson(string operationId, bool executionMayHaveStarted)
        {
            return "{\"operation_id\":" + JsonUtil.String(operationId ?? string.Empty)
                + ",\"execution_may_have_started\":" + JsonUtil.Bool(executionMayHaveStarted)
                + ",\"command_replayed\":false}";
        }

        static bool TryPhase(Receipt receipt, string name, out Phase phase)
        {
            phase = name == "reference_resolution" ? receipt.phase_evidence.reference_resolution
                : name == "compilation" ? receipt.phase_evidence.compilation
                : name == "main_thread_execution" ? receipt.phase_evidence.main_thread_execution
                : name == "result_serialization" ? receipt.phase_evidence.result_serialization
                : null;
            return phase != null;
        }

        static bool IsPhase(Phase phase)
        {
            return phase != null && phase.duration_ms >= 0
                && (phase.state == "not_started" || phase.state == "running" || phase.state == "completed" || phase.state == "failed");
        }

        static string ReceiptHash(Receipt receipt)
        {
            return Sha256Prefixed(receipt.schema_version + "\n" + receipt.contract + "\n" + receipt.command + "\n" + receipt.operation_id + "\n"
                + receipt.artifact_path + "\n" + receipt.project + "\n" + receipt.origin_instance_id + "\n" + receipt.origin_unity_pid + "\n"
                + receipt.origin_unity_process_start_utc + "\n" + receipt.package_id + "\n" + receipt.package_version + "\n" + receipt.commands_hash + "\n"
                + receipt.catalog_generation + "\n" + receipt.caller_args_hash + "\n" + receipt.snippet_hash + "\n" + receipt.created_at_utc + "\n"
                + receipt.updated_at_utc + "\n" + receipt.status + "\n" + receipt.terminal + "\n" + receipt.result_available + "\n"
                + receipt.command_result_hash + "\n" + PhasesJson(receipt.phase_evidence));
        }

        static string Sha256Prefixed(string value)
        {
            using (var sha256 = SHA256.Create())
            {
                return "sha256:" + BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty))).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        static bool IsSha256(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal)) return false;
            for (var index = 7; index < value.Length; index++)
            {
                var character = value[index];
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))) return false;
            }
            return true;
        }

        static bool IsOperationId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 31 || !value.StartsWith("csharp-", StringComparison.Ordinal)) return false;
            for (var index = 7; index < value.Length; index++)
            {
                if (index == 15 || index == 22)
                {
                    if (value[index] != '-') return false;
                    continue;
                }
                var character = value[index];
                if (index < 22)
                {
                    if (character < '0' || character > '9') return false;
                }
                else if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))) return false;
            }
            return true;
        }

        static bool TryResolveArtifactPath(string artifactPath, out string fullPath, out string error)
        {
            fullPath = string.Empty;
            error = string.Empty;
            try
            {
                if (string.IsNullOrEmpty(artifactPath) || Path.IsPathRooted(artifactPath) || artifactPath.IndexOf('\0') >= 0)
                {
                    error = "artifact_path_not_allowed";
                    return false;
                }
                var normalized = artifactPath.Replace('\\', '/');
                if (!normalized.StartsWith(ArtifactRoot + "/", StringComparison.Ordinal) || normalized.Contains("../") || normalized.Contains("/..") || normalized.IndexOf(':') >= 0)
                {
                    error = "artifact_path_not_allowed";
                    return false;
                }
                var projectRoot = Path.GetFullPath(ProjectPaths.ProjectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var artifactRoot = Path.GetFullPath(Path.Combine(projectRoot, ArtifactRoot.Replace('/', Path.DirectorySeparatorChar))).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                fullPath = Path.GetFullPath(Path.Combine(projectRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
                var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!SameOrChild(artifactRoot, projectRoot, comparison) || !SameOrChild(fullPath, artifactRoot, comparison) || HasReparsePoint(fullPath, artifactRoot, comparison) || HasReparsePoint(artifactRoot, projectRoot, comparison))
                {
                    fullPath = string.Empty;
                    error = "artifact_path_not_allowed";
                    return false;
                }
                return true;
            }
            catch
            {
                fullPath = string.Empty;
                error = "artifact_path_validation_failed";
                return false;
            }
        }

        static bool SameOrChild(string path, string root, StringComparison comparison)
        {
            return string.Equals(path, root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
        }

        static bool HasReparsePoint(string path, string stopRoot, StringComparison comparison)
        {
            var current = File.Exists(path) ? Path.GetDirectoryName(path) : path;
            while (!string.IsNullOrEmpty(current) && SameOrChild(current, stopRoot, comparison))
            {
                if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
                if (string.Equals(current, stopRoot, comparison)) break;
                current = Path.GetDirectoryName(current);
            }
            return false;
        }

        static string ReadProcessStartUtc()
        {
            try
            {
                using (var process = System.Diagnostics.Process.GetCurrentProcess())
                {
                    return process.StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                }
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
