using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using SGG.UnityMCP.Editor.Setup;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Commands
{
    internal static class DynamicCSharpDiagnosticClassifier
    {
        const string WrapperSyntaxCategory = "wrapper_syntax_error";
        const string CallerCompileCategory = "caller_compile_error";
        const string UnsupportedApiCategory = "unsupported_unity_api_shape";
        const string ObjectReturnCategory = "object_return_contract_violation";

        public static bool HasLeadingUsingDirective(string code)
        {
            var index = 0;
            SkipTrivia(code, ref index);

            if (ReadKeyword(code, ref index, "global"))
            {
                if (!ReadKeyword(code, ref index, "using"))
                {
                    return false;
                }
            }
            else if (!ReadKeyword(code, ref index, "using"))
            {
                return false;
            }

            SkipTrivia(code, ref index);
            if (index >= (code ?? string.Empty).Length || code[index] == '(')
            {
                return false;
            }

            var sawIdentifier = false;
            var needsIdentifier = false;
            if (ReadKeyword(code, ref index, "var"))
            {
                SkipTrivia(code, ref index);
                if (index >= code.Length || code[index] == '=' || code[index] == ';')
                {
                    return true;
                }

                if (code[index] == '.')
                {
                    index++;
                    sawIdentifier = true;
                    needsIdentifier = true;
                }
                else if (code[index] == ':' && index + 1 < code.Length && code[index + 1] == ':')
                {
                    index += 2;
                    sawIdentifier = true;
                    needsIdentifier = true;
                }
                else if (code[index] == '<')
                {
                    sawIdentifier = true;
                }
                else
                {
                    return false;
                }
            }
            else if (ReadKeyword(code, ref index, "static"))
            {
                SkipTrivia(code, ref index);
            }

            while (index < (code ?? string.Empty).Length)
            {
                SkipTrivia(code, ref index);
                if (index >= code.Length)
                {
                    return false;
                }

                if (code[index] == ';' || code[index] == '=')
                {
                    return sawIdentifier && !needsIdentifier;
                }

                if (IsIdentifierStart(code, index))
                {
                    if (sawIdentifier && !needsIdentifier)
                    {
                        return false;
                    }

                    ReadIdentifier(code, ref index);
                    sawIdentifier = true;
                    needsIdentifier = false;
                    continue;
                }

                if (code[index] == '.')
                {
                    if (!sawIdentifier || needsIdentifier)
                    {
                        return false;
                    }

                    index++;
                    needsIdentifier = true;
                    continue;
                }

                if (code[index] == ':' && index + 1 < code.Length && code[index + 1] == ':')
                {
                    if (!sawIdentifier || needsIdentifier)
                    {
                        return false;
                    }

                    index += 2;
                    needsIdentifier = true;
                    continue;
                }

                if (code[index] == '<' && sawIdentifier && !needsIdentifier)
                {
                    if (!SkipGenericArgumentList(code, ref index))
                    {
                        return false;
                    }

                    continue;
                }

                return false;
            }

            return false;
        }

        static bool SkipGenericArgumentList(string code, ref int index)
        {
            var depth = 0;
            while (index < (code ?? string.Empty).Length)
            {
                SkipTrivia(code, ref index);
                if (index >= code.Length)
                {
                    return false;
                }

                if (code[index] == '<')
                {
                    depth++;
                }
                else if (code[index] == '>' && --depth == 0)
                {
                    index++;
                    return true;
                }

                index++;
            }

            return false;
        }

        public static string Classify(IList<string> messages, IList<string> diagnosticIds, bool wrapperSourceShape)
        {
            if (wrapperSourceShape || HasSyntaxDiagnostic(messages, diagnosticIds))
            {
                return WrapperSyntaxCategory;
            }

            if (AnyDiagnosticId(diagnosticIds, "CS0126") || AnyMessageContains(messages, "CS0126") || AnyMessageContains(messages, "Cannot implicitly convert type 'void' to 'object'") || AnyMessageContains(messages, "convertible to 'object' is required"))
            {
                return ObjectReturnCategory;
            }

            if (HasProvenUnityApiDiagnostic(messages, diagnosticIds))
            {
                return UnsupportedApiCategory;
            }

            return CallerCompileCategory;
        }

        public static string NormalizeDiagnosticId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= 2 || value.Length > 16 || !value.StartsWith("CS", StringComparison.Ordinal))
            {
                return "compiler_error";
            }

            for (var index = 2; index < value.Length; index++)
            {
                if (!char.IsDigit(value[index]))
                {
                    return "compiler_error";
                }
            }

            return value;
        }

        static bool HasSyntaxDiagnostic(IList<string> messages, IList<string> diagnosticIds)
        {
            if (AnyDiagnosticId(diagnosticIds, "CS1001") || AnyDiagnosticId(diagnosticIds, "CS1002") || AnyDiagnosticId(diagnosticIds, "CS1022")
                || AnyDiagnosticId(diagnosticIds, "CS1513") || AnyDiagnosticId(diagnosticIds, "CS1519") || AnyDiagnosticId(diagnosticIds, "CS1525") || AnyDiagnosticId(diagnosticIds, "CS0116"))
            {
                return true;
            }

            return AnyMessageContains(messages, "Identifier expected")
                || AnyMessageContains(messages, "Invalid token")
                || AnyMessageContains(messages, "A namespace cannot directly contain");
        }

        static bool HasProvenUnityApiDiagnostic(IList<string> messages, IList<string> diagnosticIds)
        {
            if (messages == null || diagnosticIds == null)
            {
                return false;
            }

            var count = Math.Min(messages.Count, diagnosticIds.Count);
            for (var index = 0; index < count; index++)
            {
                var id = diagnosticIds[index];
                if ((id == "CS0117" || id == "CS0234" || id == "CS0246" || id == "CS1061" || id == "CS1501")
                    && ContainsQualifiedUnityApiToken(messages[index]))
                {
                    return true;
                }
            }

            return false;
        }

        static bool ContainsQualifiedUnityApiToken(string message)
        {
            return ContainsQualifiedToken(message, "UnityEditor.") || ContainsQualifiedToken(message, "UnityEngine.");
        }

        static bool ContainsQualifiedToken(string message, string token)
        {
            if (string.IsNullOrEmpty(message))
            {
                return false;
            }

            var offset = 0;
            while (offset < message.Length)
            {
                var index = message.IndexOf(token, offset, StringComparison.Ordinal);
                if (index < 0)
                {
                    return false;
                }

                if (index == 0 || !IsIdentifierPart(message[index - 1]))
                {
                    return true;
                }

                offset = index + token.Length;
            }

            return false;
        }

        static bool AnyDiagnosticId(IList<string> diagnosticIds, string expected)
        {
            if (diagnosticIds == null)
            {
                return false;
            }

            for (var index = 0; index < diagnosticIds.Count; index++)
            {
                if (string.Equals(diagnosticIds[index], expected, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        static bool AnyMessageContains(IList<string> messages, string expected)
        {
            if (messages == null)
            {
                return false;
            }

            for (var index = 0; index < messages.Count; index++)
            {
                if ((messages[index] ?? string.Empty).IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        static bool ReadKeyword(string code, ref int index, string keyword)
        {
            SkipTrivia(code, ref index);
            if (string.IsNullOrEmpty(code) || index + keyword.Length > code.Length || string.CompareOrdinal(code, index, keyword, 0, keyword.Length) != 0)
            {
                return false;
            }

            var end = index + keyword.Length;
            if ((index > 0 && IsIdentifierPart(code[index - 1]))
                || (end < code.Length && (IsIdentifierPart(code[end]) || UnicodeEscapeLength(code, end) > 0)))
            {
                return false;
            }

            index = end;
            return true;
        }

        static void ReadIdentifier(string code, ref int index)
        {
            if (index < code.Length && code[index] == '@')
            {
                index++;
            }

            while (index < code.Length)
            {
                if (IsIdentifierPart(code[index]))
                {
                    index++;
                    continue;
                }

                var escapeLength = UnicodeEscapeLength(code, index);
                if (escapeLength == 0)
                {
                    break;
                }

                index += escapeLength;
            }
        }

        static bool IsIdentifierStart(string code, int index)
        {
            if (string.IsNullOrEmpty(code) || index < 0 || index >= code.Length)
            {
                return false;
            }

            return code[index] == '@' || code[index] == '_' || char.IsLetter(code[index]) || UnicodeEscapeLength(code, index) > 0;
        }

        static bool IsIdentifierPart(char value)
        {
            var category = char.GetUnicodeCategory(value);
            return value == '_' || char.IsLetterOrDigit(value)
                || category == UnicodeCategory.NonSpacingMark
                || category == UnicodeCategory.SpacingCombiningMark
                || category == UnicodeCategory.ConnectorPunctuation
                || category == UnicodeCategory.Format;
        }

        static int UnicodeEscapeLength(string code, int index)
        {
            if (string.IsNullOrEmpty(code) || index < 0 || index + 2 >= code.Length || code[index] != '\\')
            {
                return 0;
            }

            var digits = code[index + 1] == 'u' ? 4 : code[index + 1] == 'U' ? 8 : 0;
            if (digits == 0 || index + 2 + digits > code.Length)
            {
                return 0;
            }

            for (var offset = 0; offset < digits; offset++)
            {
                if (!Uri.IsHexDigit(code[index + 2 + offset]))
                {
                    return 0;
                }
            }

            return digits + 2;
        }

        static void SkipTrivia(string code, ref int index)
        {
            if (code == null)
            {
                return;
            }

            while (index < code.Length)
            {
                if (char.IsWhiteSpace(code[index]) || code[index] == '\uFEFF')
                {
                    index++;
                    continue;
                }

                if (code[index] == '/' && index + 1 < code.Length && code[index + 1] == '/')
                {
                    index += 2;
                    while (index < code.Length && code[index] != '\r' && code[index] != '\n')
                    {
                        index++;
                    }

                    continue;
                }

                if (code[index] == '/' && index + 1 < code.Length && code[index + 1] == '*')
                {
                    var end = code.IndexOf("*/", index + 2, StringComparison.Ordinal);
                    index = end < 0 ? code.Length : end + 2;
                    continue;
                }

                return;
            }
        }
    }

    public static class EditorCSharpExecutionCommands
    {
        const string FeatureFlag = "editor_csharp_execution";
        const string EmptySchemaJson = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
        const string ExecuteSchemaJson = "{\"type\":\"object\",\"properties\":{\"code\":{\"type\":\"string\",\"description\":\"C# snippet inserted as the body of public static object Execute(). Leading namespace using directives are not supported; use existing imports or fully-qualified names. Use object-compatible returns: return null;, return value;, or return Mcp.Result(value);. Prefer regular catalog commands when available.\",\"minLength\":1,\"maxLength\":50000},\"max_output_chars\":{\"type\":\"integer\",\"description\":\"Maximum UTF-16 code units retained across captured console output entries; default 4000. Does not control returned-value serialization or whole-response size.\",\"minimum\":1,\"maximum\":20000},\"response_mode\":{\"type\":\"string\",\"description\":\"Both modes retain independent result-serialization limits; full does not mean unlimited scalar text.\",\"enum\":[\"full\",\"compact\"],\"default\":\"full\"}},\"required\":[\"code\"],\"additionalProperties\":false}";
        const string ReadonlyStatusSchemaJson = "{\"type\":\"object\",\"properties\":{\"operation_id\":{\"type\":\"string\",\"pattern\":\"^csharp-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$\"}},\"required\":[\"operation_id\"],\"additionalProperties\":false}";
        const string OutputLimitJson = "{\"max_code_chars\":50000,\"max_output_chars\":20000,\"max_output_entries\":256,\"max_result_depth\":4,\"max_result_items\":100,\"max_result_utf8_bytes\":65536,\"max_string_json_content_bytes\":256,\"max_visited_values\":128,\"max_enumerable_scan_items\":4096,\"max_name_chars\":128,\"max_name_json_content_bytes\":128}";
        const int MaxCodeChars = 50000;
        const int DefaultOutputChars = 4000;
        const int MaxOutputChars = 20000;
        const int MaxOutputEntries = 256;
        const int MaxDiagnosticMessages = 5;
        const int MaxDiagnosticMessageChars = 500;
        const int MaxJoinedDiagnosticChars = 3000;
        const int MaxAssemblyReferenceCount = 4096;
        const int MaxAssemblyReferencePathChars = 2048;
        const string WrapperTypeName = "SGGUnityMcpDynamicCode";
        const string WrapperMethodName = "Execute";

        static readonly object s_outputLock = new object();
        static readonly List<string> s_output = new List<string>();
        static int s_outputCharCount;
        static int s_outputCharLimit = DefaultOutputChars;
        static bool s_outputTruncated;
        static string[] s_assemblyPaths;

        static readonly string[] AlwaysBlockedPatterns =
        {
            "System.Diagnostics.Process",
            "Process.Start",
            "Process.Kill",
            "System.Net.",
            "UnityWebRequest",
            "HttpClient",
            "WebRequest",
            "Socket",
            "TcpClient",
            "File.Delete",
            "Directory.Delete",
            "FileUtil.DeleteFileOrDirectory",
            "AssetDatabase.DeleteAsset",
            "AssetDatabase.MoveAssetToTrash",
            "EditorApplication.Exit",
            "while(true)",
            "while (true)",
            "for(;;)",
            "for (;;)",
        };

        static readonly string[] ReadOnlyBlockedPatterns =
        {
            "AssetDatabase.CreateAsset",
            "AssetDatabase.AddObjectToAsset",
            "AssetDatabase.ImportAsset",
            "AssetDatabase.Refresh",
            "AssetDatabase.SaveAssets",
            "AssetDatabase.MoveAsset",
            "AssetDatabase.RenameAsset",
            "EditorSceneManager.OpenScene",
            "EditorSceneManager.Save",
            "PrefabUtility.Save",
            "Undo.",
            "EditorUtility.SetDirty",
            "Object.Destroy",
            "DestroyImmediate",
            "new GameObject",
            ".AddComponent<",
            ".SetActive(",
            ".parent =",
            ".position =",
            ".rotation =",
            ".localScale =",
        };

        [InitializeOnLoadMethod]
        static void OnDomainReload()
        {
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            ResetCompilerCache();
        }

        static void OnCompilationFinished(object context)
        {
            s_assemblyPaths = null;
        }

        public static void ResetCompilerCache()
        {
            s_assemblyPaths = null;
            ReflectionRoslynCompiler.Reset();
        }

        internal static string CreateDynamicAssemblyName()
        {
            return "SGGUnityMcpDynamic_" + Guid.NewGuid().ToString("N");
        }

        [McpCommand(
            "editor.roslyn.status",
            Title = "Roslyn status",
            Description = "Read dynamic C# compiler availability",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = EmptySchemaJson,
            Tags = new string[] { "editor", "roslyn" },
            Capability = "editor_csharp_execution",
            ResultShape = "status",
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult RoslynStatus(McpCommandContext context)
        {
            return CommandExecutionResult.Ok(RoslynStatusJson(false, string.Empty));
        }

        [McpCommand(
            "editor.roslyn.install",
            Title = "Install Roslyn",
            Description = "Download Roslyn compiler DLLs into Assets/Plugins for dynamic C# execution",
            Risk = "network",
            Idempotency = "idempotent",
            TimeoutMs = 120000,
            SchemaJson = EmptySchemaJson,
            Tags = new string[] { "editor", "roslyn", "network" },
            Capability = "editor_csharp_execution",
            ResultShape = "status",
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult RoslynInstall(McpCommandContext context)
        {
            var result = EditorRoslynInstaller.Install(false);
            if (!result.Success)
            {
                return CommandExecutionResult.Error("unity_exception", result.Message);
            }

            return CommandExecutionResult.Ok(RoslynStatusJson(true, result.Message));
        }

        [McpCommand(
            "editor.execute_csharp.readonly",
            Title = "Execute read-only C#",
            Description = "Compile and execute bounded read-only C# through Roslyn; max_output_chars limits console text only. Full and compact retain independent result limits, including 256 escaped JSON-content UTF-8 bytes per string (excluding quotes).",
            Risk = "execute",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = ExecuteSchemaJson,
            Tags = new string[] { "editor", "csharp", "readonly", DynamicCSharpReadonlyResultStore.OperationTag },
            Capability = "editor_csharp_execution",
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "object_details",
            OutputLimitJson = OutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ExecuteReadonly(McpCommandContext context)
        {
            return Execute(context, true);
        }

        [McpCommand(
            "editor.execute_csharp.readonly.status",
            Title = "Read dynamic C# result",
            Description = "Read one exact stored bounded editor.execute_csharp.readonly result without rerunning the snippet; this retrieves retained projected data, not omitted string tails.",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = ReadonlyStatusSchemaJson,
            Tags = new string[] { "editor", "csharp", "readonly", "status" },
            Capability = "editor_csharp_execution",
            ResultShape = "operation_status",
            OutputLimitJson = OutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ExecuteReadonlyStatus(McpCommandContext context)
        {
            return DynamicCSharpReadonlyResultStore.Status(context);
        }

        [McpCommand(
            "editor.execute_csharp",
            Title = "Execute C#",
            Description = "Compile and execute bounded C# through Roslyn; max_output_chars limits console text only. Full and compact retain independent result limits, including 256 escaped JSON-content UTF-8 bytes per string (excluding quotes).",
            Risk = "execute",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = ExecuteSchemaJson,
            Tags = new string[] { "editor", "csharp" },
            Capability = "editor_csharp_execution",
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "object_details",
            OutputLimitJson = OutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ExecuteCSharp(McpCommandContext context)
        {
            return Execute(context, false);
        }

        [McpCommand(
            "editor.recompile",
            Title = "Request script compilation",
            Description = "Request Unity script recompilation",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 30000,
            SchemaJson = EmptySchemaJson,
            Tags = new string[] { "editor", "compile" },
            Capability = "editor_csharp_execution",
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "status",
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult Recompile(McpCommandContext context)
        {
            if (EditorApplication.isCompiling)
            {
                return CommandExecutionResult.Error("unity_compiling", "Unity is already compiling");
            }

            AssetDatabase.Refresh();
            UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
            return CommandExecutionResult.Ok("{\"requested\":true}");
        }

        public static void Print(object value)
        {
            AppendOutput(value == null ? "null" : value.ToString());
        }

        public static void PrintJson(string json)
        {
            AppendOutput(json ?? string.Empty);
        }

        static CommandExecutionResult Execute(McpCommandContext context, bool readOnly)
        {
            var code = JsonUtil.ExtractString(context.ArgsJson, "code");
            if (string.IsNullOrEmpty(code))
            {
                return CommandExecutionResult.ValidationError("schema_validation_failed", "Argument code is required", "code", JsonUtil.HasProperty(context.ArgsJson, "code") ? "string_too_short" : "missing_required_field");
            }

            if (code.Length > MaxCodeChars)
            {
                return CommandExecutionResult.ValidationError("schema_validation_failed", "Argument code exceeds " + MaxCodeChars + " characters", "code", "string_too_long");
            }

            DynamicCSharpReadonlyOperation operation = null;
            if (readOnly && !DynamicCSharpReadonlyResultStore.TryBegin(context, code, out operation, out var bindingError))
            {
                return bindingError;
            }

            var statusResult = RequireEditingStatus();
            if (statusResult != null)
            {
                return operation == null ? statusResult : operation.PublishFailedPhase("reference_resolution", statusResult);
            }

            var blockedPattern = FindBlockedPattern(code, readOnly);
            if (!string.IsNullOrEmpty(blockedPattern))
            {
                var blocked = CommandExecutionResult.Error("dangerous_code_rejected", "Dynamic C# contains blocked pattern: " + blockedPattern);
                return operation == null ? blocked : operation.PublishFailedPhase("reference_resolution", blocked);
            }

            if (DynamicCSharpDiagnosticClassifier.HasLeadingUsingDirective(code))
            {
                var invalidSource = CommandExecutionResult.Error(
                    "compilation_failed",
                    "Dynamic C# code must be the body of Execute(); leading namespace using directives are not supported.",
                    CompilationFailedDetailsJson(new List<string>(), new List<string>(), "leading_using_directive"));
                return operation == null ? invalidSource : operation.PublishFailedPhase("reference_resolution", invalidSource);
            }

            if (!ReflectionRoslynCompiler.IsAvailable)
            {
                var unavailable = CommandExecutionResult.Error("dependency_missing", "Roslyn is not loaded. Use SGG/Unity MCP/Install Roslyn Compiler or editor.roslyn.install, then allow Unity to reload assemblies.");
                return operation == null ? unavailable : operation.PublishFailedPhase("reference_resolution", unavailable);
            }

            ResetOutput(Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_output_chars", DefaultOutputChars), 1, MaxOutputChars));

            var source = WrapUserCode(code);
            if (operation != null && !operation.TryStartPhase("reference_resolution", out var phaseError))
            {
                return phaseError;
            }
            var phaseTimer = System.Diagnostics.Stopwatch.StartNew();
            var assemblyPaths = GetAssemblyPaths();
            phaseTimer.Stop();
            if (operation != null && !operation.TryFinishPhase("reference_resolution", phaseTimer.ElapsedMilliseconds, true, out phaseError))
            {
                return phaseError;
            }

            if (operation != null && !operation.TryStartPhase("compilation", out phaseError))
            {
                return phaseError;
            }
            phaseTimer.Restart();
            var assembly = ReflectionRoslynCompiler.Compile(source, assemblyPaths, out var compileErrors, out var diagnosticIds);
            phaseTimer.Stop();
            if (assembly == null)
            {
                var compileFailure = CommandExecutionResult.Error("compilation_failed", JoinMessages(compileErrors), CompilationFailedDetailsJson(compileErrors, diagnosticIds, string.Empty));
                if (operation != null && !operation.TryFinishPhase("compilation", phaseTimer.ElapsedMilliseconds, false, out phaseError))
                {
                    return phaseError;
                }
                return operation == null ? compileFailure : operation.PublishTerminal(compileFailure);
            }
            if (operation != null && !operation.TryFinishPhase("compilation", phaseTimer.ElapsedMilliseconds, true, out phaseError))
            {
                return phaseError;
            }

            return InvokeCompiled(assembly, readOnly, NormalizeResponseMode(JsonUtil.ExtractString(context.ArgsJson, "response_mode")), operation);
        }

        static CommandExecutionResult RequireEditingStatus()
        {
            if (EditorApplication.isCompiling)
            {
                return CommandExecutionResult.Error("unity_compiling", "Unity is compiling");
            }

            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return CommandExecutionResult.Error("unity_busy", "Dynamic editor C# execution requires editing status");
            }

            return null;
        }

        static CommandExecutionResult InvokeCompiled(System.Reflection.Assembly assembly, bool readOnly, string responseMode, DynamicCSharpReadonlyOperation operation = null)
        {
            var type = assembly.GetType(WrapperTypeName);
            if (type == null)
            {
                var failure = CommandExecutionResult.Error("unity_exception", "Compiled dynamic type was not found");
                return operation == null ? failure : operation.PublishFailedPhase("main_thread_execution", failure);
            }

            var method = type.GetMethod(WrapperMethodName, BindingFlags.Public | BindingFlags.Static);
            if (method == null)
            {
                var failure = CommandExecutionResult.Error("unity_exception", "Compiled Execute method was not found");
                return operation == null ? failure : operation.PublishFailedPhase("main_thread_execution", failure);
            }

            return InvokeCompiledMethod(method, readOnly, responseMode, operation);
        }

        internal static CommandExecutionResult InvokeCompiledMethod(MethodInfo method, bool readOnly, string responseMode)
        {
            return InvokeCompiledMethod(method, readOnly, responseMode, null);
        }

        static CommandExecutionResult InvokeCompiledMethod(MethodInfo method, bool readOnly, string responseMode, DynamicCSharpReadonlyOperation operation)
        {
            var activePhase = string.Empty;
            var phaseTimer = new System.Diagnostics.Stopwatch();
            try
            {
                if (operation != null && !operation.TryStartPhase("main_thread_execution", out var phaseError))
                {
                    return phaseError;
                }
                activePhase = "main_thread_execution";
                phaseTimer.Restart();
                var result = method.Invoke(null, null);
                phaseTimer.Stop();
                if (operation != null && !operation.TryFinishPhase("main_thread_execution", phaseTimer.ElapsedMilliseconds, true, out phaseError))
                {
                    return phaseError;
                }
                activePhase = string.Empty;
                if (operation != null && !operation.TryStartPhase("result_serialization", out phaseError))
                {
                    return phaseError;
                }
                activePhase = "result_serialization";
                phaseTimer.Restart();
                var serialized = DynamicCSharpResultSerializer.Serialize(result);
                if (!serialized.Success)
                {
                    phaseTimer.Stop();
                    var serializationFailure = CommandExecutionResult.Error(serialized.ErrorCode, serialized.ErrorMessage, serialized.ErrorDetailsJson);
                    if (operation != null && !operation.TryFinishPhase("result_serialization", phaseTimer.ElapsedMilliseconds, false, out phaseError))
                    {
                        return phaseError;
                    }
                    activePhase = string.Empty;
                    return operation == null ? serializationFailure : operation.PublishTerminal(serializationFailure);
                }

                var output = OutputJson(out var consoleOutputTruncated);
                var anyTruncated = consoleOutputTruncated || serialized.Truncated;
                var response = CommandExecutionResult.Ok("{\"compiler\":\"roslyn\""
                    + ",\"response_mode\":" + JsonUtil.String(NormalizeResponseMode(responseMode))
                    + ",\"read_only\":" + JsonUtil.Bool(readOnly)
                    + ",\"result\":" + serialized.Json
                    + ",\"result_summary\":" + serialized.SummaryJson()
                    + ",\"result_truncated\":" + JsonUtil.Bool(serialized.Truncated)
                    + ",\"output\":[" + output + "]"
                    + ",\"console_output_truncated\":" + JsonUtil.Bool(consoleOutputTruncated)
                    + ",\"output_truncated\":" + JsonUtil.Bool(anyTruncated)
                    + ",\"truncated\":" + JsonUtil.Bool(anyTruncated)
                    + "}");
                phaseTimer.Stop();
                if (operation != null && !operation.TryFinishPhase("result_serialization", phaseTimer.ElapsedMilliseconds, true, out phaseError))
                {
                    return phaseError;
                }
                activePhase = string.Empty;
                return operation == null ? response : operation.PublishTerminal(response);
            }
            catch (TargetInvocationException exception)
            {
                var inner = exception.InnerException ?? exception;
                var failure = CommandExecutionResult.Error("unity_exception", inner.GetType().Name + ": " + inner.Message, RuntimeExceptionDetailsJson(inner));
                if (operation != null)
                {
                    phaseTimer.Stop();
                    if (!string.IsNullOrEmpty(activePhase) && !operation.TryFinishPhase(activePhase, phaseTimer.ElapsedMilliseconds, false, out var phaseError)) return phaseError;
                    return operation.PublishTerminal(failure);
                }
                return failure;
            }
            catch (Exception exception)
            {
                var failure = CommandExecutionResult.Error("unity_exception", exception.GetType().Name + ": " + exception.Message, RuntimeExceptionDetailsJson(exception));
                if (operation != null)
                {
                    phaseTimer.Stop();
                    if (!string.IsNullOrEmpty(activePhase) && !operation.TryFinishPhase(activePhase, phaseTimer.ElapsedMilliseconds, false, out var phaseError)) return phaseError;
                    return operation.PublishTerminal(failure);
                }
                return failure;
            }
        }

        static string NormalizeResponseMode(string value)
        {
            return string.Equals(value, "compact", StringComparison.Ordinal) ? "compact" : "full";
        }

        static string RoslynStatusJson(bool changed, string message)
        {
            return "{\"installed\":" + JsonUtil.Bool(EditorRoslynInstaller.IsInstalled())
                + ",\"loaded\":" + JsonUtil.Bool(EditorRoslynInstaller.IsCompilerLoaded())
                + ",\"available\":" + JsonUtil.Bool(ReflectionRoslynCompiler.IsAvailable)
                + ",\"install_path\":" + JsonUtil.String(EditorRoslynInstaller.ProjectRelativeFolder)
                + ",\"changed\":" + JsonUtil.Bool(changed)
                + ",\"message\":" + JsonUtil.String(message) + "}";
        }

        static string WrapUserCode(string code)
        {
            var builder = new StringBuilder();
            builder.AppendLine("using System;");
            builder.AppendLine("using System.Collections.Generic;");
            builder.AppendLine("using System.Linq;");
            builder.AppendLine("using UnityEditor;");
            builder.AppendLine("using UnityEngine;");
            builder.AppendLine("using SGG.UnityMCP.Editor.Commands;");
            builder.AppendLine("public static class Mcp");
            builder.AppendLine("{");
            builder.AppendLine("    public static void Print(object value) { EditorCSharpExecutionCommands.Print(value); }");
            builder.AppendLine("    public static void PrintJson(string json) { EditorCSharpExecutionCommands.PrintJson(json); }");
            builder.AppendLine("    public static object Result(object value) { return value; }");
            builder.AppendLine("}");
            builder.AppendLine("public static class " + WrapperTypeName);
            builder.AppendLine("{");
            builder.AppendLine("    public static object " + WrapperMethodName + "()");
            builder.AppendLine("    {");
            builder.AppendLine(code);
            builder.AppendLine("        return null;");
            builder.AppendLine("    }");
            builder.AppendLine("}");
            return builder.ToString();
        }

        static string CompilationFailedDetailsJson(List<string> compileErrors, List<string> diagnosticIds, string sourceShape)
        {
            var category = CompilationDiagnosticCategory(compileErrors, diagnosticIds, sourceShape);
            var legacyCategory = category == "caller_compile_error" ? "compile_error" : category;
            return "{\"snippet_contract\":\"mcp_dynamic_csharp_execute_object_v1\""
                + ",\"wrapper_method_signature\":\"public static object Execute()\""
                + ",\"accepted_return_patterns\":[\"return null;\",\"return value;\",\"return Mcp.Result(value);\"]"
                + ",\"compilation_diagnostic_category\":" + JsonUtil.String(legacyCategory)
                + ",\"compilation_diagnostic_kind\":" + JsonUtil.String(category)
                + ",\"diagnostic_count\":" + (compileErrors == null ? 0 : compileErrors.Count)
                + ",\"diagnostics\":" + DiagnosticMessagesJson(compileErrors)
                + ",\"diagnostic_ids\":" + DiagnosticIdsJson(diagnosticIds)
                + ",\"diagnostics_truncated\":" + JsonUtil.Bool(compileErrors != null && compileErrors.Count > MaxDiagnosticMessages)
                + (string.IsNullOrEmpty(sourceShape) ? string.Empty : ",\"source_shape\":" + JsonUtil.String(sourceShape))
                + ",\"preferred_probe_commands\":" + CompilationPreferredProbeCommandsJson(category)
                + ",\"suggested_recovery_actions\":" + CompileRecoveryActionsJson(category)
                + ",\"recovery_hint\":" + JsonUtil.String(CompileRecoveryHint(category)) + "}";
        }

        static string RuntimeExceptionDetailsJson(Exception exception)
        {
            var category = RuntimeExceptionCategory(exception);
            return "{\"snippet_contract\":\"mcp_dynamic_csharp_execute_object_v1\""
                + ",\"runtime_exception_category\":" + JsonUtil.String(category)
                + ",\"exception_type\":" + JsonUtil.String(exception.GetType().Name)
                + ",\"message\":" + JsonUtil.String(BoundedString(exception.Message, MaxDiagnosticMessageChars))
                + ",\"preferred_probe_commands\":" + PreferredProbeCommandsJson()
                + ",\"suggested_recovery_actions\":" + RuntimeRecoveryActionsJson(category)
                + ",\"recovery_hint\":" + JsonUtil.String(RuntimeRecoveryHint(category)) + "}";
        }

        static string PreferredProbeCommandsJson()
        {
            return "[\"unity.api.reflect.search\",\"unity.api.reflect.type\",\"unity.api.reflect.member\",\"editor.state.get\",\"editor.selection.get\",\"editor.menu.exists\",\"logs.console.query\"]";
        }

        static string CompilationDiagnosticCategory(List<string> messages, List<string> diagnosticIds, string sourceShape)
        {
            return DynamicCSharpDiagnosticClassifier.Classify(messages, diagnosticIds, sourceShape == "leading_using_directive");
        }

        static string CompileRecoveryHint(string category)
        {
            if (category == "object_return_contract_violation")
            {
                return "The snippet is compiled as public static object Execute(); use return null;, return a value, return Mcp.Result(value), or print output with Mcp.Print(...); return null;.";
            }

            if (category == "unsupported_unity_api_shape")
            {
                return "Verify the local loaded Unity API shape with unity.api.reflect.search/type/member or use a regular catalog command instead of internal Unity reflection.";
            }

            if (category == "wrapper_syntax_error")
            {
                return "Leading namespace using directives and namespace/type/member declarations are not supported; keep the snippet as Execute() method-body statements and use existing imports or fully-qualified names.";
            }

            if (category == "caller_compile_error")
            {
                return "The snippet must compile as public static object Execute(); fix the caller C# diagnostics and keep the code as method-body statements.";
            }

            return "Review the bounded compiler diagnostics and keep dynamic C# snippets short.";
        }

        static string CompileRecoveryActionsJson(string category)
        {
            if (category == "unsupported_unity_api_shape")
            {
                return "[\"call_unity_api_reflect_search\",\"call_unity_api_reflect_type\",\"prefer_regular_catalog_command\"]";
            }

            if (category == "object_return_contract_violation")
            {
                return "[\"use_return_null_or_object\",\"use_mcp_print_for_output\",\"prefer_regular_catalog_command\"]";
            }

            if (category == "wrapper_syntax_error")
            {
                return "[\"remove_leading_using_directive\",\"keep_snippet_method_body\",\"prefer_regular_catalog_command\"]";
            }

            return "[\"fix_csharp_compile_error\",\"keep_snippet_method_body\",\"prefer_regular_catalog_command\"]";
        }

        static string CompilationPreferredProbeCommandsJson(string category)
        {
            return category == "unsupported_unity_api_shape" ? PreferredProbeCommandsJson() : "[]";
        }

        static string RuntimeExceptionCategory(Exception exception)
        {
            if (exception is MissingMemberException || exception is TypeLoadException)
            {
                return "unsupported_unity_api_shape";
            }

            if (exception is TimeoutException)
            {
                return "runtime_timeout";
            }

            if (exception is InvalidOperationException)
            {
                return "unity_state_or_api_shape";
            }

            return "runtime_exception";
        }

        static string RuntimeRecoveryHint(string category)
        {
            if (category == "unsupported_unity_api_shape")
            {
                return "Verify the local loaded Unity API shape with unity.api.reflect.search/type/member before retrying dynamic C#.";
            }

            if (category == "runtime_timeout")
            {
                return "Retry after Unity is idle, reduce snippet work, or prefer a regular command/job workflow when available.";
            }

            if (category == "unity_state_or_api_shape")
            {
                return "Check editor state and local API shape before retrying; prefer a regular catalog command when available.";
            }

            return "Inspect the bounded exception details, keep snippets short, and prefer a regular catalog command when available.";
        }

        static string RuntimeRecoveryActionsJson(string category)
        {
            if (category == "unsupported_unity_api_shape")
            {
                return "[\"call_unity_api_reflect_search\",\"call_unity_api_reflect_type\",\"prefer_regular_catalog_command\"]";
            }

            if (category == "runtime_timeout")
            {
                return "[\"call_compile.status\",\"retry_after_unity_idle\",\"prefer_regular_catalog_command_or_job\"]";
            }

            return "[\"check_editor_state\",\"prefer_regular_catalog_command\"]";
        }

        static string[] GetAssemblyPaths()
        {
            if (s_assemblyPaths != null)
            {
                return s_assemblyPaths;
            }

            var pathsByIdentity = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddAssemblyReferencePath(
                pathsByIdentity,
                Path.Combine(EditorApplication.applicationContentsPath, "NetStandard", "ref", "2.1.0", "netstandard.dll"),
                true);

            try
            {
                var compilationAssemblies = CompilationPipeline.GetAssemblies();
                for (var assemblyIndex = 0; assemblyIndex < compilationAssemblies.Length && pathsByIdentity.Count < MaxAssemblyReferenceCount; assemblyIndex++)
                {
                    var compilationAssembly = compilationAssemblies[assemblyIndex];
                    AddAssemblyReferencePath(pathsByIdentity, compilationAssembly.outputPath, false);

                    var compiledReferences = compilationAssembly.compiledAssemblyReferences;
                    for (var referenceIndex = 0; referenceIndex < compiledReferences.Length && pathsByIdentity.Count < MaxAssemblyReferenceCount; referenceIndex++)
                    {
                        AddAssemblyReferencePath(pathsByIdentity, compiledReferences[referenceIndex], false);
                    }

                    var assemblyReferences = compilationAssembly.assemblyReferences;
                    for (var referenceIndex = 0; referenceIndex < assemblyReferences.Length && pathsByIdentity.Count < MaxAssemblyReferenceCount; referenceIndex++)
                    {
                        AddAssemblyReferencePath(pathsByIdentity, assemblyReferences[referenceIndex].outputPath, false);
                    }
                }
            }
            catch
            {
                // Loaded assemblies below remain a safe fallback when compilation metadata is transiently unavailable.
            }

            foreach (var assembly in EditorAssemblyDiscovery.LoadedAssemblies())
            {
                if (pathsByIdentity.Count >= MaxAssemblyReferenceCount)
                {
                    break;
                }

                try
                {
                    var loadedAssemblyPath = LoadedAssemblyPath(assembly);
                    if (assembly.IsDynamic || string.IsNullOrEmpty(loadedAssemblyPath) || !File.Exists(loadedAssemblyPath))
                    {
                        continue;
                    }

                    AddAssemblyReferencePath(pathsByIdentity, loadedAssemblyPath, false);
                }
                catch
                {
                    // Dynamic/proxy assemblies can throw when Location is queried.
                }
            }

            var result = new string[pathsByIdentity.Count];
            pathsByIdentity.Values.CopyTo(result, 0);
            Array.Sort(result, StringComparer.OrdinalIgnoreCase);
            s_assemblyPaths = result;
            return result;
        }

        static string LoadedAssemblyPath(System.Reflection.Assembly assembly)
        {
#if UNITY_6000_0_OR_NEWER
            return assembly.GetLoadedAssemblyPath();
#else
            return assembly.Location;
#endif
        }

        static void AddAssemblyReferencePath(Dictionary<string, string> pathsByIdentity, string path, bool replace)
        {
            if (pathsByIdentity == null || pathsByIdentity.Count >= MaxAssemblyReferenceCount || string.IsNullOrEmpty(path) || path.Length > MaxAssemblyReferencePathChars)
            {
                return;
            }

            try
            {
                var fullPath = Path.GetFullPath(path);
                if (fullPath.Length > MaxAssemblyReferencePathChars || !File.Exists(fullPath))
                {
                    return;
                }

                var assemblyName = AssemblyName.GetAssemblyName(fullPath);
                var identity = assemblyName.FullName;
                if (string.IsNullOrEmpty(identity) || (!replace && pathsByIdentity.ContainsKey(identity)))
                {
                    return;
                }

                pathsByIdentity[identity] = fullPath;
            }
            catch
            {
                // Native, malformed, inaccessible and transient compiler outputs are not Roslyn references.
            }
        }

        static string FindBlockedPattern(string code, bool readOnly)
        {
            var pattern = FindBlockedPattern(code, AlwaysBlockedPatterns);
            if (!string.IsNullOrEmpty(pattern))
            {
                return pattern;
            }

            return readOnly ? FindBlockedPattern(code, ReadOnlyBlockedPatterns) : string.Empty;
        }

        static string FindBlockedPattern(string code, string[] patterns)
        {
            for (var index = 0; index < patterns.Length; index++)
            {
                if (code.IndexOf(patterns[index], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return patterns[index];
                }
            }

            return string.Empty;
        }

        static void ResetOutput(int limit)
        {
            lock (s_outputLock)
            {
                s_output.Clear();
                s_outputCharCount = 0;
                s_outputCharLimit = limit;
                s_outputTruncated = false;
            }
        }

        static void AppendOutput(string value)
        {
            lock (s_outputLock)
            {
                if (s_outputTruncated)
                {
                    return;
                }

                if (s_output.Count >= MaxOutputEntries)
                {
                    s_outputTruncated = true;
                    return;
                }

                var remaining = s_outputCharLimit - s_outputCharCount;
                if (remaining <= 0)
                {
                    s_outputTruncated = true;
                    return;
                }

                var text = ValidOutputString(value, out var invalidUnicodeReplaced);
                s_outputTruncated |= invalidUnicodeReplaced;
                if (text.Length > remaining)
                {
                    var returnedChars = remaining;
                    if (returnedChars > 0 && char.IsHighSurrogate(text[returnedChars - 1]) && char.IsLowSurrogate(text[returnedChars]))
                    {
                        returnedChars--;
                    }

                    text = text.Substring(0, returnedChars);
                    s_outputTruncated = true;
                }

                if (text.Length > 0 || string.IsNullOrEmpty(value))
                {
                    s_output.Add(text);
                }
                s_outputCharCount += text.Length;
            }
        }

        static string ValidOutputString(string value, out bool replaced)
        {
            var text = value ?? string.Empty;
            replaced = false;
            StringBuilder builder = null;
            for (var index = 0; index < text.Length; index++)
            {
                var character = text[index];
                if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                {
                    if (builder != null)
                    {
                        builder.Append(character).Append(text[++index]);
                    }
                    else
                    {
                        index++;
                    }
                    continue;
                }

                if (!char.IsSurrogate(character))
                {
                    builder?.Append(character);
                    continue;
                }

                if (builder == null)
                {
                    builder = new StringBuilder(text.Length);
                    builder.Append(text, 0, index);
                }

                builder.Append('\uFFFD');
                replaced = true;
            }

            return builder == null ? text : builder.ToString();
        }

        static string OutputJson(out bool truncated)
        {
            lock (s_outputLock)
            {
                truncated = s_outputTruncated;
                var builder = new StringBuilder();
                for (var index = 0; index < s_output.Count; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(',');
                    }

                    builder.Append(JsonUtil.String(s_output[index]));
                }

                return builder.ToString();
            }
        }

        static string JoinMessages(List<string> messages)
        {
            if (messages == null || messages.Count == 0)
            {
                return "Compilation failed";
            }

            var builder = new StringBuilder();
            for (var index = 0; index < messages.Count && index < MaxDiagnosticMessages; index++)
            {
                var message = BoundedString(messages[index], MaxDiagnosticMessageChars);
                if (index > 0)
                {
                    if (builder.Length + 2 >= MaxJoinedDiagnosticChars)
                    {
                        break;
                    }

                    builder.Append("; ");
                }

                var remaining = MaxJoinedDiagnosticChars - builder.Length;
                builder.Append(message.Length <= remaining ? message : message.Substring(0, remaining));
            }

            return builder.ToString();
        }

        static string DiagnosticMessagesJson(List<string> messages)
        {
            var builder = new StringBuilder();
            builder.Append('[');
            if (messages != null)
            {
                for (var index = 0; index < messages.Count && index < MaxDiagnosticMessages; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(',');
                    }

                    builder.Append(JsonUtil.String(BoundedString(messages[index], MaxDiagnosticMessageChars)));
                }
            }

            builder.Append(']');
            return builder.ToString();
        }

        static string DiagnosticIdsJson(List<string> diagnosticIds)
        {
            var builder = new StringBuilder();
            builder.Append('[');
            if (diagnosticIds != null)
            {
                for (var index = 0; index < diagnosticIds.Count && index < MaxDiagnosticMessages; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(',');
                    }

                    builder.Append(JsonUtil.String(DynamicCSharpDiagnosticClassifier.NormalizeDiagnosticId(diagnosticIds[index])));
                }
            }

            builder.Append(']');
            return builder.ToString();
        }

        static string BoundedString(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            {
                return value ?? string.Empty;
            }

            return value.Substring(0, maxChars) + "...";
        }

        static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        static class ReflectionRoslynCompiler
        {
            static bool? s_available;
            static Type s_syntaxTreeType;
            static Type s_syntaxTreeBaseType;
            static Type s_compilationType;
            static Type s_compilationOptionsType;
            static Type s_metadataReferenceType;
            static Type s_outputKindType;
            static Type s_diagnosticSeverityType;
            static MethodInfo s_parseText;
            static MethodInfo s_createCompilation;
            static MethodInfo s_createReference;
            static MethodInfo s_emit;
            static object s_compilationOptions;

            public static bool IsAvailable
            {
                get
                {
                    if (!s_available.HasValue)
                    {
                        s_available = Initialize();
                    }

                    return s_available.Value;
                }
            }

            public static void Reset()
            {
                s_available = null;
            }

            public static System.Reflection.Assembly Compile(string source, string[] assemblyPaths, out List<string> errors, out List<string> diagnosticIds)
            {
                errors = new List<string>();
                diagnosticIds = new List<string>();
                if (!IsAvailable)
                {
                    errors.Add("Roslyn is not available");
                    return null;
                }

                try
                {
                    var syntaxTree = ParseSource(source);
                    var references = CreateReferences(assemblyPaths);
                    var syntaxTrees = Array.CreateInstance(s_syntaxTreeBaseType, 1);
                    syntaxTrees.SetValue(syntaxTree, 0);
                    var compilationName = CreateDynamicAssemblyName();
                    var compilation = s_createCompilation.Invoke(null, new object[] { compilationName, syntaxTrees, references, s_compilationOptions });

                    using var output = new MemoryStream();
                    var emitArgs = BuildEmitArgs(output);
                    var emitResult = s_emit.Invoke(compilation, emitArgs);
                    var success = (bool)emitResult.GetType().GetProperty("Success").GetValue(emitResult, null);
                    if (!success)
                    {
                        AddDiagnostics(emitResult, errors, diagnosticIds);
                        return null;
                    }

#if UNITY_6000_0_OR_NEWER
                    return UnityEngine.Assemblies.CurrentAssemblies.LoadFromBytes(output.ToArray());
#else
                    return System.Reflection.Assembly.Load(output.ToArray());
#endif
                }
                catch (Exception exception)
                {
                    errors.Add(exception.Message);
                    return null;
                }
            }

            static bool Initialize()
            {
                try
                {
                    s_syntaxTreeType = Type.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree, Microsoft.CodeAnalysis.CSharp");
                    s_syntaxTreeBaseType = Type.GetType("Microsoft.CodeAnalysis.SyntaxTree, Microsoft.CodeAnalysis");
                    s_compilationType = Type.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation, Microsoft.CodeAnalysis.CSharp");
                    s_compilationOptionsType = Type.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions, Microsoft.CodeAnalysis.CSharp");
                    s_metadataReferenceType = Type.GetType("Microsoft.CodeAnalysis.MetadataReference, Microsoft.CodeAnalysis");
                    s_outputKindType = Type.GetType("Microsoft.CodeAnalysis.OutputKind, Microsoft.CodeAnalysis");
                    s_diagnosticSeverityType = Type.GetType("Microsoft.CodeAnalysis.DiagnosticSeverity, Microsoft.CodeAnalysis");
                    if (s_syntaxTreeType == null || s_syntaxTreeBaseType == null || s_compilationType == null || s_compilationOptionsType == null || s_metadataReferenceType == null || s_outputKindType == null || s_diagnosticSeverityType == null)
                    {
                        return false;
                    }

                    s_parseText = FindParseText();
                    s_createCompilation = FindCreateCompilation();
                    s_createReference = FindCreateReference();
                    s_emit = FindEmit();
                    s_compilationOptions = CreateCompilationOptions();
                    return s_parseText != null && s_createCompilation != null && s_createReference != null && s_emit != null && s_compilationOptions != null;
                }
                catch
                {
                    return false;
                }
            }

            static MethodInfo FindParseText()
            {
                var methods = s_syntaxTreeType.GetMethods(BindingFlags.Public | BindingFlags.Static);
                for (var index = 0; index < methods.Length; index++)
                {
                    var method = methods[index];
                    var parameters = method.GetParameters();
                    if (method.Name == "ParseText" && parameters.Length > 0 && parameters[0].ParameterType == typeof(string))
                    {
                        return method;
                    }
                }

                return null;
            }

            static MethodInfo FindCreateCompilation()
            {
                var methods = s_compilationType.GetMethods(BindingFlags.Public | BindingFlags.Static);
                for (var index = 0; index < methods.Length; index++)
                {
                    var method = methods[index];
                    var parameters = method.GetParameters();
                    if (method.Name == "Create" && parameters.Length == 4 && parameters[0].ParameterType == typeof(string))
                    {
                        return method;
                    }
                }

                return null;
            }

            static MethodInfo FindCreateReference()
            {
                var methods = s_metadataReferenceType.GetMethods(BindingFlags.Public | BindingFlags.Static);
                for (var index = 0; index < methods.Length; index++)
                {
                    var method = methods[index];
                    var parameters = method.GetParameters();
                    if (method.Name == "CreateFromFile" && parameters.Length > 0 && parameters[0].ParameterType == typeof(string))
                    {
                        return method;
                    }
                }

                return null;
            }

            static MethodInfo FindEmit()
            {
                var methods = s_compilationType.BaseType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
                for (var index = 0; index < methods.Length; index++)
                {
                    var method = methods[index];
                    var parameters = method.GetParameters();
                    if (method.Name == "Emit" && parameters.Length > 0 && typeof(Stream).IsAssignableFrom(parameters[0].ParameterType))
                    {
                        return method;
                    }
                }

                return null;
            }

            static object CreateCompilationOptions()
            {
                var dllKind = Enum.Parse(s_outputKindType, "DynamicallyLinkedLibrary");
                var constructors = s_compilationOptionsType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
                for (var index = 0; index < constructors.Length; index++)
                {
                    var constructor = constructors[index];
                    var parameters = constructor.GetParameters();
                    if (parameters.Length == 0 || parameters[0].ParameterType != s_outputKindType)
                    {
                        continue;
                    }

                    var args = new object[parameters.Length];
                    args[0] = dllKind;
                    for (var argIndex = 1; argIndex < parameters.Length; argIndex++)
                    {
                        args[argIndex] = DefaultValue(parameters[argIndex]);
                    }

                    return constructor.Invoke(args);
                }

                return null;
            }

            static object ParseSource(string source)
            {
                var parameters = s_parseText.GetParameters();
                var args = new object[parameters.Length];
                args[0] = source;
                for (var index = 1; index < parameters.Length; index++)
                {
                    args[index] = DefaultValue(parameters[index]);
                }

                return s_parseText.Invoke(null, args);
            }

            static object CreateReferences(string[] assemblyPaths)
            {
                var listType = typeof(List<>).MakeGenericType(s_metadataReferenceType);
                var references = (IList)Activator.CreateInstance(listType);
                for (var index = 0; index < assemblyPaths.Length; index++)
                {
                    try
                    {
                        references.Add(CreateReference(assemblyPaths[index]));
                    }
                    catch
                    {
                        // Roslyn can reject implementation-only or invalid metadata assemblies.
                    }
                }

                return references;
            }

            static object CreateReference(string path)
            {
                var parameters = s_createReference.GetParameters();
                var args = new object[parameters.Length];
                args[0] = path;
                for (var index = 1; index < parameters.Length; index++)
                {
                    args[index] = DefaultValue(parameters[index]);
                }

                return s_createReference.Invoke(null, args);
            }

            static object[] BuildEmitArgs(Stream output)
            {
                var parameters = s_emit.GetParameters();
                var args = new object[parameters.Length];
                args[0] = output;
                for (var index = 1; index < parameters.Length; index++)
                {
                    args[index] = DefaultValue(parameters[index]);
                }

                return args;
            }

            static object DefaultValue(ParameterInfo parameter)
            {
                if (parameter.HasDefaultValue)
                {
                    return parameter.DefaultValue;
                }

                return parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null;
            }

            static void AddDiagnostics(object emitResult, List<string> errors, List<string> diagnosticIds)
            {
                var diagnostics = emitResult.GetType().GetProperty("Diagnostics").GetValue(emitResult, null) as IEnumerable;
                var errorSeverity = Enum.Parse(s_diagnosticSeverityType, "Error");
                foreach (var diagnostic in diagnostics)
                {
                    if (errors.Count > MaxDiagnosticMessages)
                    {
                        break;
                    }

                    var severity = diagnostic.GetType().GetProperty("Severity").GetValue(diagnostic, null);
                    if (!severity.Equals(errorSeverity))
                    {
                        continue;
                    }

                    var message = "Compiler diagnostic unavailable";
                    var id = string.Empty;
                    try
                    {
                        var messageMethod = diagnostic.GetType().GetMethod("GetMessage", new[] { typeof(CultureInfo) });
                        if (messageMethod != null)
                        {
                            message = messageMethod.Invoke(diagnostic, new object[] { null }) as string ?? message;
                        }

                        var idProperty = diagnostic.GetType().GetProperty("Id");
                        id = idProperty == null ? string.Empty : idProperty.GetValue(diagnostic, null) as string;
                    }
                    catch
                    {
                        // Compiler details are diagnostic-only; never fall back to location-bearing ToString output.
                    }
                    errors.Add(BoundedString(message, MaxDiagnosticMessageChars));
                    diagnosticIds.Add(DynamicCSharpDiagnosticClassifier.NormalizeDiagnosticId(id));
                }
            }
        }
    }
}
