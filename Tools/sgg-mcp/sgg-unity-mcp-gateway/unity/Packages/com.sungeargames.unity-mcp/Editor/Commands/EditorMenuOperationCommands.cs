using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SGG.UnityMCP.Editor.Bridge;
using UnityEditor;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace SGG.UnityMCP.Editor.Commands
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class SGGUnityMcpLongRunningMenuAttribute : Attribute
    {
    }

    [InitializeOnLoad]
    public static class EditorMenuOperationCommands
    {
        internal const string CommandId = "editor.menu.operation.start";
        internal const string Contract = "sgg.unity_mcp.editor_menu_operation.v1";
        internal const string ArtifactRoot = "Library/SGGUnityMcp/job-artifacts/menu-operations";
        internal const int ArtifactSchemaVersion = 1;
        internal const string MarkerSimpleTypeName = "SGGUnityMcpLongRunningMenuAttribute";
        const string SessionKey = "SGG.UnityMCP.EditorMenuOperation.active";
        const string StatusCommand = "editor.menu.operation.status";
        const string SchemaJson = "{\"type\":\"object\",\"properties\":{\"menu_path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":128},\"dry_run\":{\"type\":\"boolean\"}},\"required\":[\"menu_path\"],\"additionalProperties\":false}";
        const int MaxMenuPathLength = 128;
        const int MaxArtifactCharacters = 65536;
        const int MaxTypeCacheMethods = 5000;
        const int EligibilityBudgetMs = 3000;
        const int QueueDeadlineMs = 10000;
        const int MaxSessionCharacters = 12000;
        static readonly object Gate = new object();
        static OperationState Active;
        static bool DispatchRegistered;
        static bool TerminalPublicationRegistered;
        static bool RecoveryInvalid;

        static EditorMenuOperationCommands()
        {
            RestoreAfterReload();
        }

        [McpCommand(
            "editor.menu.operation.start",
            Title = "Start durable editor menu operation",
            Description = "Start a durable explicitly marked Unity Editor MenuItem operation and poll editor.menu.operation.status",
            Risk = "execute",
            Idempotency = "unsafe",
            TimeoutMs = 10000,
            SchemaJson = SchemaJson,
            Tags = new string[] { "editor", "menu", "editor_menu_operation_correlated" },
            Capability = "editor_utilities",
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "operation_status",
            OutputLimitJson = "{\"max_artifact_chars\":65536}",
            SupportsJob = false,
            FeatureFlag = "editor_utilities")]
        public static CommandExecutionResult EditorMenuOperationStart(McpCommandContext context)
        {
            var args = context == null ? "{}" : context.ArgsJson;
            var menuPath = NormalizeRequestedPath(JsonUtil.ExtractString(args, "menu_path"));
            if (string.IsNullOrEmpty(menuPath)) return Refusal("schema_validation_failed", "Argument menu_path is required");
            if (menuPath.Length > MaxMenuPathLength || menuPath.IndexOf('\0') >= 0)
                return Refusal("invalid_field_value", "menu_path must be at most 128 characters and must not contain NUL");

            if (!ReadHidden(args, "_editor_menu_operation_id", out var reservedId, out var hasId)
                || (hasId && !IsSafeMenuOperationId(reservedId)))
                return Refusal("invalid_field_value", "_editor_menu_operation_id has an invalid format");
            if (!ReadHidden(args, "_editor_menu_caller_args_hash", out var callerHash, out var hasCallerHash)
                || (hasCallerHash && !IsSafeHash(callerHash)))
                return Refusal("invalid_field_value", "_editor_menu_caller_args_hash has an invalid format");

            var editing = RequireEditingStatus();
            if (editing != null) return editing;
            var dryRun = JsonUtil.ExtractBool(args, "dry_run", false);
            if (!dryRun && HasActiveOperation(out _, out var recoveryInvalid))
                return Refusal(recoveryInvalid ? "operation_recovery_unavailable" : "unity_busy", recoveryInvalid ? "Menu operation recovery state is invalid" : "Another editor menu operation is queued or running");

            var eligibility = ResolveEligibility(menuPath);
            if (!eligibility.IsEligible)
                return Refusal("menu_operation_not_eligible", eligibility.FailureMessage, ",\"eligibility\":" + EligibilityJson(eligibility));
            if (dryRun) return CommandExecutionResult.Ok(DryRunJson(menuPath, eligibility));

            var operationId = hasId ? reservedId : CreateOperationId();
            var artifactPath = ArtifactPathForOperation(operationId);
            if (string.IsNullOrEmpty(artifactPath)) return Refusal("invalid_field_value", "The menu operation id cannot produce a safe receipt path");
            if (!TryArtifactStatus(artifactPath, out var exists, out _)) return Refusal("path_not_allowed", "The menu operation receipt path is not safe");
            if (exists) return Refusal("menu_operation_exists", "The reserved menu operation id already has a persisted receipt", ",\"operation_id\":" + JsonUtil.String(operationId));

            var now = DateTime.UtcNow;
            var state = new OperationState
            {
                OperationId = operationId,
                ArtifactPath = artifactPath,
                MenuPath = menuPath,
                ArgsHash = Sha256(args),
                CallerArgsHash = hasCallerHash ? callerHash : Sha256(args),
                CreatedAtUtc = Stamp(now),
                QueuedAtUtc = Stamp(now),
                QueueDeadlineUtc = now.AddMilliseconds(QueueDeadlineMs),
                Status = "queued",
                Eligibility = MenuIdentity.From(eligibility),
            };
            state.QueueDeadlineText = Stamp(state.QueueDeadlineUtc);

            if (!TryWriteArtifactAtomic(state.ArtifactPath, ReceiptJson(state), out _))
                return Refusal("artifact_write_failed", "Could not persist the menu operation startup receipt");
            if (!Activate(state))
            {
                FailCreated(state, "operation_recovery_failed", "The menu operation could not establish SessionState recovery");
                Finish(state);
                return CreatedError("operation_recovery_failed", "The menu operation receipt was created but recovery could not be established", state);
            }

            Schedule();
            return CommandExecutionResult.Ok(ReceiptJson(state));
        }

        static CommandExecutionResult RequireEditingStatus()
        {
            if (EditorApplication.isCompiling) return Refusal("unity_compiling", "Unity is compiling; the menu operation was not started");
            if (EditorApplication.isUpdating) return Refusal("unity_updating", "Unity is updating packages or assets; the menu operation was not started");
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isPlaying) return Refusal("unity_busy", "The menu operation requires Unity editing status");
            if (ConsoleLogBuffer.HasCompileErrors()) return Refusal("compilation_failed", "Unity has compiler errors; the menu operation was not started");
            return null;
        }

        static CommandExecutionResult Refusal(string code, string message, string extra = "")
        {
            return CommandExecutionResult.Error(code, message, "{\"operation_created\":false,\"menu_execute_invoked\":false" + extra + "}");
        }

        static CommandExecutionResult CreatedError(string code, string message, OperationState state)
        {
            return CommandExecutionResult.Error(code, message, "{\"operation_created\":true,\"menu_execute_invoked\":false,\"operation_id\":"
                + JsonUtil.String(state.OperationId) + ",\"artifact_path\":" + JsonUtil.String(state.ArtifactPath)
                + ",\"status_request\":" + StatusRequestJson(state.OperationId) + "}");
        }

        static string DryRunJson(string menuPath, EligibilityResult eligibility)
        {
            return "{\"contract\":" + JsonUtil.String(Contract) + ",\"artifact_schema_version\":" + ArtifactSchemaVersion
                + ",\"command\":" + JsonUtil.String(CommandId) + ",\"dry_run\":true,\"would_start\":true,\"would_dispatch\":true"
                + ",\"dispatch_scheduled\":false,\"operation_created\":false,\"menu_execute_invoked\":false,\"artifact_persisted\":false"
                + ",\"menu_path\":" + JsonUtil.String(menuPath) + ",\"eligibility\":" + EligibilityJson(eligibility)
                + ",\"next_valid_actions\":[\"editor.menu.operation.start\"]}";
        }

        static bool ReadHidden(string json, string name, out string value, out bool present)
        {
            present = JsonUtil.HasProperty(json, name);
            value = present ? JsonUtil.ExtractString(json, name).Trim() : string.Empty;
            return !present || !string.IsNullOrEmpty(value);
        }

        static string CreateOperationId()
        {
            return "menu-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        internal static bool IsSafeMenuOperationId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 29 || !value.StartsWith("menu-", StringComparison.Ordinal)) return false;
            for (var i = 5; i < value.Length; i++)
            {
                if (i == 13 || i == 20) { if (value[i] != '-') return false; }
                else if (i < 20) { if (value[i] < '0' || value[i] > '9') return false; }
                else if (!LowerHex(value[i])) return false;
            }
            return true;
        }

        static bool IsSafeHash(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal)) return false;
            for (var i = 7; i < value.Length; i++) if (!LowerHex(value[i])) return false;
            return true;
        }

        static bool LowerHex(char value) => (value >= '0' && value <= '9') || (value >= 'a' && value <= 'f');

        static string Sha256(string value)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                var result = new StringBuilder("sha256:");
                foreach (var item in bytes) result.Append(item.ToString("x2", CultureInfo.InvariantCulture));
                return result.ToString();
            }
        }

        internal static string NormalizeMenuPathForTests(string value) => NormalizeRequestedPath(value);

        static string NormalizeRequestedPath(string value) => string.IsNullOrEmpty(value) ? string.Empty : value.Trim();

        static string NormalizeMenuItemPath(string value)
        {
            var path = NormalizeRequestedPath(value);
            var space = path.LastIndexOf(' ');
            if (space > 0 && space < path.Length - 1 && ShortcutStart(path[space + 1])) path = path.Substring(0, space);
            return path;
        }

        static bool ShortcutStart(char value) => value == '_' || value == '%' || value == '#' || value == '&';

        internal static EligibilityResult ResolveEligibilityForTests(string menuPath) => ResolveEligibility(NormalizeRequestedPath(menuPath));

        static EligibilityResult ResolveEligibility(string menuPath)
        {
            var result = new EligibilityResult { RequestedMenuPath = menuPath ?? string.Empty };
            var watch = Stopwatch.StartNew();
            IEnumerable<MethodBase> methods;
            try { methods = TypeCache.GetMethodsWithAttribute<MenuItem>(); }
            catch { return ScanFailure(result, "menu_operation_scan_unavailable", "Unity MenuItem TypeCache was unavailable"); }

            try
            {
                foreach (var methodBase in methods)
                {
                    result.MethodsScanned++;
                    if (result.MethodsScanned > MaxTypeCacheMethods) { result.Truncated = true; break; }
                    if (watch.ElapsedMilliseconds >= EligibilityBudgetMs) { result.TimedOut = true; break; }
                    var method = methodBase as MethodInfo;
                    if (method == null) continue;
                    var attributes = CustomAttributeData.GetCustomAttributes(method);
                    var exactExecution = false;
                    foreach (var attribute in attributes)
                    {
                        if (attribute.AttributeType != typeof(MenuItem)) continue;
                        if (TryMenuItemData(attribute, out var path, out var validator)
                            && !validator && string.Equals(NormalizeMenuItemPath(path), menuPath, StringComparison.Ordinal))
                        {
                            exactExecution = true;
                            break;
                        }
                    }
                    if (!exactExecution) continue;

                    result.CandidateCount++;
                    if (result.Method != null) continue;
                    result.Method = method;
                    result.ValidSignature = ValidMenuSignature(method);
                    result.MarkerTypes = MarkerTypes(attributes);
                }
            }
            catch
            {
                result.Truncated = true;
            }

            if (watch.ElapsedMilliseconds >= EligibilityBudgetMs && !result.Truncated) result.TimedOut = true;
            if (result.TimedOut) return ScanFailure(result, "menu_operation_scan_timeout", "Unity MenuItem TypeCache scan exceeded its bounded time");
            if (result.Truncated) return ScanFailure(result, "menu_operation_scan_truncated", "Unity MenuItem TypeCache scan exceeded its bounded candidate count");
            if (result.CandidateCount == 0) return Fail(result, "menu_operation_menu_item_not_found", "No exact non-validator Unity MenuItem method was found for menu_path");
            if (result.CandidateCount > 1) return Fail(result, "menu_operation_ambiguous", "More than one exact non-validator Unity MenuItem method matches menu_path");
            if (!result.ValidSignature) return Fail(result, "menu_operation_invalid_signature", "The exact MenuItem method must be static, parameterless, non-generic, and return void");
            if (result.MarkerTypes.Count == 0) return Fail(result, "menu_operation_marker_missing", "The exact MenuItem method lacks SGGUnityMcpLongRunningMenuAttribute");
            if (result.MarkerTypes.Count > 1) return Fail(result, "menu_operation_marker_ambiguous", "The exact MenuItem method has multiple marker types with the reserved simple name");
            result.IsEligible = true;
            result.MarkerType = result.MarkerTypes[0];
            result.Identity = MenuIdentity.From(result.Method, result.MarkerType);
            return result;
        }

        static EligibilityResult Fail(EligibilityResult result, string code, string message)
        {
            result.FailureCode = code;
            result.FailureMessage = message;
            return result;
        }

        static EligibilityResult ScanFailure(EligibilityResult result, string code, string message)
        {
            result.FailureCode = code;
            result.FailureMessage = message;
            return result;
        }

        static bool TryMenuItemData(CustomAttributeData attribute, out string path, out bool validator)
        {
            path = string.Empty;
            validator = false;
            if (attribute.ConstructorArguments.Count == 0) return false;
            path = attribute.ConstructorArguments[0].Value as string ?? string.Empty;
            if (attribute.ConstructorArguments.Count > 1 && attribute.ConstructorArguments[1].ArgumentType == typeof(bool))
                validator = attribute.ConstructorArguments[1].Value is bool value && value;
            foreach (var named in attribute.NamedArguments)
                if (named.MemberName == "validate" && named.TypedValue.ArgumentType == typeof(bool)) validator = named.TypedValue.Value is bool namedValue && namedValue;
            return !string.IsNullOrEmpty(path);
        }

        static List<Type> MarkerTypes(IList<CustomAttributeData> attributes)
        {
            var result = new List<Type>();
            foreach (var attribute in attributes)
            {
                var type = attribute.AttributeType;
                if (!typeof(Attribute).IsAssignableFrom(type) || type.Name != MarkerSimpleTypeName || result.Contains(type)) continue;
                result.Add(type);
            }
            return result;
        }

        static bool ValidMenuSignature(MethodInfo method)
        {
            try { return method.IsStatic && !method.IsGenericMethod && !method.ContainsGenericParameters && method.ReturnType == typeof(void) && method.GetParameters().Length == 0; }
            catch { return false; }
        }

        static string EligibilityJson(EligibilityResult result)
        {
            var json = new StringBuilder("{\"eligible\":").Append(JsonUtil.Bool(result.IsEligible))
                .Append(",\"menu_path\":").Append(JsonUtil.String(result.RequestedMenuPath))
                .Append(",\"failure_code\":").Append(JsonUtil.String(result.FailureCode))
                .Append(",\"scan\":{\"methods_scanned\":").Append(result.MethodsScanned)
                .Append(",\"candidate_methods\":").Append(result.CandidateCount)
                .Append(",\"timed_out\":").Append(JsonUtil.Bool(result.TimedOut))
                .Append(",\"truncated\":").Append(JsonUtil.Bool(result.Truncated)).Append('}');
            if (result.IsEligible)
            {
                json.Append(",\"marker_type\":").Append(JsonUtil.String(result.Identity.MarkerType))
                    .Append(",\"marker_assembly\":").Append(JsonUtil.String(result.Identity.MarkerAssembly));
                AppendMethodIdentity(json, result.Identity);
            }
            return json.Append('}').ToString();
        }

        static void AppendMethodIdentity(StringBuilder json, MenuIdentity identity)
        {
            json.Append(",\"method_identity\":{\"assembly\":").Append(JsonUtil.String(Bounded(identity.MethodAssembly, 160)))
                .Append(",\"declaring_type\":").Append(JsonUtil.String(Bounded(identity.MethodDeclaringType, 256)))
                .Append(",\"name\":").Append(JsonUtil.String(Bounded(identity.MethodName, 128)))
                .Append(",\"signature\":").Append(JsonUtil.String(Bounded(identity.MethodSignature, 512))).Append('}');
        }

        static string MethodSignature(MethodInfo method)
        {
            var json = new StringBuilder(TypeName(method.ReturnType)).Append(' ').Append(TypeName(method.DeclaringType)).Append('.').Append(method.Name).Append('(');
            try
            {
                var parameters = method.GetParameters();
                for (var i = 0; i < parameters.Length; i++) { if (i > 0) json.Append(','); json.Append(TypeName(parameters[i].ParameterType)); }
            }
            catch { }
            return json.Append(')').ToString();
        }

        static string TypeName(Type type) => type == null ? string.Empty : Bounded(type.FullName ?? type.Name, 512);
        static string AssemblyName(Assembly assembly) { try { return Bounded(assembly == null ? string.Empty : assembly.GetName().Name, 160); } catch { return string.Empty; } }
        static string Bounded(string value, int max) => string.IsNullOrEmpty(value) || value.Length <= max ? value ?? string.Empty : value.Substring(0, max);
        static string Stamp(DateTime value) => value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

        internal static string ArtifactPathForOperation(string operationId) => IsSafeMenuOperationId(operationId) ? ArtifactRoot + "/" + operationId + ".json" : string.Empty;

        static string ReceiptJson(OperationState state)
        {
            var json = new StringBuilder("{");
            Field(json, "contract", Contract); Number(json, "artifact_schema_version", ArtifactSchemaVersion); Field(json, "command", CommandId);
            Field(json, "operation_id", state.OperationId); Field(json, "status", state.Status); Bool(json, "terminal", state.Terminal); Bool(json, "success", state.Success);
            FieldOrNull(json, "created_at_utc", state.CreatedAtUtc); FieldOrNull(json, "queued_at_utc", state.QueuedAtUtc); FieldOrNull(json, "running_at_utc", state.RunningAtUtc); FieldOrNull(json, "terminal_at_utc", state.TerminalAtUtc); FieldOrNull(json, "queue_deadline_utc", state.QueueDeadlineText);
            Field(json, "menu_path", state.MenuPath); Field(json, "args_hash", state.ArgsHash); Field(json, "caller_args_hash", state.CallerArgsHash); Number(json, "dispatch_count", state.DispatchCount); NullableBool(json, "execute_menu_item_returned", state.ExecuteMenuItemReturned);
            Field(json, "artifact_path", state.ArtifactPath); Bool(json, "artifact_persisted", true); Bool(json, "operation_created", true); Bool(json, "menu_execute_invoked", state.MenuExecuteInvoked);
            Field(json, "failure_code", Bounded(state.FailureCode, 96)); Field(json, "message", Bounded(state.Message, 512));
            json.Append(",\"eligibility\":{"); Field(json, "marker_type", Bounded(state.Eligibility.MarkerType, 256)); Field(json, "marker_assembly", Bounded(state.Eligibility.MarkerAssembly, 160)); AppendMethodIdentity(json, state.Eligibility); json.Append('}');
            json.Append(",\"status_request\":").Append(StatusRequestJson(state.OperationId));
            json.Append(",\"next_valid_actions\":[\"editor.menu.operation.status\",\"job.artifact.read\"]}");
            return json.ToString();
        }

        static string StatusRequestJson(string operationId)
        {
            var args = "{\"operation_id\":" + JsonUtil.String(operationId) + "}";
            return "{\"command\":" + JsonUtil.String(StatusCommand) + ",\"args_json\":" + JsonUtil.String(args) + "}";
        }

        static void Field(StringBuilder json, string name, string value) { Comma(json); json.Append(JsonUtil.String(name)).Append(':').Append(JsonUtil.String(value ?? string.Empty)); }
        static void FieldOrNull(StringBuilder json, string name, string value) { Comma(json); json.Append(JsonUtil.String(name)).Append(':').Append(string.IsNullOrEmpty(value) ? "null" : JsonUtil.String(value)); }
        static void Number(StringBuilder json, string name, int value) { Comma(json); json.Append(JsonUtil.String(name)).Append(':').Append(value.ToString(CultureInfo.InvariantCulture)); }
        static void Bool(StringBuilder json, string name, bool value) { Comma(json); json.Append(JsonUtil.String(name)).Append(':').Append(JsonUtil.Bool(value)); }
        static void NullableBool(StringBuilder json, string name, bool? value) { Comma(json); json.Append(JsonUtil.String(name)).Append(':').Append(value.HasValue ? JsonUtil.Bool(value.Value) : "null"); }
        static void Comma(StringBuilder json) { if (json.Length > 1 && json[json.Length - 1] != '{') json.Append(','); }

        internal static bool PersistedMenuOperationArtifactIdentityMatches(string content, string operationId, string artifactPath)
        {
            return IsSafeMenuOperationId(operationId) && artifactPath == ArtifactPathForOperation(operationId) && HasValidJsonSyntax(content) && content.Length <= MaxArtifactCharacters
                && JsonUtil.ExtractString(content, "contract") == Contract && JsonUtil.ExtractInt(content, "artifact_schema_version", 0) == ArtifactSchemaVersion
                && JsonUtil.ExtractString(content, "command") == CommandId && JsonUtil.ExtractString(content, "operation_id") == operationId && JsonUtil.ExtractString(content, "artifact_path") == artifactPath;
        }

        static bool HasValidJsonSyntax(string content)
        {
            if (string.IsNullOrEmpty(content) || content.Length > MaxArtifactCharacters) return false;
            try
            {
                var parsed = JsonUtility.FromJson<ReceiptSyntaxProbe>(content);
                return parsed != null && string.Equals(parsed.contract, Contract, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        internal static string NormalizePersistedMenuOperationArtifactForRead(string content, string operationId, string artifactPath)
        {
            return TryParseReceipt(content, operationId, artifactPath, out var state) ? ReceiptJson(state) : string.Empty;
        }

        internal static string ReadPersistedMenuOperationArtifact(string operationId, string artifactPath, out bool identityMismatch, out string readError)
        {
            identityMismatch = false; readError = string.Empty;
            if (!IsSafeMenuOperationId(operationId) || artifactPath != ArtifactPathForOperation(operationId)) { identityMismatch = true; return string.Empty; }
            if (!TryResolveArtifactPath(artifactPath, out var fullPath, out readError)) return string.Empty;
            try
            {
                if (!File.Exists(fullPath)) return string.Empty;
                string content;
                using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    var length = stream.Length;
                    if (length <= 0 || length > MaxArtifactCharacters) { readError = "receipt_size_out_of_bounds"; return string.Empty; }
                    var bytes = new byte[(int)length];
                    var offset = 0;
                    while (offset < bytes.Length)
                    {
                        var read = stream.Read(bytes, offset, bytes.Length - offset);
                        if (read <= 0) break;
                        offset += read;
                    }
                    if (offset != bytes.Length || stream.Length != length) { readError = "receipt_changed_during_read"; return string.Empty; }
                    content = new UTF8Encoding(false, true).GetString(bytes);
                }
                if (!TryResolveArtifactPath(artifactPath, out var verifiedPath, out readError)
                    || !string.Equals(verifiedPath, fullPath, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return string.Empty;
                if (!PersistedMenuOperationArtifactIdentityMatches(content, operationId, artifactPath)) { identityMismatch = true; return string.Empty; }
                if (!TryParseReceipt(content, operationId, artifactPath, out var state)) { readError = "receipt_malformed"; return string.Empty; }
                return ReceiptJson(state);
            }
            catch { readError = "receipt_read_failed"; return string.Empty; }
        }

        static bool TryParseReceipt(string content, string operationId, string artifactPath, out OperationState state)
        {
            state = null;
            if (!PersistedMenuOperationArtifactIdentityMatches(content, operationId, artifactPath) || content.Length > MaxArtifactCharacters) return false;
            var status = JsonUtil.ExtractString(content, "status");
            if (status != "queued" && status != "running" && status != "succeeded" && status != "failed" && status != "expired" && status != "unknown_after_reload") return false;
            var menuPath = NormalizeRequestedPath(JsonUtil.ExtractString(content, "menu_path"));
            var argsHash = JsonUtil.ExtractString(content, "args_hash"); var callerHash = JsonUtil.ExtractString(content, "caller_args_hash");
            var deadlineText = JsonUtil.ExtractString(content, "queue_deadline_utc");
            if (string.IsNullOrEmpty(menuPath) || menuPath.Length > MaxMenuPathLength || !IsSafeHash(argsHash) || !IsSafeHash(callerHash) || !DateTime.TryParse(deadlineText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var deadline)) return false;
            var dispatch = JsonUtil.ExtractInt(content, "dispatch_count", -1); if (dispatch < 0 || dispatch > 1) return false;
            var terminal = status == "succeeded" || status == "failed" || status == "expired" || status == "unknown_after_reload";
            if (JsonUtil.ExtractBool(content, "terminal", !terminal) != terminal || JsonUtil.ExtractBool(content, "success", false) != (status == "succeeded")) return false;
            if (!JsonUtil.HasProperty(content, "menu_execute_invoked") || !JsonUtil.HasProperty(content, "execute_menu_item_returned")) return false;
            var menuExecuteInvoked = JsonUtil.ExtractBool(content, "menu_execute_invoked", false);
            var executeMenuItemReturned = NullableBoolFromJson(content, "execute_menu_item_returned");
            var failureCode = JsonUtil.ExtractString(content, "failure_code");
            var lifecycleValid = status == "queued"
                ? dispatch == 0 && !menuExecuteInvoked && !executeMenuItemReturned.HasValue
                : status == "running"
                    ? dispatch == 1 && menuExecuteInvoked && !executeMenuItemReturned.HasValue
                    : status == "succeeded"
                        ? dispatch == 1 && menuExecuteInvoked && executeMenuItemReturned == true
                        : status == "expired"
                            ? dispatch == 0 && !menuExecuteInvoked && !executeMenuItemReturned.HasValue
                            : status == "unknown_after_reload"
                                 ? dispatch == 1 && menuExecuteInvoked && !executeMenuItemReturned.HasValue
                                  : dispatch == 0
                                      ? !menuExecuteInvoked && !executeMenuItemReturned.HasValue
                                      : menuExecuteInvoked && (executeMenuItemReturned != true || failureCode == "unity_exception");
            if (!lifecycleValid) return false;
            var createdAt = JsonUtil.ExtractString(content, "created_at_utc"); var queuedAt = JsonUtil.ExtractString(content, "queued_at_utc"); var runningAt = JsonUtil.ExtractString(content, "running_at_utc"); var terminalAt = JsonUtil.ExtractString(content, "terminal_at_utc");
            if (!DateTime.TryParse(createdAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)
                || !DateTime.TryParse(queuedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)
                || dispatch == 1 && !DateTime.TryParse(runningAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)
                || terminal && !DateTime.TryParse(terminalAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)) return false;
            var eligibility = JsonUtil.ExtractObject(content, "eligibility");
            var methodIdentity = JsonUtil.ExtractObject(eligibility, "method_identity");
            var identity = new MenuIdentity
            {
                MarkerType = JsonUtil.ExtractString(eligibility, "marker_type"), MarkerAssembly = JsonUtil.ExtractString(eligibility, "marker_assembly"),
                MethodAssembly = JsonUtil.ExtractString(methodIdentity, "assembly"), MethodDeclaringType = JsonUtil.ExtractString(methodIdentity, "declaring_type"),
                MethodName = JsonUtil.ExtractString(methodIdentity, "name"), MethodSignature = JsonUtil.ExtractString(methodIdentity, "signature"),
            };
            if (string.IsNullOrEmpty(identity.MarkerType) || string.IsNullOrEmpty(identity.MarkerAssembly) || string.IsNullOrEmpty(identity.MethodAssembly) || string.IsNullOrEmpty(identity.MethodDeclaringType) || string.IsNullOrEmpty(identity.MethodName) || string.IsNullOrEmpty(identity.MethodSignature)) return false;
            state = new OperationState
            {
                OperationId = operationId, ArtifactPath = artifactPath, MenuPath = menuPath, ArgsHash = argsHash, CallerArgsHash = callerHash,
                CreatedAtUtc = createdAt, QueuedAtUtc = queuedAt, RunningAtUtc = runningAt, TerminalAtUtc = terminalAt, QueueDeadlineText = deadlineText, QueueDeadlineUtc = deadline,
                Status = status, DispatchCount = dispatch, Terminal = terminal, Success = status == "succeeded", MenuExecuteInvoked = menuExecuteInvoked, ExecuteMenuItemReturned = executeMenuItemReturned, FailureCode = failureCode, Message = JsonUtil.ExtractString(content, "message"), Eligibility = identity,
            };
            return true;
        }

        static bool? NullableBoolFromJson(string json, string property)
        {
            if (!JsonUtil.HasProperty(json, property)) return null;
            var key = "\"" + property + "\""; var start = json.IndexOf(key, StringComparison.Ordinal); if (start < 0) return null;
            var colon = json.IndexOf(':', start + key.Length); if (colon < 0) return null; var i = colon + 1; while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (json.IndexOf("true", i, StringComparison.Ordinal) == i) return true; if (json.IndexOf("false", i, StringComparison.Ordinal) == i) return false; return null;
        }

        static bool Activate(OperationState state)
        {
            lock (Gate)
            {
                if (RecoveryInvalid || Active != null && !Active.Terminal) return false;
                var raw = SessionState.GetString(SessionKey, string.Empty);
                if (!string.IsNullOrEmpty(raw)) return false;
                if (!StoreSession(state)) return false;
                Active = state; return true;
            }
        }

        static bool HasActiveOperation(out OperationState state, out bool invalid)
        {
            state = null; invalid = false;
            lock (Gate)
            {
                if (RecoveryInvalid) { invalid = true; return true; }
                if (Active != null)
                {
                    if (!Active.Terminal) { state = Active; return true; }
                    if (!TryWriteArtifactAtomic(Active.ArtifactPath, ReceiptJson(Active), out _)) { state = Active; invalid = true; return true; }
                    Finish(Active);
                }
                var raw = SessionState.GetString(SessionKey, string.Empty);
                if (string.IsNullOrEmpty(raw)) return false;
                if (!TryParseSession(raw, out var parsed)) { RecoveryInvalid = true; invalid = true; return true; }
                if (parsed.Terminal)
                {
                    if (!TryWriteArtifactAtomic(parsed.ArtifactPath, ReceiptJson(parsed), out _)) { Active = parsed; state = parsed; invalid = true; return true; }
                    Finish(parsed);
                    return false;
                }
                Active = parsed; state = parsed; return true;
            }
        }

        static bool TryParseSession(string raw, out OperationState state)
        {
            state = null;
            var id = JsonUtil.ExtractString(raw, "operation_id"); var path = JsonUtil.ExtractString(raw, "artifact_path");
            return IsSafeMenuOperationId(id) && path == ArtifactPathForOperation(id) && TryParseReceipt(raw, id, path, out state);
        }

        static bool StoreSession(OperationState state)
        {
            var json = ReceiptJson(state); if (json.Length > MaxSessionCharacters) return false;
            try { SessionState.SetString(SessionKey, json); return SessionState.GetString(SessionKey, string.Empty) == json; } catch { return false; }
        }

        static void Schedule()
        {
            if (DispatchRegistered) return;
            EditorApplication.update -= Dispatch;
            EditorApplication.update += Dispatch;
            DispatchRegistered = true;
        }

        static OperationState Snapshot()
        {
            lock (Gate) return Active;
        }

        static void Dispatch()
        {
            EditorApplication.update -= Dispatch;
            DispatchRegistered = false;
            var state = Snapshot();
            if (state == null || state.Terminal || state.Status != "queued" || state.DispatchCount != 0) return;
            if (DateTime.UtcNow >= state.QueueDeadlineUtc) { Complete(state, "expired", "queue_expired", "The menu operation queue deadline elapsed before dispatch"); return; }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) { Schedule(); return; }
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isPlaying || ConsoleLogBuffer.HasCompileErrors()) { Complete(state, "failed", "pre_execute_editor_not_ready", "Unity was not in editing status before menu execution"); return; }
            var eligibility = ResolveEligibility(state.MenuPath);
            if (!eligibility.IsEligible) { Complete(state, "failed", eligibility.FailureCode, "Menu eligibility was not proven immediately before execution"); return; }
            if (!state.Eligibility.Matches(eligibility)) { Complete(state, "failed", "menu_operation_identity_changed", "The eligible MenuItem identity changed before execution"); return; }

            state.Status = "running"; state.DispatchCount = 1; state.RunningAtUtc = Stamp(DateTime.UtcNow); state.MenuExecuteInvoked = true;
            if (!TryWriteArtifactAtomic(state.ArtifactPath, ReceiptJson(state), out _) || !StoreSession(state)) { state.Status = "failed"; state.DispatchCount = 0; state.RunningAtUtc = string.Empty; state.MenuExecuteInvoked = false; Complete(state, "failed", "running_receipt_persist_failed", "The running receipt could not be persisted; menu execution was not invoked"); return; }
            try
            {
                var execution = EditorUtilityCommands.ExecuteMenuItemObserved(state.MenuPath);
                state.ExecuteMenuItemReturned = execution.Returned;
                if (execution.ExceptionObserved)
                {
                    state.Status = "failed"; state.Success = false; state.FailureCode = "unity_exception"; state.Message = "The invoked Unity menu item logged a synchronous exception";
                }
                else
                {
                    state.Status = execution.Returned ? "succeeded" : "failed"; state.Success = execution.Returned; state.FailureCode = execution.Returned ? string.Empty : "execute_menu_item_returned_false"; state.Message = execution.Returned ? string.Empty : "EditorApplication.ExecuteMenuItem returned false";
                }
            }
            catch
            {
                state.Status = "failed"; state.Success = false; state.FailureCode = "execute_menu_item_exception"; state.Message = "EditorApplication.ExecuteMenuItem threw an exception"; state.ExecuteMenuItemReturned = null;
            }
            state.Terminal = true; state.TerminalAtUtc = Stamp(DateTime.UtcNow);
            if (TryWriteArtifactAtomic(state.ArtifactPath, ReceiptJson(state), out _)) Finish(state);
            else RetainTerminalForPublication(state);
        }

        static void Complete(OperationState state, string status, string code, string message)
        {
            state.Status = status; state.Terminal = true; state.Success = status == "succeeded"; state.DispatchCount = Math.Max(0, state.DispatchCount); state.FailureCode = code; state.Message = message; state.TerminalAtUtc = Stamp(DateTime.UtcNow); state.MenuExecuteInvoked = false; state.ExecuteMenuItemReturned = null;
            if (TryWriteArtifactAtomic(state.ArtifactPath, ReceiptJson(state), out _)) Finish(state); else RetainTerminalForPublication(state);
        }

        static void FailCreated(OperationState state, string code, string message)
        {
            state.Status = "failed"; state.Terminal = true; state.Success = false; state.FailureCode = code; state.Message = message; state.TerminalAtUtc = Stamp(DateTime.UtcNow); state.MenuExecuteInvoked = false;
            TryWriteArtifactAtomic(state.ArtifactPath, ReceiptJson(state), out _);
        }

        static void Finish(OperationState state)
        {
            lock (Gate)
            {
                EditorApplication.update -= RetryTerminalPublication; TerminalPublicationRegistered = false;
                if (Active == null || Active.OperationId == state.OperationId) Active = null;
                var raw = SessionState.GetString(SessionKey, string.Empty);
                if (string.IsNullOrEmpty(raw) || JsonUtil.ExtractString(raw, "operation_id") == state.OperationId) SessionState.EraseString(SessionKey);
            }
        }

        static void RestoreAfterReload()
        {
            EditorApplication.update -= Dispatch; DispatchRegistered = false; Active = null; RecoveryInvalid = false;
            var raw = SessionState.GetString(SessionKey, string.Empty); if (string.IsNullOrEmpty(raw)) return;
            if (!TryParseSession(raw, out var state)) { RecoveryInvalid = true; return; }
            if (state.Terminal)
            {
                if (TryWriteArtifactAtomic(state.ArtifactPath, ReceiptJson(state), out _)) Finish(state);
                else RetainTerminalForPublication(state);
                return;
            }
            if (state.Status == "running" || state.DispatchCount == 1) { MarkUnknown(state); return; }
            var persisted = ReadPersistedMenuOperationArtifact(state.OperationId, state.ArtifactPath, out var mismatch, out _);
            if (mismatch || string.IsNullOrEmpty(persisted)) { Complete(state, "failed", mismatch ? "receipt_identity_mismatch" : "receipt_unavailable_after_reload", "The menu operation receipt could not be verified after reload"); return; }
            if (!TryParseReceipt(persisted, state.OperationId, state.ArtifactPath, out var persistedState)) { Complete(state, "failed", "receipt_malformed", "The menu operation receipt was malformed after reload"); return; }
            if (persistedState.Terminal) { Finish(state); return; }
            if (state.Status == "queued" && state.DispatchCount == 0 && persistedState.Status == "queued" && persistedState.DispatchCount == 0)
            {
                if (DateTime.UtcNow >= state.QueueDeadlineUtc) Complete(state, "expired", "queue_expired", "The menu operation queue deadline elapsed during reload recovery");
                else { Active = state; Schedule(); }
                return;
            }
            MarkUnknown(state);
        }

        static void MarkUnknown(OperationState state)
        {
            state.Status = "unknown_after_reload"; state.Terminal = true; state.Success = false; state.DispatchCount = Math.Max(1, state.DispatchCount); state.FailureCode = "unknown_after_reload"; state.Message = "The menu operation reached dispatch and cannot be safely replayed after domain reload"; state.TerminalAtUtc = Stamp(DateTime.UtcNow); state.MenuExecuteInvoked = true; state.ExecuteMenuItemReturned = null;
            if (TryWriteArtifactAtomic(state.ArtifactPath, ReceiptJson(state), out _)) Finish(state); else RetainTerminalForPublication(state);
        }

        static void RetainTerminalForPublication(OperationState state)
        {
            lock (Gate)
            {
                Active = state;
                StoreSession(state);
            }
            ScheduleTerminalPublication();
        }

        static void ScheduleTerminalPublication()
        {
            if (TerminalPublicationRegistered) return;
            EditorApplication.update -= RetryTerminalPublication;
            EditorApplication.update += RetryTerminalPublication;
            TerminalPublicationRegistered = true;
        }

        static void RetryTerminalPublication()
        {
            var state = Snapshot();
            if (state == null || !state.Terminal)
            {
                EditorApplication.update -= RetryTerminalPublication;
                TerminalPublicationRegistered = false;
                return;
            }
            if (EditorApplication.timeSinceStartup < state.NextTerminalPublicationAttemptAt) return;
            state.TerminalPublicationAttempts++;
            if (TryWriteArtifactAtomic(state.ArtifactPath, ReceiptJson(state), out _)) { Finish(state); return; }
            state.NextTerminalPublicationAttemptAt = EditorApplication.timeSinceStartup + 1d;
        }

        internal static void ResetForTests()
        {
            EditorApplication.update -= Dispatch; EditorApplication.update -= RetryTerminalPublication; DispatchRegistered = false; TerminalPublicationRegistered = false;
            lock (Gate) { Active = null; RecoveryInvalid = false; SessionState.EraseString(SessionKey); }
        }

        internal static bool TryWriteArtifactAtomic(string artifactPath, string content, out string error)
        {
            error = string.Empty; string temp = null; string tempArtifactPath = null;
            try
            {
                if (string.IsNullOrEmpty(content) || content.Length > MaxArtifactCharacters) { error = "receipt_content_out_of_bounds"; return false; }
                if (!TryResolveArtifactPath(artifactPath, out var full, out error)) return false;
                var directory = Path.GetDirectoryName(full); if (string.IsNullOrEmpty(directory)) { error = "receipt_directory_unavailable"; return false; }
                Directory.CreateDirectory(directory);
                if (!TryResolveArtifactPath(artifactPath, out full, out error)) return false;
                tempArtifactPath = artifactPath + ".tmp-" + Guid.NewGuid().ToString("N");
                if (!TryResolveArtifactPath(tempArtifactPath, out var safeTemp, out error)) return false;
                temp = safeTemp;
                using (var stream = new FileStream(safeTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true)) { writer.Write(content); writer.Flush(); stream.Flush(true); }
                var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!TryResolveArtifactPath(tempArtifactPath, out var publicationTemp, out error) || !string.Equals(publicationTemp, safeTemp, comparison)) return false;
                if (!TryResolveArtifactPath(artifactPath, out var publication, out error) || !string.Equals(publication, full, comparison)) return false;
                if (File.Exists(full)) File.Replace(safeTemp, full, null); else File.Move(safeTemp, full);
                return true;
            }
            catch { error = "receipt_publication_failed"; return false; }
            finally { try { if (!string.IsNullOrEmpty(temp) && File.Exists(temp)) File.Delete(temp); } catch { } }
        }

        static bool TryArtifactStatus(string artifactPath, out bool exists, out string error)
        {
            exists = false; if (!TryResolveArtifactPath(artifactPath, out var full, out error)) return false;
            try { exists = File.Exists(full) || Directory.Exists(full); return true; } catch { error = "receipt_path_status_unavailable"; return false; }
        }

        static bool TryResolveArtifactPath(string artifactPath, out string fullPath, out string error)
        {
            fullPath = string.Empty; error = string.Empty;
            try
            {
                if (string.IsNullOrEmpty(artifactPath) || artifactPath.IndexOf('\0') >= 0 || Path.IsPathRooted(artifactPath)) { error = "receipt_path_not_allowed"; return false; }
                var normalized = artifactPath.Replace('\\', '/'); if (!normalized.StartsWith(ArtifactRoot + "/", StringComparison.Ordinal) || normalized.Contains("../") || normalized.Contains("/..") || normalized.IndexOf(':') >= 0) { error = "receipt_path_not_allowed"; return false; }
                var project = Path.GetFullPath(ProjectPaths.ProjectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var root = Path.GetFullPath(Path.Combine(project, ArtifactRoot.Replace('/', Path.DirectorySeparatorChar))).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                fullPath = Path.GetFullPath(Path.Combine(project, normalized.Replace('/', Path.DirectorySeparatorChar)));
                var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!SameOrChild(root, project, comparison) || !SameOrChild(fullPath, root, comparison) || ReparseChain(fullPath, root, comparison) || ReparseChain(root, project, comparison)) { fullPath = string.Empty; error = "receipt_path_not_allowed"; return false; }
                return true;
            }
            catch { fullPath = string.Empty; error = "receipt_path_validation_failed"; return false; }
        }

        static bool SameOrChild(string path, string root, StringComparison comparison)
        {
            path = (path ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); root = (root ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(path, root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison) || path.StartsWith(root + Path.AltDirectorySeparatorChar, comparison);
        }

        static bool ReparseChain(string path, string root, StringComparison comparison)
        {
            for (var current = path; SameOrChild(current, root, comparison); current = Path.GetDirectoryName(current))
            {
                try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true; }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch { return true; }
                var next = Path.GetDirectoryName(current); if (string.IsNullOrEmpty(next) || next == current) break;
            }
            return false;
        }

        [Serializable]
        sealed class ReceiptSyntaxProbe
        {
            public string contract = string.Empty;
        }

        internal sealed class EligibilityResult
        {
            internal string RequestedMenuPath = string.Empty, FailureCode = string.Empty, FailureMessage = string.Empty;
            internal bool IsEligible, ValidSignature, TimedOut, Truncated;
            internal int MethodsScanned, CandidateCount;
            internal MethodInfo Method; internal Type MarkerType; internal List<Type> MarkerTypes = new List<Type>(); internal MenuIdentity Identity;
        }

        sealed class OperationState
        {
            internal string OperationId = string.Empty, ArtifactPath = string.Empty, MenuPath = string.Empty, ArgsHash = string.Empty, CallerArgsHash = string.Empty, CreatedAtUtc = string.Empty, QueuedAtUtc = string.Empty, RunningAtUtc = string.Empty, TerminalAtUtc = string.Empty, QueueDeadlineText = string.Empty, Status = string.Empty, FailureCode = string.Empty, Message = string.Empty;
            internal DateTime QueueDeadlineUtc; internal double NextTerminalPublicationAttemptAt; internal int DispatchCount, TerminalPublicationAttempts; internal bool Terminal, Success, MenuExecuteInvoked; internal bool? ExecuteMenuItemReturned; internal MenuIdentity Eligibility;
        }

        internal sealed class MenuIdentity
        {
            internal string MarkerType = string.Empty, MarkerAssembly = string.Empty, MethodAssembly = string.Empty, MethodDeclaringType = string.Empty, MethodName = string.Empty, MethodSignature = string.Empty;
            internal static MenuIdentity From(EligibilityResult result) => From(result.Method, result.MarkerType);
            internal static MenuIdentity From(MethodInfo method, Type marker) => new MenuIdentity { MarkerType = TypeName(marker), MarkerAssembly = AssemblyName(marker == null ? null : marker.Assembly), MethodAssembly = AssemblyName(method == null || method.DeclaringType == null ? null : method.DeclaringType.Assembly), MethodDeclaringType = TypeName(method == null ? null : method.DeclaringType), MethodName = method == null ? string.Empty : method.Name, MethodSignature = EditorMenuOperationCommands.MethodSignature(method) };
            internal bool Matches(EligibilityResult result) => result != null && result.IsEligible && MarkerType == TypeName(result.MarkerType) && MarkerAssembly == AssemblyName(result.MarkerType == null ? null : result.MarkerType.Assembly) && MethodAssembly == AssemblyName(result.Method == null || result.Method.DeclaringType == null ? null : result.Method.DeclaringType.Assembly) && MethodDeclaringType == TypeName(result.Method == null ? null : result.Method.DeclaringType) && MethodName == (result.Method == null ? string.Empty : result.Method.Name) && MethodSignature == EditorMenuOperationCommands.MethodSignature(result.Method);
        }
    }
}
