using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SGG.UnityMCP.Editor.Paths;
using UnityEditor;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class AssetSafeWriteCommands
    {
        const string ReadonlyFeatureFlag = "asset_readonly";
        const string SafeWritesFeatureFlag = "asset_safe_writes";
        const string FolderCreateSchemaJson = "{\"type\":\"object\",\"properties\":{\"folder_path\":{\"type\":\"string\"}},\"required\":[\"folder_path\"],\"additionalProperties\":false}";
        const string AssetMoveSchemaJson = "{\"type\":\"object\",\"properties\":{\"source_path\":{\"type\":\"string\"},\"destination_path\":{\"type\":\"string\"},\"dry_run\":{\"type\":\"boolean\"},\"precondition_guid\":{\"type\":\"string\",\"minLength\":32,\"maxLength\":32}},\"required\":[\"source_path\",\"destination_path\"],\"additionalProperties\":false}";
        const string AssetDeleteSchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_path\":{\"type\":\"string\"},\"dry_run\":{\"type\":\"boolean\"},\"precondition_guid\":{\"type\":\"string\",\"minLength\":32,\"maxLength\":32}},\"required\":[\"asset_path\"],\"additionalProperties\":false}";
        const string EmptySchemaJson = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
        const string ScriptReadSchemaJson = "{\"type\":\"object\",\"properties\":{\"script_path\":{\"type\":\"string\"},\"max_bytes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":65536}},\"required\":[\"script_path\"],\"additionalProperties\":false}";
        const string ScriptHashSchemaJson = "{\"type\":\"object\",\"properties\":{\"script_path\":{\"type\":\"string\"}},\"required\":[\"script_path\"],\"additionalProperties\":false}";
        const string ScriptValidateSchemaJson = "{\"type\":\"object\",\"properties\":{\"script_path\":{\"type\":\"string\"},\"level\":{\"type\":\"string\",\"enum\":[\"basic\",\"standard\"],\"default\":\"basic\"},\"include_diagnostics\":{\"type\":\"boolean\",\"default\":false},\"max_bytes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":262144}},\"required\":[\"script_path\"],\"additionalProperties\":false}";
        const string ScriptTextEditsApplySchemaJson = "{\"type\":\"object\",\"properties\":{\"script_path\":{\"type\":\"string\"},\"precondition_sha256\":{\"type\":\"string\",\"minLength\":64,\"maxLength\":64},\"edits\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":32,\"items\":{\"type\":\"object\",\"properties\":{\"start_line\":{\"type\":\"integer\",\"minimum\":1},\"start_column\":{\"type\":\"integer\",\"minimum\":1},\"end_line\":{\"type\":\"integer\",\"minimum\":1},\"end_column\":{\"type\":\"integer\",\"minimum\":1},\"new_text\":{\"type\":\"string\",\"maxLength\":32768}},\"required\":[\"start_line\",\"start_column\",\"end_line\",\"end_column\",\"new_text\"],\"additionalProperties\":false}},\"dry_run\":{\"type\":\"boolean\"},\"refresh\":{\"type\":\"boolean\",\"default\":true}},\"required\":[\"script_path\",\"precondition_sha256\",\"edits\"],\"additionalProperties\":false}";
        const string ScriptWriteSchemaJson = "{\"type\":\"object\",\"properties\":{\"script_path\":{\"type\":\"string\"},\"content\":{\"type\":\"string\",\"maxLength\":131072},\"overwrite\":{\"type\":\"boolean\"},\"precondition_sha256\":{\"type\":\"string\",\"minLength\":64,\"maxLength\":64},\"dry_run\":{\"type\":\"boolean\"},\"refresh\":{\"type\":\"boolean\",\"default\":true}},\"required\":[\"script_path\",\"content\"],\"additionalProperties\":false}";
        const string ScriptReadOutputLimitJson = "{\"max_bytes\":65536}";
        const string ScriptValidateOutputLimitJson = "{\"max_bytes\":262144,\"max_diagnostics\":50}";
        const string ScriptCapabilitiesOutputLimitJson = "{\"max_bytes\":4000}";
        const string ScriptTextEditsSpecOutputLimitJson = "{\"max_bytes\":12000}";
        const string ScriptTextEditsApplyOutputLimitJson = "{\"max_bytes\":262144,\"max_edits\":32}";
        const int MaxScriptReadBytes = 65536;
        const long MaxScriptHashBytes = 4194304;
        const int MaxScriptValidateBytes = 262144;
        const int MaxScriptValidateDiagnostics = 50;
        const int MaxScriptTextEditBytes = 262144;
        const int MaxScriptTextEditCount = 32;
        const int MaxScriptTextEditNewTextChars = 32768;
        const int MaxScriptWriteChars = 131072;

        [McpCommand(
            "asset.folder.create",
            Title = "Create asset folder",
            Description = "Create one non-recursive folder under Assets using AssetDatabase",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 30000,
            SchemaJson = FolderCreateSchemaJson,
            Tags = new string[] { "asset", "folder", "safe-write" },
            Capability = SafeWritesFeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "mutation_result",
            FeatureFlag = SafeWritesFeatureFlag)]
        public static CommandExecutionResult AssetFolderCreate(McpCommandContext context)
        {
            var status = RequireEditing("asset.folder.create", false, out var editorState);
            if (status != null)
            {
                return status;
            }

            var rawPath = JsonUtil.ExtractString(context.ArgsJson, "folder_path");
            if (string.IsNullOrEmpty(rawPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument folder_path is required");
            }

            var folderPath = PathGuard.RequireUnityAssetPath(rawPath);
            if (!folderPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error("invalid_field_value", "folder_path must be under Assets/");
            }

            if (folderPath == "Assets" || folderPath == "Assets/" || folderPath.EndsWith("/", StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error("invalid_field_value", "folder_path must reference a child folder under Assets");
            }

            var separator = folderPath.LastIndexOf('/');
            if (separator < "Assets".Length || separator >= folderPath.Length - 1)
            {
                return CommandExecutionResult.Error("invalid_field_value", "folder_path must reference a child folder under Assets");
            }

            if (AssetDatabase.IsValidFolder(folderPath))
            {
                return CommandExecutionResult.Ok(ResultJson(folderPath, false, AssetDatabase.AssetPathToGUID(folderPath), editorState));
            }

            var parentPath = folderPath.Substring(0, separator);
            if (!AssetDatabase.IsValidFolder(parentPath))
            {
                return CommandExecutionResult.Error("invalid_field_value", "Parent folder must already exist");
            }

            var folderName = folderPath.Substring(separator + 1);
            status = RequireEditing("asset.folder.create", false, out editorState);
            if (status != null)
            {
                return status;
            }

            var guid = AssetDatabase.CreateFolder(parentPath, folderName);
            if (string.IsNullOrEmpty(guid))
            {
                return CommandExecutionResult.Error("unity_exception", "AssetDatabase.CreateFolder failed");
            }

            var createdPath = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(createdPath))
            {
                createdPath = folderPath;
            }

            return CommandExecutionResult.Ok(ResultJson(createdPath, true, guid, editorState));
        }

        [McpCommand(
            "asset.move",
            Title = "Move asset",
            Description = "Move one asset or folder under Assets without overwrite",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = AssetMoveSchemaJson,
            Tags = new string[] { "asset", "safe-write" },
            Capability = SafeWritesFeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "mutation_result",
            FeatureFlag = SafeWritesFeatureFlag)]
        public static CommandExecutionResult AssetMove(McpCommandContext context)
        {
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false);
            var status = RequireEditing("asset.move", dryRun, out var editorState);
            if (status != null)
            {
                return status;
            }

            var sourceResult = RequireAssetsPath(context.ArgsJson, "source_path", out var sourcePath);
            if (sourceResult != null)
            {
                return sourceResult;
            }

            var destinationResult = RequireAssetsPath(context.ArgsJson, "destination_path", out var destinationPath);
            if (destinationResult != null)
            {
                return destinationResult;
            }

            if (sourcePath == destinationPath)
            {
                return CommandExecutionResult.Error("invalid_field_value", "source_path and destination_path must differ");
            }

            var sourceGuid = AssetDatabase.AssetPathToGUID(sourcePath);
            if (string.IsNullOrEmpty(sourceGuid))
            {
                return CommandExecutionResult.Error("asset_not_found", "source_path does not exist");
            }

            if (AssetExists(destinationPath))
            {
                return CommandExecutionResult.Error("invalid_field_value", "destination_path already exists; overwrite is not supported");
            }

            var parentPath = ParentAssetPath(destinationPath);
            if (string.IsNullOrEmpty(parentPath) || !AssetDatabase.IsValidFolder(parentPath))
            {
                return CommandExecutionResult.Error("invalid_field_value", "destination parent folder must already exist");
            }

            if (dryRun)
            {
                return CommandExecutionResult.Ok("{\"dry_run\":true,\"would_move\":true,\"source_path\":" + JsonUtil.String(sourcePath)
                    + ",\"destination_path\":" + JsonUtil.String(destinationPath)
                    + ",\"guid\":" + JsonUtil.String(sourceGuid)
                    + ",\"guid_before\":" + JsonUtil.String(sourceGuid)
                    + ",\"precondition_guid\":" + JsonUtil.String(sourceGuid)
                    + ",\"editor_state\":" + editorState.NormalizedStateJson()
                    + ",\"changed_assets\":[]"
                    + ",\"moved_assets\":[{" + MoveRecordFieldsJson(sourcePath, destinationPath, sourceGuid, sourceGuid) + "}]"
                    + ",\"warnings\":[\"Dry-run only; rerun with precondition_guid equal to the current source GUID to move.\"]"
                    + ",\"next_valid_actions\":[\"asset.relocation.preflight\",\"checkpoint.create\",\"asset.move\"]}");
            }

            var currentSourceGuid = AssetDatabase.AssetPathToGUID(sourcePath);
            if (string.IsNullOrEmpty(currentSourceGuid))
            {
                return CommandExecutionResult.Error("precondition_failed", "source_path no longer resolves to the dry-run asset", PreconditionGuidDetailsJson("asset.move", sourcePath, currentSourceGuid));
            }

            var preconditionResult = RequirePreconditionGuid(context.ArgsJson, "asset.move", sourcePath, currentSourceGuid);
            if (preconditionResult != null)
            {
                return preconditionResult;
            }

            if (AssetExists(destinationPath))
            {
                return CommandExecutionResult.Error("precondition_failed", "destination_path changed after preflight and now exists");
            }

            if (!AssetDatabase.IsValidFolder(parentPath))
            {
                return CommandExecutionResult.Error("precondition_failed", "destination parent folder changed after preflight");
            }

            status = RequireEditing("asset.move", false, out editorState);
            if (status != null)
            {
                return status;
            }

            var error = AssetDatabase.MoveAsset(sourcePath, destinationPath);
            if (!string.IsNullOrEmpty(error))
            {
                return CommandExecutionResult.Error("unity_exception", error);
            }

            var guidAfter = AssetDatabase.AssetPathToGUID(destinationPath);
            if (string.IsNullOrEmpty(guidAfter))
            {
                guidAfter = currentSourceGuid;
            }

            return CommandExecutionResult.Ok("{\"moved\":true,\"source_path\":" + JsonUtil.String(sourcePath)
                + ",\"destination_path\":" + JsonUtil.String(destinationPath)
                + ",\"guid\":" + JsonUtil.String(guidAfter)
                + ",\"guid_before\":" + JsonUtil.String(currentSourceGuid)
                + ",\"guid_after\":" + JsonUtil.String(guidAfter)
                + ",\"editor_state\":" + editorState.NormalizedStateJson()
                + ",\"changed_assets\":[" + JsonUtil.String(destinationPath) + "]"
                + ",\"moved_assets\":[{" + MoveRecordFieldsJson(sourcePath, destinationPath, currentSourceGuid, guidAfter) + "}]"
                + ",\"warnings\":[]"
                + ",\"next_valid_actions\":[\"validation.after_edit\",\"asset.index.rebuild\",\"asset.info\"]}");
        }

        [McpCommand(
            "asset.delete",
            Title = "Delete asset",
            Description = "Delete one asset or folder under Assets",
            Risk = "destructive",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = AssetDeleteSchemaJson,
            Tags = new string[] { "asset", "safe-write" },
            Capability = SafeWritesFeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "mutation_result",
            FeatureFlag = SafeWritesFeatureFlag)]
        public static CommandExecutionResult AssetDelete(McpCommandContext context)
        {
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false);
            var status = RequireEditing("asset.delete", dryRun, out var editorState);
            if (status != null)
            {
                return status;
            }

            var pathResult = RequireAssetsPath(context.ArgsJson, "asset_path", out var assetPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid))
            {
                return CommandExecutionResult.Error("asset_not_found", "asset_path does not exist");
            }

            if (dryRun)
            {
                return CommandExecutionResult.Ok("{\"dry_run\":true,\"would_delete\":true,\"asset_path\":" + JsonUtil.String(assetPath)
                    + ",\"guid\":" + JsonUtil.String(guid)
                    + ",\"guid_before\":" + JsonUtil.String(guid)
                    + ",\"precondition_guid\":" + JsonUtil.String(guid)
                    + ",\"editor_state\":" + editorState.NormalizedStateJson()
                    + ",\"changed_assets\":[]"
                    + ",\"deleted_assets\":[{" + AssetRecordFieldsJson(assetPath, guid) + "}]"
                    + ",\"warnings\":[\"Dry-run only; if checkpoint.create is enabled and approved, snapshot the asset before rerunning with precondition_guid equal to its current GUID.\"]"
                    + ",\"next_valid_actions\":[\"asset.risk_report\",\"checkpoint.create\",\"asset.delete\"]}");
            }

            var currentGuid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(currentGuid))
            {
                return CommandExecutionResult.Error("precondition_failed", "asset_path no longer resolves to the dry-run asset", PreconditionGuidDetailsJson("asset.delete", assetPath, currentGuid));
            }

            var preconditionResult = RequirePreconditionGuid(context.ArgsJson, "asset.delete", assetPath, currentGuid);
            if (preconditionResult != null)
            {
                return preconditionResult;
            }

            status = RequireEditing("asset.delete", false, out editorState);
            if (status != null)
            {
                return status;
            }

            if (!AssetDatabase.DeleteAsset(assetPath))
            {
                return CommandExecutionResult.Error("unity_exception", "AssetDatabase.DeleteAsset failed");
            }

            return CommandExecutionResult.Ok("{\"deleted\":true,\"asset_path\":" + JsonUtil.String(assetPath)
                + ",\"guid\":" + JsonUtil.String(currentGuid)
                + ",\"guid_before\":" + JsonUtil.String(currentGuid)
                + ",\"guid_after\":\"\""
                + ",\"editor_state\":" + editorState.NormalizedStateJson()
                + ",\"changed_assets\":[" + JsonUtil.String(assetPath) + "]"
                + ",\"deleted_assets\":[{" + AssetRecordFieldsJson(assetPath, currentGuid) + "}]"
                + ",\"warnings\":[\"Asset was deleted; if an approved checkpoint exists, inspect it before any restore.\"]"
                + ",\"next_valid_actions\":[\"validation.after_edit\",\"asset.index.rebuild\",\"checkpoint.list\"]}");
        }

        [McpCommand(
            "script.read",
            Title = "Read script",
            Description = "Read one bounded C# script under Assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = ScriptReadSchemaJson,
            Tags = new string[] { "asset", "script" },
            Capability = ReadonlyFeatureFlag,
            ResultShape = "text_artifact",
            OutputLimitJson = ScriptReadOutputLimitJson,
            FeatureFlag = ReadonlyFeatureFlag)]
        public static CommandExecutionResult ScriptRead(McpCommandContext context)
        {
            var pathResult = RequireScriptPath(context.ArgsJson, out var scriptPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            var fullPath = AbsoluteAssetPath(scriptPath);
            if (!File.Exists(fullPath))
            {
                return CommandExecutionResult.Error("asset_not_found", "script_path does not exist");
            }

            var maxBytes = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_bytes", 32768), 1, MaxScriptReadBytes);
            var content = ReadUtf8Prefix(fullPath, maxBytes, out var byteLength, out var truncated);
            return CommandExecutionResult.Ok("{\"script_path\":" + JsonUtil.String(scriptPath)
                + ",\"guid\":" + JsonUtil.String(AssetDatabase.AssetPathToGUID(scriptPath))
                + ",\"encoding\":\"utf-8\",\"byte_length\":" + byteLength
                + ",\"max_bytes\":" + maxBytes
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"content\":" + JsonUtil.String(content) + "}");
        }

        [McpCommand(
            "script.hash",
            Title = "Hash script",
            Description = "Read SHA-256 metadata for one C# script under Assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = ScriptHashSchemaJson,
            Tags = new string[] { "asset", "script" },
            Capability = ReadonlyFeatureFlag,
            ResultShape = "metadata",
            FeatureFlag = ReadonlyFeatureFlag)]
        public static CommandExecutionResult ScriptHash(McpCommandContext context)
        {
            var pathResult = RequireScriptPath(context.ArgsJson, out var scriptPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            var fullPath = AbsoluteAssetPath(scriptPath);
            if (!File.Exists(fullPath))
            {
                return CommandExecutionResult.Error("asset_not_found", "script_path does not exist");
            }

            var info = new FileInfo(fullPath);
            if (info.Length > MaxScriptHashBytes)
            {
                return CommandExecutionResult.Error("invalid_field_value", "script_path exceeds maximum hashable script size");
            }

            string sha256;
            using (var stream = File.OpenRead(fullPath))
            using (var hash = SHA256.Create())
            {
                sha256 = Hex(hash.ComputeHash(stream));
            }

            return CommandExecutionResult.Ok("{\"script_path\":" + JsonUtil.String(scriptPath)
                + ",\"guid\":" + JsonUtil.String(AssetDatabase.AssetPathToGUID(scriptPath))
                + ",\"byte_length\":" + info.Length
                + ",\"sha256\":" + JsonUtil.String(sha256)
                + ",\"last_write_time_utc\":" + JsonUtil.String(info.LastWriteTimeUtc.ToString("o")) + "}");
        }

        [McpCommand(
            "script.validate",
            Title = "Validate script",
            Description = "Validate bounded C# script structure under Assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = ScriptValidateSchemaJson,
            Tags = new string[] { "asset", "script", "validation" },
            Capability = ReadonlyFeatureFlag,
            ResultShape = "validation_result",
            OutputLimitJson = ScriptValidateOutputLimitJson,
            FeatureFlag = ReadonlyFeatureFlag)]
        public static CommandExecutionResult ScriptValidate(McpCommandContext context)
        {
            var pathResult = RequireScriptPath(context.ArgsJson, out var scriptPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            var level = JsonUtil.ExtractString(context.ArgsJson, "level");
            if (string.IsNullOrEmpty(level))
            {
                level = "basic";
            }

            if (level != "basic" && level != "standard")
            {
                return CommandExecutionResult.Error("invalid_field_value", "level must be basic or standard");
            }

            var fullPath = AbsoluteAssetPath(scriptPath);
            if (!File.Exists(fullPath))
            {
                return CommandExecutionResult.Error("asset_not_found", "script_path does not exist");
            }

            var maxBytes = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_bytes", MaxScriptValidateBytes), 1, MaxScriptValidateBytes);
            var info = new FileInfo(fullPath);
            if (info.Length > maxBytes)
            {
                return CommandExecutionResult.Error("invalid_field_value", "script_path exceeds max_bytes for validation");
            }

            string content;
            try
            {
                content = File.ReadAllText(fullPath, new UTF8Encoding(false, true));
            }
            catch (DecoderFallbackException)
            {
                return CommandExecutionResult.Error("invalid_field_value", "script_path must be valid UTF-8 text");
            }
            catch (IOException exception)
            {
                return CommandExecutionResult.Error("unity_exception", exception.Message);
            }

            if (content.IndexOf('\0') >= 0)
            {
                return CommandExecutionResult.Error("invalid_field_value", "script_path must be text without NUL bytes");
            }

            var diagnostics = new ScriptValidationDiagnostics();
            ValidateScriptStructure(content, diagnostics);
            if (level == "standard")
            {
                AddStandardScriptWarnings(content, diagnostics);
            }

            var includeDiagnostics = JsonUtil.ExtractBool(context.ArgsJson, "include_diagnostics", false);
            return CommandExecutionResult.Ok(ScriptValidationResultJson(scriptPath, level, info.Length, maxBytes, includeDiagnostics, diagnostics));
        }

        [McpCommand(
            "script.capabilities",
            Title = "Script capabilities",
            Description = "Read local script operation capabilities for Unity AI Assistant compatibility",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = EmptySchemaJson,
            Tags = new string[] { "asset", "script", "capabilities", "unity-ai-assistant" },
            Capability = ReadonlyFeatureFlag,
            ResultShape = "capabilities",
            OutputLimitJson = ScriptCapabilitiesOutputLimitJson,
            FeatureFlag = ReadonlyFeatureFlag)]
        public static CommandExecutionResult ScriptCapabilities(McpCommandContext context)
        {
            return CommandExecutionResult.Ok(ScriptCapabilitiesJson());
        }

        [McpCommand(
            "script.text_edits.spec",
            Title = "Script text edits spec",
            Description = "Read the local script text-edits specification for Unity AI Assistant compatibility",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = EmptySchemaJson,
            Tags = new string[] { "asset", "script", "spec", "unity-ai-assistant" },
            Capability = ReadonlyFeatureFlag,
            ResultShape = "text_artifact",
            OutputLimitJson = ScriptTextEditsSpecOutputLimitJson,
            FeatureFlag = ReadonlyFeatureFlag)]
        public static CommandExecutionResult ScriptTextEditsSpec(McpCommandContext context)
        {
            return CommandExecutionResult.Ok(ScriptTextEditsSpecJson());
        }

        [McpCommand(
            "script.text_edits.apply",
            Title = "Apply script text edits",
            Description = "Apply bounded line/column text edits to one C# script under Assets",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = ScriptTextEditsApplySchemaJson,
            Tags = new string[] { "asset", "script", "safe-write" },
            Capability = SafeWritesFeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "mutation_result",
            OutputLimitJson = ScriptTextEditsApplyOutputLimitJson,
            FeatureFlag = SafeWritesFeatureFlag)]
        public static CommandExecutionResult ScriptTextEditsApply(McpCommandContext context)
        {
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false);
            var status = RequireEditing("script.text_edits.apply", dryRun, out var editorState);
            if (status != null)
            {
                return status;
            }

            var pathResult = RequireScriptPath(context.ArgsJson, out var scriptPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            var preconditionSha256 = JsonUtil.ExtractString(context.ArgsJson, "precondition_sha256").Trim().ToLowerInvariant();
            if (!IsSha256Hex(preconditionSha256))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument precondition_sha256 must be a 64-character SHA-256 hex string");
            }

            var fullPath = AbsoluteAssetPath(scriptPath);
            if (!File.Exists(fullPath))
            {
                return CommandExecutionResult.Error("asset_not_found", "script_path does not exist");
            }

            var info = new FileInfo(fullPath);
            if (info.Length > MaxScriptTextEditBytes)
            {
                return CommandExecutionResult.Error("invalid_field_value", "script_path exceeds maximum editable script size");
            }

            var currentSha256 = Sha256File(fullPath);
            if (!string.Equals(currentSha256, preconditionSha256, StringComparison.OrdinalIgnoreCase))
            {
                return CommandExecutionResult.Error("precondition_failed", "precondition_sha256 does not match current script content");
            }

            string content;
            try
            {
                content = File.ReadAllText(fullPath, new UTF8Encoding(false, true));
            }
            catch (DecoderFallbackException)
            {
                return CommandExecutionResult.Error("invalid_field_value", "script_path must be valid UTF-8 text");
            }
            catch (IOException exception)
            {
                return CommandExecutionResult.Error("unity_exception", exception.Message);
            }

            if (content.IndexOf('\0') >= 0)
            {
                return CommandExecutionResult.Error("invalid_field_value", "script_path must be text without NUL bytes");
            }

            var editsJson = ExtractArray(context.ArgsJson, "edits");
            if (string.IsNullOrEmpty(editsJson))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument edits is required");
            }

            if (!TryParseScriptTextEdits(editsJson, out var edits, out var editError))
            {
                return editError;
            }

            if (!TryResolveScriptTextEdits(content, edits, out var resolvedEdits, out var resolveError))
            {
                return resolveError;
            }

            var updatedContent = ApplyResolvedScriptTextEdits(content, resolvedEdits);
            var updatedByteLength = Encoding.UTF8.GetByteCount(updatedContent);
            if (updatedByteLength > MaxScriptTextEditBytes)
            {
                return CommandExecutionResult.Error("invalid_field_value", "edited script exceeds maximum editable script size");
            }

            var updatedSha256 = Sha256Utf8(updatedContent);
            var refresh = JsonUtil.ExtractBool(context.ArgsJson, "refresh", true);
            if (dryRun)
            {
                return CommandExecutionResult.Ok("{\"dry_run\":true,\"would_apply\":true,\"script_path\":" + JsonUtil.String(scriptPath)
                    + ",\"edit_count\":" + resolvedEdits.Count
                    + ",\"precondition_sha256\":" + JsonUtil.String(preconditionSha256)
                    + ",\"original_sha256\":" + JsonUtil.String(currentSha256)
                    + ",\"result_sha256\":" + JsonUtil.String(updatedSha256)
                    + ",\"byte_length\":" + updatedByteLength
                    + ",\"editor_state\":" + editorState.NormalizedStateJson()
                    + ",\"changed_assets\":[]"
                    + ",\"imported_assets\":[]"
                    + ",\"next_valid_actions\":[\"script.text_edits.apply\",\"validation.after_edit\"]}");
            }

            if (!File.Exists(fullPath))
            {
                return CommandExecutionResult.Error("precondition_failed", "script_path changed after preflight and no longer exists", PreconditionHashDetailsJson("script.text_edits.apply", scriptPath, string.Empty));
            }

            var preMutationSha256 = Sha256File(fullPath);
            if (!string.Equals(preMutationSha256, preconditionSha256, StringComparison.OrdinalIgnoreCase))
            {
                return CommandExecutionResult.Error("precondition_failed", "precondition_sha256 no longer matches current script content", PreconditionHashDetailsJson("script.text_edits.apply", scriptPath, preMutationSha256));
            }

            status = RequireEditing("script.text_edits.apply", false, out editorState);
            if (status != null)
            {
                return status;
            }

            File.WriteAllText(fullPath, updatedContent, new UTF8Encoding(false));
            if (refresh)
            {
                AssetDatabase.ImportAsset(scriptPath, ImportAssetOptions.ForceUpdate);
            }

            return CommandExecutionResult.Ok("{\"applied\":true,\"script_path\":" + JsonUtil.String(scriptPath)
                + ",\"edit_count\":" + resolvedEdits.Count
                + ",\"precondition_sha256\":" + JsonUtil.String(preconditionSha256)
                + ",\"original_sha256\":" + JsonUtil.String(currentSha256)
                + ",\"sha256\":" + JsonUtil.String(updatedSha256)
                + ",\"byte_length\":" + updatedByteLength
                + ",\"refreshed\":" + JsonUtil.Bool(refresh)
                + ",\"guid\":" + JsonUtil.String(AssetDatabase.AssetPathToGUID(scriptPath))
                + ",\"editor_state\":" + editorState.NormalizedStateJson()
                + ",\"changed_assets\":[" + JsonUtil.String(scriptPath) + "]"
                + ",\"imported_assets\":" + (refresh ? "[" + JsonUtil.String(scriptPath) + "]" : "[]")
                + ",\"next_valid_actions\":[\"validation.after_edit\",\"compile.status\"]}");
        }

        [McpCommand(
            "script.write",
            Title = "Write script",
            Description = "Create or overwrite one C# script under Assets",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = ScriptWriteSchemaJson,
            Tags = new string[] { "asset", "script", "safe-write" },
            Capability = SafeWritesFeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "mutation_result",
            FeatureFlag = SafeWritesFeatureFlag)]
        public static CommandExecutionResult ScriptWrite(McpCommandContext context)
        {
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false);
            var status = RequireEditing("script.write", dryRun, out var editorState);
            if (status != null)
            {
                return status;
            }

            var pathResult = RequireScriptPath(context.ArgsJson, out var scriptPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            var content = JsonUtil.ExtractString(context.ArgsJson, "content");
            if (string.IsNullOrEmpty(content))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument content is required");
            }

            if (content.Length > MaxScriptWriteChars || content.IndexOf('\0') >= 0)
            {
                return CommandExecutionResult.Error("invalid_field_value", "content must be bounded text without NUL bytes");
            }

            var parentPath = ParentAssetPath(scriptPath);
            if (string.IsNullOrEmpty(parentPath) || !AssetDatabase.IsValidFolder(parentPath))
            {
                return CommandExecutionResult.Error("invalid_field_value", "script parent folder must already exist");
            }

            var fullPath = AbsoluteAssetPath(scriptPath);
            var exists = File.Exists(fullPath) || AssetExists(scriptPath);
            var overwrite = JsonUtil.ExtractBool(context.ArgsJson, "overwrite", false);
            var refresh = JsonUtil.ExtractBool(context.ArgsJson, "refresh", true);
            if (exists && !overwrite)
            {
                return CommandExecutionResult.Error("invalid_field_value", "script_path already exists; set overwrite:true to replace it");
            }

            var byteLength = Encoding.UTF8.GetByteCount(content);
            if (dryRun)
            {
                var currentSha256 = exists && File.Exists(fullPath) ? Sha256File(fullPath) : string.Empty;
                return CommandExecutionResult.Ok("{\"dry_run\":true,\"would_write\":true,\"script_path\":" + JsonUtil.String(scriptPath)
                    + ",\"exists\":" + JsonUtil.Bool(exists)
                    + ",\"overwrite\":" + JsonUtil.Bool(overwrite)
                    + ",\"precondition_sha256\":" + JsonUtil.String(currentSha256)
                    + ",\"byte_length\":" + byteLength
                    + ",\"editor_state\":" + editorState.NormalizedStateJson()
                    + ",\"changed_assets\":[]"
                    + ",\"imported_assets\":[]"
                    + ",\"next_valid_actions\":[\"script.write\",\"validation.after_edit\"]}");
            }

            var originalSha256 = string.Empty;
            if (exists)
            {
                if (!File.Exists(fullPath))
                {
                    return CommandExecutionResult.Error("asset_not_found", "script_path exists in AssetDatabase but the script file was not found");
                }

                originalSha256 = Sha256File(fullPath);
                var preconditionSha256 = JsonUtil.ExtractString(context.ArgsJson, "precondition_sha256").Trim().ToLowerInvariant();
                if (!IsSha256Hex(preconditionSha256))
                {
                    return CommandExecutionResult.Error("precondition_required", "precondition_sha256 is required when overwrite:true replaces an existing script", PreconditionHashDetailsJson("script.write", scriptPath, originalSha256));
                }

                if (!string.Equals(originalSha256, preconditionSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return CommandExecutionResult.Error("precondition_failed", "precondition_sha256 does not match current script content", PreconditionHashDetailsJson("script.write", scriptPath, originalSha256));
                }
            }

            var existsBeforeMutation = File.Exists(fullPath) || AssetExists(scriptPath);
            if (exists != existsBeforeMutation)
            {
                var currentSha256 = File.Exists(fullPath) ? Sha256File(fullPath) : string.Empty;
                return CommandExecutionResult.Error("precondition_failed", "script_path existence changed after preflight", PreconditionHashDetailsJson("script.write", scriptPath, currentSha256));
            }

            if (exists)
            {
                var currentSha256 = File.Exists(fullPath) ? Sha256File(fullPath) : string.Empty;
                if (!string.Equals(currentSha256, originalSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return CommandExecutionResult.Error("precondition_failed", "script_path content changed after preflight", PreconditionHashDetailsJson("script.write", scriptPath, currentSha256));
                }
            }

            if (!AssetDatabase.IsValidFolder(parentPath))
            {
                return CommandExecutionResult.Error("precondition_failed", "script parent folder changed after preflight");
            }

            status = RequireEditing("script.write", false, out editorState);
            if (status != null)
            {
                return status;
            }

            File.WriteAllText(fullPath, content, new UTF8Encoding(false));
            if (refresh)
            {
                AssetDatabase.ImportAsset(scriptPath, ImportAssetOptions.ForceUpdate);
            }

            var guid = AssetDatabase.AssetPathToGUID(scriptPath);
            var sha256 = Sha256Utf8(content);
            return CommandExecutionResult.Ok("{\"written\":true,\"script_path\":" + JsonUtil.String(scriptPath)
                + ",\"created\":" + JsonUtil.Bool(!exists)
                + ",\"overwritten\":" + JsonUtil.Bool(exists)
                + ",\"original_sha256\":" + JsonUtil.String(originalSha256)
                + ",\"sha256\":" + JsonUtil.String(sha256)
                + ",\"byte_length\":" + byteLength
                + ",\"refreshed\":" + JsonUtil.Bool(refresh)
                + ",\"guid\":" + JsonUtil.String(guid)
                + ",\"editor_state\":" + editorState.NormalizedStateJson()
                + ",\"changed_assets\":[" + JsonUtil.String(scriptPath) + "]"
                + ",\"imported_assets\":" + (refresh ? "[" + JsonUtil.String(scriptPath) + "]" : "[]")
                + ",\"next_valid_actions\":[\"validation.after_edit\",\"compile.status\"]}");
        }

        static string ScriptTextEditsSpecJson()
        {
            var exampleSha = new string('0', 64);
            return "{\"uri\":\"unity://spec/script-edits\""
                + ",\"command\":\"script.text_edits.apply\""
                + ",\"status\":\"supported_local_parity\""
                + ",\"description\":\"Cloudless bounded line/column C# script edits with SHA-256 precondition.\""
                + ",\"schema\":" + ScriptTextEditsApplySchemaJson
                + ",\"supported_operations\":[\"bounded_line_column_replace\"]"
                + ",\"edit_fields\":[\"start_line\",\"start_column\",\"end_line\",\"end_column\",\"new_text\"]"
                + ",\"safety\":[\"Assets-only .cs paths\",\"64-hex precondition_sha256 required\",\"max 32 edits\",\"overlapping edits rejected\",\"dry_run supported\",\"optional AssetDatabase import refresh\"]"
                + ",\"unsupported_structured_ops\":[\"replace_method\",\"insert_method\",\"delete_method\",\"anchor_insert\",\"anchor_delete\",\"anchor_replace\"]"
                + ",\"example_args_json\":{\"script_path\":\"Assets/Scripts/A.cs\",\"precondition_sha256\":" + JsonUtil.String(exampleSha)
                + ",\"edits\":[{\"start_line\":1,\"start_column\":1,\"end_line\":1,\"end_column\":1,\"new_text\":" + JsonUtil.String("using UnityEngine;\n") + "}],\"dry_run\":true}"
                + "}";
        }

        static string ScriptCapabilitiesJson()
        {
            return "{\"command\":\"script.capabilities\""
                + ",\"compatible_with\":\"Unity.ManageScript_capabilities\""
                + ",\"status\":\"supported_local_parity\""
                + ",\"ops\":[]"
                + ",\"text_ops\":[\"bounded_line_column_replace\"]"
                + ",\"max_edit_payload_bytes\":" + MaxScriptTextEditBytes
                + ",\"max_edit_count\":" + MaxScriptTextEditCount
                + ",\"max_new_text_chars\":" + MaxScriptTextEditNewTextChars
                + ",\"guards\":{\"assets_only\":true,\"cs_only\":true,\"sha256_precondition_required\":true,\"overlapping_edits_rejected\":true,\"dry_run_supported\":true,\"using_guard\":false}"
                + ",\"extras\":{\"get_sha\":true,\"script_read\":true,\"script_validate\":true,\"text_edits_spec\":true,\"script_write\":true}"
                + ",\"local_commands\":{\"read\":\"script.read\",\"hash\":\"script.hash\",\"validate\":\"script.validate\",\"text_edits_spec\":\"script.text_edits.spec\",\"apply_text_edits\":\"script.text_edits.apply\",\"write\":\"script.write\"}"
                + ",\"unsupported_structured_ops\":[\"replace_class\",\"delete_class\",\"replace_method\",\"delete_method\",\"insert_method\",\"anchor_insert\",\"anchor_delete\",\"anchor_replace\"]"
                + ",\"unsupported_text_ops\":[\"replace_range\",\"regex_replace\",\"prepend\",\"append\"]"
                + ",\"notes\":[\"Only bounded line/column edits are supported.\",\"Use script.hash before script.text_edits.apply.\",\"No cloud, relay, AI Gateway, or official Unity subscription dependency.\"]"
                + "}";
        }

        static CommandExecutionResult RequireEditing(string commandId, bool dryRun, out EditorEditModeEligibility editorState)
        {
            editorState = EditorStatusCommands.CaptureEditModeEligibility();
            return RequireEditing(commandId, dryRun, editorState);
        }

        internal static CommandExecutionResult RequireEditing(string commandId, bool dryRun, EditorEditModeEligibility editorState)
        {
            if (editorState == null)
            {
                return CommandExecutionResult.Error("editor_not_ready", commandId + " requires an authoritative Editor state",
                    "{\"contract\":\"sgg.unity_mcp.asset_write_eligibility.v1\",\"command\":" + JsonUtil.String(commandId)
                    + ",\"required_editor_status\":\"editing\",\"dry_run\":" + JsonUtil.Bool(dryRun)
                    + ",\"mutation_state\":\"not_applied\",\"dry_run_authorizes_execution\":false,\"editor_state\":null"
                    + ",\"next_valid_actions\":[\"editor.state.get\",\"compile.status\",\"retry_after_editor_ready\"]}");
            }

            if (editorState.IsEligible)
            {
                return null;
            }

            var unsupportedInPlayMode = editorState.IsPlaying;
            var code = unsupportedInPlayMode ? "unsupported_in_play_mode" : "editor_not_ready";
            var message = unsupportedInPlayMode
                ? commandId + " requires Edit Mode and cannot run during Play Mode or a playmode transition"
                : commandId + " requires an idle Edit Mode Editor";
            return CommandExecutionResult.Error(code, message,
                "{\"contract\":\"sgg.unity_mcp.asset_write_eligibility.v1\""
                + ",\"command\":" + JsonUtil.String(commandId)
                + ",\"required_editor_status\":\"editing\""
                + ",\"dry_run\":" + JsonUtil.Bool(dryRun)
                + ",\"mutation_state\":\"not_applied\""
                + ",\"dry_run_authorizes_execution\":false"
                + ",\"editor_state\":" + editorState.NormalizedStateJson()
                + ",\"next_valid_actions\":[\"editor.state.get\",\"compile.status\",\"retry_after_editor_ready\"]}");
        }

        static CommandExecutionResult RequireAssetsPath(string argsJson, string fieldName, out string assetPath)
        {
            assetPath = string.Empty;
            var rawPath = JsonUtil.ExtractString(argsJson, fieldName);
            if (string.IsNullOrEmpty(rawPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument " + fieldName + " is required");
            }

            assetPath = PathGuard.RequireUnityAssetPath(rawPath);
            if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error("invalid_field_value", fieldName + " must be under Assets/");
            }

            return null;
        }

        static CommandExecutionResult RequireScriptPath(string argsJson, out string scriptPath)
        {
            var result = RequireAssetsPath(argsJson, "script_path", out scriptPath);
            if (result != null)
            {
                return result;
            }

            if (!scriptPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                return CommandExecutionResult.Error("invalid_field_value", "script_path must point to a .cs file under Assets/");
            }

            return null;
        }

        internal static CommandExecutionResult RequirePreconditionGuid(string argsJson, string commandId, string assetPath, string currentGuid)
        {
            var preconditionGuid = JsonUtil.ExtractString(argsJson, "precondition_guid").Trim().ToLowerInvariant();
            if (!IsUnityGuid(preconditionGuid))
            {
                return CommandExecutionResult.Error("precondition_required", "precondition_guid is required for " + commandId + " when dry_run is false", PreconditionGuidDetailsJson(commandId, assetPath, currentGuid));
            }

            if (!string.Equals(preconditionGuid, currentGuid, StringComparison.OrdinalIgnoreCase))
            {
                return CommandExecutionResult.Error("precondition_failed", "precondition_guid does not match the current asset GUID", PreconditionGuidDetailsJson(commandId, assetPath, currentGuid));
            }

            return null;
        }

        static string PreconditionGuidDetailsJson(string commandId, string assetPath, string currentGuid)
        {
            return "{\"required_precondition\":\"precondition_guid\""
                + ",\"command\":" + JsonUtil.String(commandId)
                + ",\"asset_path\":" + JsonUtil.String(assetPath)
                + ",\"current_guid\":" + JsonUtil.String(currentGuid)
                + ",\"next_valid_actions\":[\"asset.risk_report\",\"asset.relocation.preflight\",\"checkpoint.create\",\"retry_with_precondition_guid\"]}";
        }

        static string PreconditionHashDetailsJson(string commandId, string scriptPath, string currentSha256)
        {
            return "{\"required_precondition\":\"precondition_sha256\""
                + ",\"command\":" + JsonUtil.String(commandId)
                + ",\"script_path\":" + JsonUtil.String(scriptPath)
                + ",\"current_sha256\":" + JsonUtil.String(currentSha256)
                + ",\"next_valid_actions\":[\"script.hash\",\"checkpoint.create\",\"retry_with_precondition_sha256\"]}";
        }

        static string AbsoluteAssetPath(string assetPath)
        {
            var projectRoot = Directory.GetParent(Application.dataPath);
            var root = projectRoot == null ? Application.dataPath : projectRoot.FullName;
            return Path.Combine(root, assetPath.Replace('/', Path.DirectorySeparatorChar));
        }

        static string ReadUtf8Prefix(string fullPath, int maxBytes, out long byteLength, out bool truncated)
        {
            var info = new FileInfo(fullPath);
            byteLength = info.Length;
            var bytesToRead = (int)Math.Min(byteLength, maxBytes + 1L);
            var buffer = new byte[bytesToRead];
            using (var stream = File.OpenRead(fullPath))
            {
                var offset = 0;
                while (offset < bytesToRead)
                {
                    var read = stream.Read(buffer, offset, bytesToRead - offset);
                    if (read <= 0)
                    {
                        break;
                    }

                    offset += read;
                }
            }

            truncated = byteLength > maxBytes;
            var count = truncated ? maxBytes : bytesToRead;
            return Encoding.UTF8.GetString(buffer, 0, count);
        }

        static void ValidateScriptStructure(string content, ScriptValidationDiagnostics diagnostics)
        {
            var stack = new List<BracketFrame>();
            var line = 1;
            var column = 0;
            var inLineComment = false;
            var inBlockComment = false;
            var inString = false;
            var verbatimString = false;
            var inChar = false;
            var escaped = false;

            for (var index = 0; index < content.Length; index++)
            {
                var character = content[index];
                var currentLine = line;
                var currentColumn = column + 1;

                if (inLineComment)
                {
                    if (character == '\n')
                    {
                        inLineComment = false;
                    }

                    AdvancePosition(character, ref line, ref column);
                    continue;
                }

                if (inBlockComment)
                {
                    if (character == '*' && index + 1 < content.Length && content[index + 1] == '/')
                    {
                        inBlockComment = false;
                        index++;
                        column++;
                    }

                    AdvancePosition(character, ref line, ref column);
                    continue;
                }

                if (inString)
                {
                    if (verbatimString)
                    {
                        if (character == '"' && index + 1 < content.Length && content[index + 1] == '"')
                        {
                            index++;
                            column++;
                        }
                        else if (character == '"')
                        {
                            inString = false;
                            verbatimString = false;
                        }
                    }
                    else if (escaped)
                    {
                        escaped = false;
                    }
                    else if (character == '\\')
                    {
                        escaped = true;
                    }
                    else if (character == '"')
                    {
                        inString = false;
                    }
                    else if (character == '\n')
                    {
                        AddDiagnostic(diagnostics, "error", "Unterminated string literal before newline", currentLine, currentColumn);
                        inString = false;
                    }

                    AdvancePosition(character, ref line, ref column);
                    continue;
                }

                if (inChar)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (character == '\\')
                    {
                        escaped = true;
                    }
                    else if (character == '\'')
                    {
                        inChar = false;
                    }
                    else if (character == '\n')
                    {
                        AddDiagnostic(diagnostics, "error", "Unterminated character literal before newline", currentLine, currentColumn);
                        inChar = false;
                    }

                    AdvancePosition(character, ref line, ref column);
                    continue;
                }

                if (character == '/' && index + 1 < content.Length && content[index + 1] == '/')
                {
                    inLineComment = true;
                    index++;
                    column++;
                    AdvancePosition(character, ref line, ref column);
                    continue;
                }

                if (character == '/' && index + 1 < content.Length && content[index + 1] == '*')
                {
                    inBlockComment = true;
                    index++;
                    column++;
                    AdvancePosition(character, ref line, ref column);
                    continue;
                }

                if (character == '@' && index + 1 < content.Length && content[index + 1] == '"')
                {
                    inString = true;
                    verbatimString = true;
                    index++;
                    column++;
                    AdvancePosition(character, ref line, ref column);
                    continue;
                }

                if (character == '"')
                {
                    inString = true;
                    escaped = false;
                }
                else if (character == '\'')
                {
                    inChar = true;
                    escaped = false;
                }
                else if (character == '{' || character == '(' || character == '[')
                {
                    stack.Add(new BracketFrame(character, currentLine, currentColumn));
                }
                else if (character == '}' || character == ')' || character == ']')
                {
                    if (stack.Count == 0 || !BracketsMatch(stack[stack.Count - 1].Character, character))
                    {
                        AddDiagnostic(diagnostics, "error", "Unmatched closing bracket " + character, currentLine, currentColumn);
                    }
                    else
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }
                }

                AdvancePosition(character, ref line, ref column);
            }

            if (inBlockComment)
            {
                AddDiagnostic(diagnostics, "error", "Unterminated block comment", line, Math.Max(column, 1));
            }

            if (inString)
            {
                AddDiagnostic(diagnostics, "error", "Unterminated string literal", line, Math.Max(column, 1));
            }

            if (inChar)
            {
                AddDiagnostic(diagnostics, "error", "Unterminated character literal", line, Math.Max(column, 1));
            }

            for (var index = stack.Count - 1; index >= 0; index--)
            {
                var frame = stack[index];
                AddDiagnostic(diagnostics, "error", "Unmatched opening bracket " + frame.Character, frame.Line, frame.Column);
            }
        }

        static void AddStandardScriptWarnings(string content, ScriptValidationDiagnostics diagnostics)
        {
            if ((content.IndexOf("MonoBehaviour", StringComparison.Ordinal) >= 0 || content.IndexOf("ScriptableObject", StringComparison.Ordinal) >= 0)
                && content.IndexOf("using UnityEngine;", StringComparison.Ordinal) < 0)
            {
                AddDiagnostic(diagnostics, "warning", "Common Unity base types usually require using UnityEngine", 1, 1);
            }

            if (content.IndexOf("void Update", StringComparison.Ordinal) >= 0)
            {
                AddWarningAtFirstMatch(content, diagnostics, "FindObjectOfType", "FindObjectOfType in Update can be expensive");
                AddWarningAtFirstMatch(content, diagnostics, "GameObject.Find", "GameObject.Find in Update can be expensive");
            }

            if (content.IndexOf("IEnumerator", StringComparison.Ordinal) >= 0
                && content.IndexOf("using System.Collections", StringComparison.Ordinal) < 0)
            {
                AddWarningAtFirstMatch(content, diagnostics, "IEnumerator", "IEnumerator coroutines usually require using System.Collections");
            }
        }

        static void AddWarningAtFirstMatch(string content, ScriptValidationDiagnostics diagnostics, string text, string message)
        {
            var index = content.IndexOf(text, StringComparison.Ordinal);
            if (index < 0)
            {
                return;
            }

            FindLineColumn(content, index, out var line, out var column);
            AddDiagnostic(diagnostics, "warning", message, line, column);
        }

        static void AddDiagnostic(ScriptValidationDiagnostics diagnostics, string severity, string message, int line, int column)
        {
            if (severity == "error")
            {
                diagnostics.Errors++;
            }
            else
            {
                diagnostics.Warnings++;
            }

            if (diagnostics.Items.Count >= MaxScriptValidateDiagnostics)
            {
                diagnostics.Truncated = true;
                return;
            }

            diagnostics.Items.Add(new ScriptValidationDiagnostic(severity, message, line, column));
        }

        static void FindLineColumn(string content, int offset, out int line, out int column)
        {
            line = 1;
            column = 1;
            for (var index = 0; index < content.Length && index < offset; index++)
            {
                if (content[index] == '\n')
                {
                    line++;
                    column = 1;
                }
                else
                {
                    column++;
                }
            }
        }

        static void AdvancePosition(char character, ref int line, ref int column)
        {
            if (character == '\n')
            {
                line++;
                column = 0;
            }
            else
            {
                column++;
            }
        }

        static bool BracketsMatch(char open, char close)
        {
            return (open == '{' && close == '}')
                || (open == '(' && close == ')')
                || (open == '[' && close == ']');
        }

        static bool TryParseScriptTextEdits(string editsJson, out List<ScriptTextEdit> edits, out CommandExecutionResult error)
        {
            edits = new List<ScriptTextEdit>();
            error = null;

            var editObjects = new List<string>();
            if (!TryExtractObjectArrayItems(editsJson, editObjects))
            {
                error = CommandExecutionResult.Error("schema_validation_failed", "edits must be an array of edit objects");
                return false;
            }

            if (editObjects.Count == 0)
            {
                error = CommandExecutionResult.Error("schema_validation_failed", "edits must include at least one edit");
                return false;
            }

            if (editObjects.Count > MaxScriptTextEditCount)
            {
                error = CommandExecutionResult.Error("invalid_field_value", "edits exceeds maximum edit count");
                return false;
            }

            foreach (var editJson in editObjects)
            {
                if (!ContainsJsonProperty(editJson, "new_text"))
                {
                    error = CommandExecutionResult.Error("schema_validation_failed", "Each edit requires new_text");
                    return false;
                }

                var startLine = JsonUtil.ExtractInt(editJson, "start_line", 0);
                var startColumn = JsonUtil.ExtractInt(editJson, "start_column", 0);
                var endLine = JsonUtil.ExtractInt(editJson, "end_line", 0);
                var endColumn = JsonUtil.ExtractInt(editJson, "end_column", 0);
                if (startLine < 1 || startColumn < 1 || endLine < 1 || endColumn < 1)
                {
                    error = CommandExecutionResult.Error("invalid_field_value", "Edit line and column values must be positive");
                    return false;
                }

                var newText = JsonUtil.ExtractString(editJson, "new_text");
                if (newText.Length > MaxScriptTextEditNewTextChars || newText.IndexOf('\0') >= 0)
                {
                    error = CommandExecutionResult.Error("invalid_field_value", "new_text must be bounded text without NUL bytes");
                    return false;
                }

                edits.Add(new ScriptTextEdit(startLine, startColumn, endLine, endColumn, newText));
            }

            return true;
        }

        static bool TryResolveScriptTextEdits(string content, List<ScriptTextEdit> edits, out List<ResolvedScriptTextEdit> resolvedEdits, out CommandExecutionResult error)
        {
            resolvedEdits = new List<ResolvedScriptTextEdit>();
            error = null;

            foreach (var edit in edits)
            {
                if (!TryLineColumnToOffset(content, edit.StartLine, edit.StartColumn, out var startOffset, out var startError))
                {
                    error = CommandExecutionResult.Error("invalid_field_value", startError);
                    return false;
                }

                if (!TryLineColumnToOffset(content, edit.EndLine, edit.EndColumn, out var endOffset, out var endError))
                {
                    error = CommandExecutionResult.Error("invalid_field_value", endError);
                    return false;
                }

                if (startOffset > endOffset)
                {
                    error = CommandExecutionResult.Error("invalid_field_value", "Edit start must not be after edit end");
                    return false;
                }

                resolvedEdits.Add(new ResolvedScriptTextEdit(startOffset, endOffset, edit.NewText));
            }

            resolvedEdits.Sort((left, right) => left.StartOffset.CompareTo(right.StartOffset));
            for (var index = 1; index < resolvedEdits.Count; index++)
            {
                var previous = resolvedEdits[index - 1];
                var current = resolvedEdits[index];
                if (current.StartOffset < previous.EndOffset || current.StartOffset == previous.StartOffset)
                {
                    error = CommandExecutionResult.Error("invalid_field_value", "edits must not overlap or share the same start location");
                    return false;
                }
            }

            return true;
        }

        static string ApplyResolvedScriptTextEdits(string content, List<ResolvedScriptTextEdit> resolvedEdits)
        {
            var builder = new StringBuilder(content);
            for (var index = resolvedEdits.Count - 1; index >= 0; index--)
            {
                var edit = resolvedEdits[index];
                builder.Remove(edit.StartOffset, edit.EndOffset - edit.StartOffset);
                builder.Insert(edit.StartOffset, edit.NewText);
            }

            return builder.ToString();
        }

        static bool TryLineColumnToOffset(string content, int targetLine, int targetColumn, out int offset, out string error)
        {
            offset = 0;
            error = string.Empty;
            var line = 1;
            var lineStart = 0;

            for (var index = 0; index <= content.Length; index++)
            {
                if (line == targetLine)
                {
                    var lineEnd = content.IndexOf('\n', lineStart);
                    if (lineEnd < 0)
                    {
                        lineEnd = content.Length;
                    }

                    var visibleLineEnd = lineEnd;
                    if (visibleLineEnd > lineStart && content[visibleLineEnd - 1] == '\r')
                    {
                        visibleLineEnd--;
                    }

                    var maxColumn = visibleLineEnd - lineStart + 1;
                    if (targetColumn > maxColumn)
                    {
                        error = "Edit column is outside the target line";
                        return false;
                    }

                    offset = lineStart + targetColumn - 1;
                    return true;
                }

                if (index == content.Length)
                {
                    break;
                }

                if (content[index] == '\n')
                {
                    line++;
                    lineStart = index + 1;
                }
            }

            error = "Edit line is outside the script";
            return false;
        }

        static string ExtractArray(string json, string property)
        {
            var colon = FindPropertyColon(json, property);
            if (colon < 0)
            {
                return string.Empty;
            }

            var start = IndexOfNextNonWhitespace(json, colon + 1);
            if (start < 0 || json[start] != '[')
            {
                return string.Empty;
            }

            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var index = start; index < json.Length; index++)
            {
                var character = json[index];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (character == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (character == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (inString)
                {
                    continue;
                }

                if (character == '[')
                {
                    depth++;
                }
                else if (character == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return json.Substring(start, index - start + 1);
                    }
                }
            }

            return string.Empty;
        }

        static bool TryExtractObjectArrayItems(string arrayJson, List<string> items)
        {
            var start = IndexOfNextNonWhitespace(arrayJson, 0);
            if (start < 0 || arrayJson[start] != '[')
            {
                return false;
            }

            var depth = 0;
            var objectStart = -1;
            var inString = false;
            var escaped = false;
            for (var index = start + 1; index < arrayJson.Length; index++)
            {
                var character = arrayJson[index];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (character == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (character == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (inString)
                {
                    continue;
                }

                if (character == '{')
                {
                    if (depth == 0)
                    {
                        objectStart = index;
                    }

                    depth++;
                }
                else if (character == '}')
                {
                    depth--;
                    if (depth < 0)
                    {
                        return false;
                    }

                    if (depth == 0 && objectStart >= 0)
                    {
                        items.Add(arrayJson.Substring(objectStart, index - objectStart + 1));
                        objectStart = -1;
                    }
                }
                else if (character == ']' && depth == 0)
                {
                    return true;
                }
            }

            return false;
        }

        static bool ContainsJsonProperty(string json, string property)
        {
            return FindPropertyColon(json, property) >= 0;
        }

        static int FindPropertyColon(string json, string property)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(property))
            {
                return -1;
            }

            var pattern = "\"" + property + "\"";
            var propertyIndex = json.IndexOf(pattern, StringComparison.Ordinal);
            if (propertyIndex < 0)
            {
                return -1;
            }

            return json.IndexOf(':', propertyIndex + pattern.Length);
        }

        static int IndexOfNextNonWhitespace(string value, int start)
        {
            for (var index = start; index < value.Length; index++)
            {
                if (!char.IsWhiteSpace(value[index]))
                {
                    return index;
                }
            }

            return -1;
        }

        static bool IsSha256Hex(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64)
            {
                return false;
            }

            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                var isHex = (character >= '0' && character <= '9')
                    || (character >= 'a' && character <= 'f')
                    || (character >= 'A' && character <= 'F');
                if (!isHex)
                {
                    return false;
                }
            }

            return true;
        }

        static bool IsUnityGuid(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 32)
            {
                return false;
            }

            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                var isHex = (character >= '0' && character <= '9')
                    || (character >= 'a' && character <= 'f')
                    || (character >= 'A' && character <= 'F');
                if (!isHex)
                {
                    return false;
                }
            }

            return true;
        }

        static string Sha256File(string fullPath)
        {
            using (var stream = File.OpenRead(fullPath))
            using (var hash = SHA256.Create())
            {
                return Hex(hash.ComputeHash(stream));
            }
        }

        static string Sha256Utf8(string content)
        {
            using (var hash = SHA256.Create())
            {
                return Hex(hash.ComputeHash(new UTF8Encoding(false).GetBytes(content)));
            }
        }

        static bool AssetExists(string assetPath)
        {
            return AssetDatabase.IsValidFolder(assetPath) || AssetDatabase.LoadMainAssetAtPath(assetPath) != null;
        }

        static string ParentAssetPath(string assetPath)
        {
            var separator = assetPath.LastIndexOf('/');
            return separator <= 0 ? string.Empty : assetPath.Substring(0, separator);
        }

        static int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }

        static string Hex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            for (var i = 0; i < bytes.Length; i++)
            {
                builder.Append(bytes[i].ToString("x2"));
            }

            return builder.ToString();
        }

        static string ResultJson(string folderPath, bool created, string guid, EditorEditModeEligibility editorState)
        {
            var json = "{\"folder_path\":" + JsonUtil.String(folderPath)
                + ",\"created\":" + JsonUtil.Bool(created);
            if (!string.IsNullOrEmpty(guid))
            {
                json += ",\"guid\":" + JsonUtil.String(guid);
            }

            json += ",\"changed_assets\":" + (created ? "[" + JsonUtil.String(folderPath) + "]" : "[]")
                + ",\"created_assets\":" + (created ? "[{" + AssetRecordFieldsJson(folderPath, guid) + "}]" : "[]")
                + ",\"editor_state\":" + editorState.NormalizedStateJson()
                + ",\"warnings\":[]"
                + ",\"next_valid_actions\":[\"validation.after_edit\",\"asset.index.rebuild\"]";

            return json + "}";
        }

        static string AssetRecordFieldsJson(string assetPath, string guid)
        {
            return "\"asset_path\":" + JsonUtil.String(assetPath)
                + ",\"guid\":" + JsonUtil.String(guid);
        }

        static string MoveRecordFieldsJson(string sourcePath, string destinationPath, string guidBefore, string guidAfter)
        {
            return "\"source_path\":" + JsonUtil.String(sourcePath)
                + ",\"destination_path\":" + JsonUtil.String(destinationPath)
                + ",\"guid_before\":" + JsonUtil.String(guidBefore)
                + ",\"guid_after\":" + JsonUtil.String(guidAfter);
        }

        static string ScriptValidationResultJson(string scriptPath, string level, long byteLength, int maxBytes, bool includeDiagnostics, ScriptValidationDiagnostics diagnostics)
        {
            var json = "{\"script_path\":" + JsonUtil.String(scriptPath)
                + ",\"guid\":" + JsonUtil.String(AssetDatabase.AssetPathToGUID(scriptPath))
                + ",\"level\":" + JsonUtil.String(level)
                + ",\"valid\":" + JsonUtil.Bool(diagnostics.Errors == 0)
                + ",\"warnings\":" + diagnostics.Warnings
                + ",\"errors\":" + diagnostics.Errors
                + ",\"byte_length\":" + byteLength
                + ",\"max_bytes\":" + maxBytes
                + ",\"include_diagnostics\":" + JsonUtil.Bool(includeDiagnostics)
                + ",\"diagnostics_truncated\":" + JsonUtil.Bool(diagnostics.Truncated);

            if (includeDiagnostics)
            {
                json += ",\"diagnostics\":[";
                for (var index = 0; index < diagnostics.Items.Count; index++)
                {
                    if (index > 0)
                    {
                        json += ",";
                    }

                    var diagnostic = diagnostics.Items[index];
                    json += "{\"severity\":" + JsonUtil.String(diagnostic.Severity)
                        + ",\"message\":" + JsonUtil.String(diagnostic.Message)
                        + ",\"line\":" + diagnostic.Line
                        + ",\"column\":" + diagnostic.Column + "}";
                }

                json += "]";
            }

            return json + "}";
        }

        sealed class ScriptValidationDiagnostics
        {
            public readonly List<ScriptValidationDiagnostic> Items = new List<ScriptValidationDiagnostic>();
            public int Warnings;
            public int Errors;
            public bool Truncated;
        }

        sealed class ScriptValidationDiagnostic
        {
            public readonly string Severity;
            public readonly string Message;
            public readonly int Line;
            public readonly int Column;

            public ScriptValidationDiagnostic(string severity, string message, int line, int column)
            {
                Severity = severity;
                Message = message;
                Line = line;
                Column = column;
            }
        }

        sealed class ScriptTextEdit
        {
            public readonly int StartLine;
            public readonly int StartColumn;
            public readonly int EndLine;
            public readonly int EndColumn;
            public readonly string NewText;

            public ScriptTextEdit(int startLine, int startColumn, int endLine, int endColumn, string newText)
            {
                StartLine = startLine;
                StartColumn = startColumn;
                EndLine = endLine;
                EndColumn = endColumn;
                NewText = newText;
            }
        }

        sealed class ResolvedScriptTextEdit
        {
            public readonly int StartOffset;
            public readonly int EndOffset;
            public readonly string NewText;

            public ResolvedScriptTextEdit(int startOffset, int endOffset, string newText)
            {
                StartOffset = startOffset;
                EndOffset = endOffset;
                NewText = newText;
            }
        }

        struct BracketFrame
        {
            public readonly char Character;
            public readonly int Line;
            public readonly int Column;

            public BracketFrame(char character, int line, int column)
            {
                Character = character;
                Line = line;
                Column = column;
            }
        }
    }
}
