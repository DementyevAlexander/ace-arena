using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SGG.UnityMCP.Editor.Bridge;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class ConsoleLogBuffer
    {
        const int MaxEntries = 1000;
        const int MaxCompileErrors = 100;
        const int MaxCompileWarnings = 200;
        const int MaxMessageChars = 8000;
        const int MaxStackChars = 12000;
        const int MaxPrefabImportObservationChars = 12288;
        const string PrefabImportObservationKey = "SGG.UnityMCP.PrefabImportObservation.v1";
        const string BufferMetadataKey = "SGG.UnityMCP.ConsoleLogBuffer.Metadata.v1";
        static readonly object Gate = new object();
        static readonly Queue<ConsoleLogEntry> Entries = new Queue<ConsoleLogEntry>();
        static readonly List<CompileDiagnosticEntry> CompileErrors = new List<CompileDiagnosticEntry>();
        static readonly List<CompileDiagnosticEntry> CompileWarnings = new List<CompileDiagnosticEntry>();
        static bool s_initialized;
        static string s_prefabImportOperationId = string.Empty;
        static long s_prefabImportTimestampTicks;
        static string s_prefabImportObservationPayload = string.Empty;
        static string s_prefabImportDiagnosticsJson = string.Empty;
        static string s_bufferEpoch = string.Empty;
        static string s_bufferInitializedAtUtc = string.Empty;
        static bool s_bufferResetLoss;
        static readonly CompilationLifecycleState CompilationLifecycle = new CompilationLifecycleState();

        public static void Initialize()
        {
            if (s_initialized)
            {
                return;
            }

            s_initialized = true;
            InitializeBufferMetadata();
            LoadPrefabImportObservation();
            Application.logMessageReceivedThreaded += OnLogMessageReceived;
            Application.logMessageReceived += OnMainThreadLogMessageReceived;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        public static bool HasCompileErrors()
        {
            lock (Gate)
            {
                return CompileErrors.Count > 0;
            }
        }

        public static int CompileErrorCount()
        {
            lock (Gate)
            {
                return CompileErrors.Count;
            }
        }

        public static int CompileWarningCount()
        {
            lock (Gate)
            {
                return CompileWarnings.Count;
            }
        }

        internal static CompilationLifecycleSnapshot CompilationLifecycleSnapshot()
        {
            lock (Gate)
            {
                return CompilationLifecycle.Snapshot();
            }
        }

        public static string CompilationLifecycleJson()
        {
            return CompilationLifecycleSnapshot().ToJson();
        }

        public static string CompileDiagnosticsJson(int limit, int maxMessageChars, int maxStackChars)
        {
            limit = Math.Max(1, Math.Min(MaxCompileErrors, limit));
            maxMessageChars = Math.Max(1, Math.Min(MaxMessageChars, maxMessageChars));
            maxStackChars = Math.Max(1, Math.Min(MaxStackChars, maxStackChars));

            CompileDiagnosticEntry[] snapshot;
            CompilationLifecycleSnapshot lifecycle;
            lock (Gate)
            {
                snapshot = CompileErrors.ToArray();
                lifecycle = CompilationLifecycle.Snapshot();
            }

            var start = Math.Max(0, snapshot.Length - limit);
            var builder = new StringBuilder();
            builder.Append("{\"has_compile_errors\":").Append(JsonUtil.Bool(snapshot.Length > 0));
            builder.Append(",\"compile_error_count\":").Append(snapshot.Length);
            builder.Append(",\"compilation_generation\":").Append(lifecycle.Generation);
            builder.Append(",\"completed_compilation_generation\":").Append(lifecycle.CompletedGeneration);
            builder.Append(",\"compilation_evidence\":").Append(JsonUtil.String(lifecycle.Evidence));
            builder.Append(",\"last_compilation_started_utc\":").Append(JsonUtil.String(ToTimestamp(lifecycle.StartedUtc)));
            builder.Append(",\"last_compilation_finished_utc\":").Append(JsonUtil.String(ToTimestamp(lifecycle.FinishedUtc)));
            builder.Append(",\"truncated\":").Append(JsonUtil.Bool(snapshot.Length > limit));
            builder.Append(",\"errors\":[");
            for (var index = start; index < snapshot.Length; index++)
            {
                if (index > start)
                {
                    builder.Append(',');
                }

                var entry = snapshot[index];
                AppendCompileDiagnosticEntryJson(builder, entry, maxMessageChars, maxStackChars, false);
            }

            builder.Append("]}");
            return builder.ToString();
        }

        public static string CompileWarningsJson(int limit, int maxMessageChars, int maxStackChars)
        {
            limit = Math.Max(1, Math.Min(MaxCompileWarnings, limit));
            maxMessageChars = Math.Max(1, Math.Min(MaxMessageChars, maxMessageChars));
            maxStackChars = Math.Max(1, Math.Min(MaxStackChars, maxStackChars));

            CompileDiagnosticEntry[] snapshot;
            CompilationLifecycleSnapshot lifecycle;
            lock (Gate)
            {
                snapshot = CompileWarnings.ToArray();
                lifecycle = CompilationLifecycle.Snapshot();
            }

            var start = Math.Max(0, snapshot.Length - limit);
            var returnedCount = Math.Max(0, snapshot.Length - start);
            var builder = new StringBuilder();
            builder.Append("{\"status\":\"ok\",\"has_compile_warnings\":").Append(JsonUtil.Bool(snapshot.Length > 0));
            builder.Append(",\"compile_warning_count\":").Append(snapshot.Length);
            builder.Append(",\"compilation_generation\":").Append(lifecycle.Generation);
            builder.Append(",\"completed_compilation_generation\":").Append(lifecycle.CompletedGeneration);
            builder.Append(",\"compilation_evidence\":").Append(JsonUtil.String(lifecycle.Evidence));
            builder.Append(",\"last_compilation_started_utc\":").Append(JsonUtil.String(ToTimestamp(lifecycle.StartedUtc)));
            builder.Append(",\"last_compilation_finished_utc\":").Append(JsonUtil.String(ToTimestamp(lifecycle.FinishedUtc)));
            builder.Append(",\"limit\":").Append(limit);
            builder.Append(",\"returned_count\":").Append(returnedCount);
            builder.Append(",\"truncated\":").Append(JsonUtil.Bool(snapshot.Length > limit));
            builder.Append(",\"read_only\":true");
            builder.Append(",\"warnings\":[");
            for (var index = start; index < snapshot.Length; index++)
            {
                if (index > start)
                {
                    builder.Append(',');
                }

                var entry = snapshot[index];
                AppendCompileDiagnosticEntryJson(builder, entry, maxMessageChars, maxStackChars, true);
            }

            builder.Append("],\"next_valid_actions\":[\"compile.status\",\"logs.console.query\"]}");
            return builder.ToString();
        }

        static void AppendCompileDiagnosticEntryJson(StringBuilder builder, CompileDiagnosticEntry entry, int maxMessageChars, int maxStackChars, bool includeAssemblyNames)
        {
            builder.Append("{\"timestamp\":").Append(JsonUtil.String(ToTimestamp(entry.TimestampUtc)));
            var severity = SafeCompileSeverity(entry.Severity);
            if (!string.IsNullOrEmpty(severity))
            {
                builder.Append(",\"severity\":").Append(JsonUtil.String(severity));
            }

            var code = SafeCompilerCode(entry.Code);
            if (!string.IsNullOrEmpty(code))
            {
                builder.Append(",\"code\":").Append(JsonUtil.String(code));
            }

            builder.Append(",\"source\":").Append(JsonUtil.String(entry.Source));
            if (includeAssemblyNames)
            {
                builder.Append(",\"assembly\":").Append(JsonUtil.String(entry.AssemblyName));
                builder.Append(",\"assembly_name\":").Append(JsonUtil.String(entry.AssemblyName));
            }

            builder.Append(",\"assembly_path\":").Append(JsonUtil.String(entry.AssemblyPath));
            builder.Append(",\"file\":").Append(JsonUtil.String(entry.File));
            builder.Append(",\"line\":").Append(entry.Line);
            builder.Append(",\"column\":").Append(entry.Column);
            builder.Append(",\"message\":").Append(JsonUtil.String(Truncate(entry.Message, maxMessageChars)));
            if (!string.IsNullOrEmpty(entry.StackTrace))
            {
                builder.Append(",\"stack_trace\":").Append(JsonUtil.String(Truncate(entry.StackTrace, maxStackChars)));
            }

            builder.Append('}');
        }

        static string SafeCompileSeverity(string severity)
        {
            if (string.Equals(severity, "error", StringComparison.Ordinal)
                || string.Equals(severity, "warning", StringComparison.Ordinal))
            {
                return severity;
            }

            return string.Empty;
        }

        static string SafeCompilerCode(string code)
        {
            if (string.IsNullOrEmpty(code) || code.Length < 2 || code.Length > 16)
            {
                return string.Empty;
            }

            var index = 0;
            while (index < code.Length && index < 8 && code[index] >= 'A' && code[index] <= 'Z')
            {
                index++;
            }

            if (index == 0 || index >= code.Length)
            {
                return string.Empty;
            }

            var digitStart = index;
            while (index < code.Length && index - digitStart < 8 && code[index] >= '0' && code[index] <= '9')
            {
                index++;
            }

            return index == code.Length && index > digitStart ? code : string.Empty;
        }

        internal static string CompileDiagnosticEntryJsonForTests(string severity, string code, string message, string stackTrace, int maxMessageChars, int maxStackChars)
        {
            var entry = new CompileDiagnosticEntry
            {
                TimestampUtc = new DateTime(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc),
                Severity = severity ?? string.Empty,
                Code = code ?? string.Empty,
                Source = "compilation_pipeline",
                AssemblyPath = "Library/ScriptAssemblies/Game.dll",
                AssemblyName = "Game",
                File = "Assets/Test.cs",
                Line = 12,
                Column = 3,
                Message = message ?? string.Empty,
                StackTrace = stackTrace ?? string.Empty,
            };
            var builder = new StringBuilder();
            AppendCompileDiagnosticEntryJson(
                builder,
                entry,
                Math.Max(1, Math.Min(MaxMessageChars, maxMessageChars)),
                Math.Max(1, Math.Min(MaxStackChars, maxStackChars)),
                string.Equals(severity, "warning", StringComparison.Ordinal));
            return builder.ToString();
        }

        public static string EntriesJson(int limit)
        {
            return "{\"entries\":" + EntriesArrayJson(limit) + ",\"buffer\":" + BufferMetadataJson() + "}";
        }

        public static string BufferMetadataJson()
        {
            lock (Gate)
            {
                return BuildBufferMetadataJson(s_bufferEpoch, s_bufferInitializedAtUtc, s_bufferResetLoss);
            }
        }

        public static string EntriesArrayJson(int limit)
        {
            limit = Math.Max(1, Math.Min(MaxEntries, limit));
            ConsoleLogEntry[] snapshot;
            lock (Gate)
            {
                snapshot = Entries.ToArray();
            }

            var start = Math.Max(0, snapshot.Length - limit);
            var builder = new StringBuilder();
            builder.Append('[');
            for (var index = start; index < snapshot.Length; index++)
            {
                if (index > start)
                {
                    builder.Append(',');
                }

                var entry = snapshot[index];
                builder.Append("{\"timestamp\":").Append(JsonUtil.String(entry.Timestamp));
                builder.Append(",\"type\":").Append(JsonUtil.String(entry.Type));
                builder.Append(",\"message\":").Append(JsonUtil.String(entry.Message));
                if (!string.IsNullOrEmpty(entry.StackTrace))
                {
                    builder.Append(",\"stack_trace\":").Append(JsonUtil.String(entry.StackTrace));
                }

                builder.Append('}');
            }

            builder.Append(']');
            return builder.ToString();
        }

        public static string ReadJson(int limit, string types, string filterText, bool includeStackTrace, int maxMessageChars, int maxStackChars)
        {
            limit = Math.Max(1, Math.Min(MaxEntries, limit));
            maxMessageChars = Math.Max(1, Math.Min(MaxMessageChars, maxMessageChars));
            maxStackChars = Math.Max(1, Math.Min(MaxStackChars, maxStackChars));
            ConsoleLogEntry[] snapshot;
            lock (Gate)
            {
                snapshot = Entries.ToArray();
            }

            var matched = new List<ConsoleLogEntry>();
            for (var index = 0; index < snapshot.Length; index++)
            {
                var entry = snapshot[index];
                if (!MatchesTypeFilter(entry.Type, types))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(filterText) && entry.Message.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                matched.Add(entry);
            }

            var start = Math.Max(0, matched.Count - limit);
            var builder = new StringBuilder();
            builder.Append("{\"entries\":[");
            for (var index = start; index < matched.Count; index++)
            {
                if (index > start)
                {
                    builder.Append(',');
                }

                var entry = matched[index];
                builder.Append("{\"timestamp\":").Append(JsonUtil.String(entry.Timestamp));
                builder.Append(",\"type\":").Append(JsonUtil.String(entry.Type));
                builder.Append(",\"message\":").Append(JsonUtil.String(Truncate(entry.Message, maxMessageChars)));
                if (includeStackTrace && !string.IsNullOrEmpty(entry.StackTrace))
                {
                    builder.Append(",\"stack_trace\":").Append(JsonUtil.String(Truncate(entry.StackTrace, maxStackChars)));
                }

                builder.Append('}');
            }

            builder.Append("],\"count\":").Append(Math.Max(0, matched.Count - start));
            builder.Append(",\"matched_count\":").Append(matched.Count);
            builder.Append(",\"limit\":").Append(limit);
            builder.Append(",\"truncated\":").Append(JsonUtil.Bool(matched.Count > limit));
            builder.Append(",\"cleared\":false");
            builder.Append(",\"buffer\":").Append(BufferMetadataJson()).Append('}');
            return builder.ToString();
        }

        public static string QueryJson(int limit, string severity, string contains, string since, bool includeStackTrace, int maxChars)
        {
            limit = Math.Max(1, Math.Min(500, limit));
            maxChars = Math.Max(1000, Math.Min(100000, maxChars));
            severity = string.IsNullOrWhiteSpace(severity) ? "all" : severity.Trim();
            contains = Truncate(contains ?? string.Empty, 256);
            since = Truncate((since ?? string.Empty).Trim(), 64);
            var hasSince = TryParseSinceUtc(since, out var sinceUtc);

            ConsoleLogEntry[] snapshot;
            lock (Gate)
            {
                snapshot = Entries.ToArray();
            }

            var matched = new List<ConsoleLogEntry>();
            for (var index = 0; index < snapshot.Length; index++)
            {
                var entry = snapshot[index];
                if (!MatchesTypeFilter(entry.Type, severity))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(contains) && entry.Message.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (hasSince && entry.TimestampUtc < sinceUtc)
                {
                    continue;
                }

                matched.Add(entry);
            }

            var start = Math.Max(0, matched.Count - limit);
            var returnedCount = 0;
            var remainingChars = maxChars;
            var charsTruncated = false;
            var builder = new StringBuilder();
            builder.Append("{\"status\":\"ok\",\"logs\":[");
            for (var index = start; index < matched.Count; index++)
            {
                if (remainingChars <= 0)
                {
                    charsTruncated = true;
                    break;
                }

                if (returnedCount > 0)
                {
                    builder.Append(',');
                }

                var entry = matched[index];
                var message = TakeChars(entry.Message, ref remainingChars, ref charsTruncated);
                builder.Append("{\"timestamp\":").Append(JsonUtil.String(entry.Timestamp));
                builder.Append(",\"type\":").Append(JsonUtil.String(entry.Type));
                builder.Append(",\"severity\":").Append(JsonUtil.String(entry.Type));
                builder.Append(",\"message\":").Append(JsonUtil.String(message));
                if (includeStackTrace && !string.IsNullOrEmpty(entry.StackTrace) && remainingChars > 0)
                {
                    builder.Append(",\"stack_trace\":").Append(JsonUtil.String(TakeChars(entry.StackTrace, ref remainingChars, ref charsTruncated)));
                }

                builder.Append('}');
                returnedCount++;
            }

            var resultTruncated = matched.Count > limit || charsTruncated;
            builder.Append(']');
            builder.Append(",\"count\":").Append(returnedCount);
            builder.Append(",\"returned_count\":").Append(returnedCount);
            builder.Append(",\"matched_count\":").Append(matched.Count);
            builder.Append(",\"limit\":").Append(limit);
            builder.Append(",\"truncated\":").Append(JsonUtil.Bool(resultTruncated));
            builder.Append(",\"chars_truncated\":").Append(JsonUtil.Bool(charsTruncated));
            builder.Append(",\"cleared\":false");
            builder.Append(",\"source_available\":false");
            builder.Append(",\"source_note\":\"Unity Application.logMessageReceived does not provide a source field; use message/stack filters when source is needed.\"");
            builder.Append(",\"filters\":{\"severity\":").Append(JsonUtil.String(severity));
            builder.Append(",\"contains\":").Append(JsonUtil.String(contains));
            builder.Append(",\"since\":").Append(JsonUtil.String(since));
            builder.Append(",\"since_valid\":").Append(JsonUtil.Bool(string.IsNullOrEmpty(since) || hasSince));
            builder.Append(",\"since_utc\":").Append(JsonUtil.String(hasSince ? sinceUtc.ToString("O") : string.Empty));
            builder.Append(",\"include_stack_trace\":").Append(JsonUtil.Bool(includeStackTrace));
            builder.Append(",\"max_chars\":").Append(maxChars).Append('}');
            builder.Append(",\"buffer\":").Append(BufferMetadataJson());
            builder.Append(",\"next_actions\":[\"increase_limit_up_to_500\",\"narrow_severity_contains_or_since\",\"rerun_with_include_stack_trace\"]}");
            return builder.ToString();
        }

        static void InitializeBufferMetadata()
        {
            var previousMarker = SessionState.GetString(BufferMetadataKey, string.Empty);
            var epoch = Guid.NewGuid().ToString("N");
            var initializedAtUtc = DateTime.UtcNow.ToString("O");
            lock (Gate)
            {
                s_bufferEpoch = epoch;
                s_bufferInitializedAtUtc = initializedAtUtc;
                s_bufferResetLoss = IsValidBufferMetadataMarker(previousMarker);
            }
            SessionState.SetString(BufferMetadataKey, BuildBufferMetadataMarker(epoch));
        }

        static string BuildBufferMetadataMarker(string epoch)
        {
            return "v1|" + (epoch ?? string.Empty);
        }

        static bool IsValidBufferMetadataMarker(string marker)
        {
            if (string.IsNullOrEmpty(marker) || !marker.StartsWith("v1|", StringComparison.Ordinal))
            {
                return false;
            }

            return Guid.TryParseExact(marker.Substring(3), "N", out _);
        }

        static string BuildBufferMetadataJson(string epoch, string initializedAtUtc, bool resetLoss)
        {
            return "{\"epoch\":" + JsonUtil.String(epoch ?? string.Empty)
                + ",\"initialized_at_utc\":" + JsonUtil.String(initializedAtUtc ?? string.Empty)
                + ",\"reset_loss\":" + JsonUtil.Bool(resetLoss)
                + ",\"history_complete\":false}";
        }

        internal static string BufferMetadataMarkerForTests(string epoch)
        {
            return BuildBufferMetadataMarker(epoch);
        }

        internal static string BufferMetadataJsonForTests(string previousMarker, string epoch, string initializedAtUtc)
        {
            return BuildBufferMetadataJson(epoch, initializedAtUtc, IsValidBufferMetadataMarker(previousMarker));
        }

        internal static void BeginPrefabImportObservation(string operationId)
        {
            lock (Gate)
            {
                s_prefabImportOperationId = operationId ?? string.Empty;
                s_prefabImportTimestampTicks = 0;
                s_prefabImportObservationPayload = string.Empty;
                s_prefabImportDiagnosticsJson = string.Empty;
            }
            SessionState.SetString(PrefabImportObservationKey, string.Empty);
        }

        internal static string PrefabImportDiagnosticsJson(DateTime sinceUtc, DateTime untilUtc, string operationId)
        {
            if (sinceUtc == DateTime.MinValue || untilUtc < sinceUtc)
            {
                return string.Empty;
            }
            ConsoleLogEntry[] snapshot;
            string persistedOperationId;
            long persistedTimestampTicks;
            string persistedObservationPayload;
            string persistedDiagnosticsJson;
            lock (Gate)
            {
                snapshot = Entries.ToArray();
                persistedOperationId = s_prefabImportOperationId;
                persistedTimestampTicks = s_prefabImportTimestampTicks;
                persistedObservationPayload = s_prefabImportObservationPayload;
                persistedDiagnosticsJson = s_prefabImportDiagnosticsJson;
            }

            var messages = new List<string>();
            for (var index = snapshot.Length - 1; index >= 0; index--)
            {
                var entry = snapshot[index];
                if (entry.TimestampUtc < sinceUtc || entry.TimestampUtc > untilUtc
                    || (!string.Equals(entry.Type, LogType.Error.ToString(), StringComparison.Ordinal)
                        && !string.Equals(entry.Type, LogType.Exception.ToString(), StringComparison.Ordinal)))
                {
                    continue;
                }

                messages.Add(entry.Message);
            }

            var currentPayload = PrefabYamlIntegrity.PrefabImportObservationPayload(messages);
            var persistedEligible = string.Equals(persistedOperationId, operationId, StringComparison.Ordinal)
                && persistedTimestampTicks >= sinceUtc.Ticks
                && persistedTimestampTicks <= untilUtc.Ticks
                && !string.IsNullOrEmpty(persistedDiagnosticsJson);
            if (persistedEligible)
            {
                currentPayload = PrefabYamlIntegrity.MergePrefabImportObservationPayloads(persistedObservationPayload, currentPayload);
            }
            return PrefabYamlIntegrity.PrefabImportDiagnosticsJsonFromObservationPayload(currentPayload);
        }

        static void OnMainThreadLogMessageReceived(string condition, string stackTrace, LogType type)
        {
            if ((type != LogType.Error && type != LogType.Exception) || string.IsNullOrEmpty(condition))
            {
                return;
            }

            if (!PrefabYamlIntegrity.TryBuildPrefabImportObservationPayload(condition, out var observationPayload))
            {
                return;
            }

            var barrier = ExplicitCompilationBarrier.Snapshot(string.Empty, false);
            var operation = barrier.Operation;
            var timestampTicks = DateTime.UtcNow.Ticks;
            if (operation == null || !operation.RequestIssued || operation.RequestIssuedTicks <= 0 || timestampTicks < operation.RequestIssuedTicks
                || (barrier.State != "pending" && barrier.State != "verified"))
            {
                return;
            }
            if (operation.Finished && operation.FinishedTicks > 0 && timestampTicks > operation.FinishedTicks)
            {
                return;
            }

            string priorPayload;
            lock (Gate)
            {
                priorPayload = string.Equals(s_prefabImportOperationId, operation.OperationId, StringComparison.Ordinal)
                    ? s_prefabImportObservationPayload
                    : string.Empty;
            }
            observationPayload = PrefabYamlIntegrity.MergePrefabImportObservationPayloads(priorPayload, observationPayload);
            var diagnosticsJson = PrefabYamlIntegrity.PrefabImportDiagnosticsJsonFromObservationPayload(observationPayload);
            var blob = BuildPrefabImportObservationBlob(operation.OperationId, timestampTicks, observationPayload);
            if (string.IsNullOrEmpty(blob))
            {
                return;
            }
            lock (Gate)
            {
                s_prefabImportOperationId = operation.OperationId;
                s_prefabImportTimestampTicks = timestampTicks;
                s_prefabImportObservationPayload = observationPayload;
                s_prefabImportDiagnosticsJson = diagnosticsJson;
            }
            SessionState.SetString(PrefabImportObservationKey, blob);
        }

        static void LoadPrefabImportObservation()
        {
            var blob = SessionState.GetString(PrefabImportObservationKey, string.Empty);
            if (!TryParsePrefabImportObservationBlob(blob, out var operationId, out var timestampTicks, out var observationPayload, out var diagnosticsJson))
            {
                operationId = string.Empty;
                timestampTicks = 0;
                observationPayload = string.Empty;
                diagnosticsJson = string.Empty;
            }
            lock (Gate)
            {
                s_prefabImportOperationId = operationId;
                s_prefabImportTimestampTicks = timestampTicks;
                s_prefabImportObservationPayload = observationPayload;
                s_prefabImportDiagnosticsJson = diagnosticsJson;
            }
        }

        static string BuildPrefabImportObservationBlob(string operationId, long timestampTicks, string observationPayload)
        {
            if (!ExplicitCompilationBarrierState.IsValidOperationId(operationId) || timestampTicks <= 0
                || string.IsNullOrEmpty(observationPayload) || observationPayload.Length > MaxPrefabImportObservationChars)
            {
                return string.Empty;
            }
            return "v1|" + operationId + "|" + timestampTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|"
                + Convert.ToBase64String(Encoding.UTF8.GetBytes(observationPayload));
        }

        static bool TryParsePrefabImportObservationBlob(string blob, out string operationId, out long timestampTicks, out string observationPayload, out string diagnosticsJson)
        {
            operationId = string.Empty;
            timestampTicks = 0;
            observationPayload = string.Empty;
            diagnosticsJson = string.Empty;
            if (string.IsNullOrEmpty(blob) || blob.Length > MaxPrefabImportObservationChars * 2)
            {
                return false;
            }
            var parts = blob.Split(new[] { '|' }, 4);
            if (parts.Length != 4 || parts[0] != "v1" || !ExplicitCompilationBarrierState.IsValidOperationId(parts[1])
                || !long.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out timestampTicks)
                || timestampTicks <= 0 || timestampTicks > DateTime.UtcNow.Ticks)
            {
                return false;
            }
            try
            {
                observationPayload = Encoding.UTF8.GetString(Convert.FromBase64String(parts[3]));
            }
            catch (FormatException)
            {
                return false;
            }
            diagnosticsJson = PrefabYamlIntegrity.PrefabImportDiagnosticsJsonFromObservationPayload(observationPayload);
            if (string.IsNullOrEmpty(diagnosticsJson) || diagnosticsJson.Length > MaxPrefabImportObservationChars)
            {
                return false;
            }
            operationId = parts[1];
            return true;
        }

        internal static string PrefabImportObservationBlobForTests(string operationId, long timestampTicks, string observationPayload)
        {
            return BuildPrefabImportObservationBlob(operationId, timestampTicks, observationPayload);
        }

        internal static string PrefabImportObservationJsonForTests(string blob, string operationId, long sinceTicks)
        {
            return TryParsePrefabImportObservationBlob(blob, out var parsedOperationId, out var timestampTicks, out _, out var diagnosticsJson)
                && string.Equals(parsedOperationId, operationId, StringComparison.Ordinal)
                && timestampTicks >= sinceTicks
                ? diagnosticsJson
                : string.Empty;
        }

        static bool TryParseSinceUtc(string value, out DateTime sinceUtc)
        {
            sinceUtc = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (!DateTimeOffset.TryParse(value, out var parsed))
            {
                return false;
            }

            sinceUtc = parsed.UtcDateTime;
            return true;
        }

        static bool MatchesTypeFilter(string type, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter) || ContainsFilterToken(filter, "all"))
            {
                return true;
            }

            var normalizedType = (type ?? string.Empty).Trim().ToLowerInvariant();
            if (ContainsFilterToken(filter, normalizedType))
            {
                return true;
            }

            return (normalizedType == "exception" || normalizedType == "assert") && ContainsFilterToken(filter, "error");
        }

        static bool ContainsFilterToken(string filter, string token)
        {
            var parts = filter.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < parts.Length; index++)
            {
                if (string.Equals(parts[index].Trim(), token, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        static string Truncate(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            {
                return value ?? string.Empty;
            }

            return value.Substring(0, maxChars);
        }

        static string TakeChars(string value, ref int remainingChars, ref bool truncated)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            if (value.Length <= remainingChars)
            {
                remainingChars -= value.Length;
                return value;
            }

            var taken = Math.Max(0, remainingChars);
            remainingChars = 0;
            truncated = true;
            return value.Substring(0, taken);
        }

        static void OnLogMessageReceived(string condition, string stackTrace, LogType type)
        {
            var timestampUtc = DateTime.UtcNow;
            lock (Gate)
            {
                while (Entries.Count >= MaxEntries)
                {
                    Entries.Dequeue();
                }

                Entries.Enqueue(new ConsoleLogEntry
                {
                    Timestamp = timestampUtc.ToString("O"),
                    TimestampUtc = timestampUtc,
                    Type = type.ToString(),
                    Message = condition ?? string.Empty,
                    StackTrace = stackTrace ?? string.Empty,
                });

                if (IsCompilerErrorLog(condition, type, out var file, out var line, out var column))
                {
                    AddCompileErrorLocked(new CompileDiagnosticEntry
                    {
                        TimestampUtc = timestampUtc,
                        Severity = "error",
                        Code = ExtractCompilerCode(condition, "error CS"),
                        Source = "console_log",
                        AssemblyPath = string.Empty,
                        AssemblyName = string.Empty,
                        File = file,
                        Line = line,
                        Column = column,
                        Message = condition ?? string.Empty,
                        StackTrace = stackTrace ?? string.Empty,
                    });
                }

                if (IsCompilerWarningLog(condition, type, out file, out line, out column))
                {
                    AddCompileWarningLocked(new CompileDiagnosticEntry
                    {
                        TimestampUtc = timestampUtc,
                        Severity = "warning",
                        Code = ExtractCompilerCode(condition, "warning CS"),
                        Source = "console_log",
                        AssemblyPath = string.Empty,
                        AssemblyName = string.Empty,
                        File = file,
                        Line = line,
                        Column = column,
                        Message = condition ?? string.Empty,
                        StackTrace = stackTrace ?? string.Empty,
                    });
                }
            }
        }

        static void OnCompilationStarted(object context)
        {
            lock (Gate)
            {
                CompileErrors.Clear();
                CompileWarnings.Clear();
                CompilationLifecycle.Start(DateTime.UtcNow);
            }
        }

        static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            if (messages == null || messages.Length == 0)
            {
                return;
            }

            lock (Gate)
            {
                CompilationLifecycle.EnsureStarted(DateTime.UtcNow);

                for (var index = 0; index < messages.Length; index++)
                {
                    var message = messages[index];
                    if (message.type == CompilerMessageType.Error)
                    {
                        AddCompileErrorLocked(new CompileDiagnosticEntry
                        {
                            TimestampUtc = DateTime.UtcNow,
                            Severity = "error",
                            Code = ExtractCompilerCode(message.message, "error CS"),
                            Source = "compilation_pipeline",
                            AssemblyPath = assemblyPath ?? string.Empty,
                            AssemblyName = AssemblyNameFromPath(assemblyPath),
                            File = message.file ?? string.Empty,
                            Line = Math.Max(0, message.line),
                            Column = Math.Max(0, message.column),
                            Message = message.message ?? string.Empty,
                            StackTrace = string.Empty,
                        });
                    }
                    else if (message.type == CompilerMessageType.Warning)
                    {
                        AddCompileWarningLocked(new CompileDiagnosticEntry
                        {
                            TimestampUtc = DateTime.UtcNow,
                            Severity = "warning",
                            Code = ExtractCompilerCode(message.message, "warning CS"),
                            Source = "compilation_pipeline",
                            AssemblyPath = assemblyPath ?? string.Empty,
                            AssemblyName = AssemblyNameFromPath(assemblyPath),
                            File = message.file ?? string.Empty,
                            Line = Math.Max(0, message.line),
                            Column = Math.Max(0, message.column),
                            Message = message.message ?? string.Empty,
                            StackTrace = string.Empty,
                        });
                    }
                }
            }
        }

        static void OnCompilationFinished(object context)
        {
            lock (Gate)
            {
                CompilationLifecycle.Finish(DateTime.UtcNow);
            }

            UnityBridgeRuntime.PublishCompilationFinishedHeartbeat();
        }

        static string ToTimestamp(DateTime value)
        {
            return value == DateTime.MinValue ? string.Empty : value.ToString("O");
        }

        static void AddCompileErrorLocked(CompileDiagnosticEntry entry)
        {
            for (var index = 0; index < CompileErrors.Count; index++)
            {
                var existing = CompileErrors[index];
                if (string.Equals(existing.File, entry.File, StringComparison.Ordinal)
                    && existing.Line == entry.Line
                    && existing.Column == entry.Column
                    && string.Equals(existing.Message, entry.Message, StringComparison.Ordinal))
                {
                    return;
                }
            }

            while (CompileErrors.Count >= MaxCompileErrors)
            {
                CompileErrors.RemoveAt(0);
            }

            CompileErrors.Add(entry);
        }

        static void AddCompileWarningLocked(CompileDiagnosticEntry entry)
        {
            for (var index = 0; index < CompileWarnings.Count; index++)
            {
                var existing = CompileWarnings[index];
                if (string.Equals(existing.File, entry.File, StringComparison.Ordinal)
                    && existing.Line == entry.Line
                    && existing.Column == entry.Column
                    && string.Equals(existing.Message, entry.Message, StringComparison.Ordinal))
                {
                    return;
                }
            }

            while (CompileWarnings.Count >= MaxCompileWarnings)
            {
                CompileWarnings.RemoveAt(0);
            }

            CompileWarnings.Add(entry);
        }

        static bool IsCompilerErrorLog(string condition, LogType type, out string file, out int line, out int column)
        {
            file = string.Empty;
            line = 0;
            column = 0;
            if (type != LogType.Error && type != LogType.Exception)
            {
                return false;
            }

            if (string.IsNullOrEmpty(condition))
            {
                return false;
            }

            var errorMarker = condition.IndexOf("error CS", StringComparison.OrdinalIgnoreCase);
            if (errorMarker < 0)
            {
                return false;
            }

            var open = condition.LastIndexOf('(', errorMarker);
            var close = open >= 0 ? condition.IndexOf(')', open + 1) : -1;
            if (open > 0 && close > open)
            {
                file = condition.Substring(0, open).Trim();
                var location = condition.Substring(open + 1, close - open - 1);
                var comma = location.IndexOf(',');
                if (comma >= 0)
                {
                    int.TryParse(location.Substring(0, comma).Trim(), out line);
                    int.TryParse(location.Substring(comma + 1).Trim(), out column);
                }
                else
                {
                    int.TryParse(location.Trim(), out line);
                }
            }

            return true;
        }

        static bool IsCompilerWarningLog(string condition, LogType type, out string file, out int line, out int column)
        {
            file = string.Empty;
            line = 0;
            column = 0;
            if (type != LogType.Warning)
            {
                return false;
            }

            if (string.IsNullOrEmpty(condition))
            {
                return false;
            }

            var warningMarker = condition.IndexOf("warning CS", StringComparison.OrdinalIgnoreCase);
            if (warningMarker < 0)
            {
                return false;
            }

            var open = condition.LastIndexOf('(', warningMarker);
            var close = open >= 0 ? condition.IndexOf(')', open + 1) : -1;
            if (open > 0 && close > open)
            {
                file = condition.Substring(0, open).Trim();
                var location = condition.Substring(open + 1, close - open - 1);
                var comma = location.IndexOf(',');
                if (comma >= 0)
                {
                    int.TryParse(location.Substring(0, comma).Trim(), out line);
                    int.TryParse(location.Substring(comma + 1).Trim(), out column);
                }
                else
                {
                    int.TryParse(location.Trim(), out line);
                }
            }

            return true;
        }

        static string ExtractCompilerCode(string message, string marker)
        {
            if (string.IsNullOrEmpty(message))
            {
                return string.Empty;
            }

            var at = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return string.Empty;
            }

            var codeStart = message.IndexOf("CS", at, StringComparison.OrdinalIgnoreCase);
            if (codeStart < 0)
            {
                return string.Empty;
            }

            var codeEnd = codeStart + 2;
            while (codeEnd < message.Length && char.IsDigit(message[codeEnd]))
            {
                codeEnd++;
            }

            return codeEnd > codeStart + 2 ? message.Substring(codeStart, codeEnd - codeStart).ToUpperInvariant() : string.Empty;
        }

        static string AssemblyNameFromPath(string assemblyPath)
        {
            if (string.IsNullOrEmpty(assemblyPath))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFileNameWithoutExtension(assemblyPath) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        sealed class ConsoleLogEntry
        {
            public string Timestamp { get; set; } = string.Empty;
            public DateTime TimestampUtc { get; set; } = DateTime.MinValue;
            public string Type { get; set; } = string.Empty;
            public string Message { get; set; } = string.Empty;
            public string StackTrace { get; set; } = string.Empty;
        }

        sealed class CompileDiagnosticEntry
        {
            public DateTime TimestampUtc { get; set; } = DateTime.MinValue;
            public string Severity { get; set; } = string.Empty;
            public string Code { get; set; } = string.Empty;
            public string Source { get; set; } = string.Empty;
            public string AssemblyPath { get; set; } = string.Empty;
            public string AssemblyName { get; set; } = string.Empty;
            public string File { get; set; } = string.Empty;
            public int Line { get; set; }
            public int Column { get; set; }
            public string Message { get; set; } = string.Empty;
            public string StackTrace { get; set; } = string.Empty;
        }
    }

    internal sealed class CompilationLifecycleState
    {
        long m_generation;
        long m_completedGeneration;
        DateTime m_startedUtc = DateTime.MinValue;
        DateTime m_finishedUtc = DateTime.MinValue;

        public CompilationLifecycleSnapshot Start(DateTime nowUtc)
        {
            m_generation++;
            m_startedUtc = nowUtc;
            m_finishedUtc = DateTime.MinValue;
            return Snapshot();
        }

        public CompilationLifecycleSnapshot EnsureStarted(DateTime nowUtc)
        {
            return m_generation > m_completedGeneration ? Snapshot() : Start(nowUtc);
        }

        public CompilationLifecycleSnapshot Finish(DateTime nowUtc)
        {
            EnsureStarted(nowUtc);
            m_completedGeneration = m_generation;
            m_finishedUtc = nowUtc;
            return Snapshot();
        }

        public CompilationLifecycleSnapshot Snapshot()
        {
            return new CompilationLifecycleSnapshot(m_generation, m_completedGeneration, m_startedUtc, m_finishedUtc);
        }
    }

    internal sealed class CompilationLifecycleSnapshot
    {
        public CompilationLifecycleSnapshot(long generation, long completedGeneration, DateTime startedUtc, DateTime finishedUtc)
        {
            Generation = Math.Max(0, generation);
            CompletedGeneration = Math.Max(0, Math.Min(completedGeneration, Generation));
            StartedUtc = startedUtc;
            FinishedUtc = finishedUtc;
        }

        public long Generation { get; }
        public long CompletedGeneration { get; }
        public DateTime StartedUtc { get; }
        public DateTime FinishedUtc { get; }
        public string Evidence => Generation == 0 ? "unobserved" : CompletedGeneration < Generation ? "active" : "finished";

        public string ToJson()
        {
            return "{\"generation\":" + Generation
                + ",\"completed_generation\":" + CompletedGeneration
                + ",\"evidence\":" + JsonUtil.String(Evidence)
                + ",\"started_utc\":" + JsonUtil.String(Timestamp(StartedUtc))
                + ",\"finished_utc\":" + JsonUtil.String(Timestamp(FinishedUtc))
                + "}";
        }

        static string Timestamp(DateTime value)
        {
            return value == DateTime.MinValue ? string.Empty : value.ToString("O");
        }
    }
}
