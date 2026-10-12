import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const executionCommandsPath = path.join(packageRoot, "Editor", "Commands", "EditorCSharpExecutionCommands.cs");
const resultSerializerPath = path.join(packageRoot, "Editor", "Commands", "DynamicCSharpResultSerializer.cs");
const resultStorePath = path.join(packageRoot, "Editor", "Commands", "DynamicCSharpReadonlyResultStore.cs");
const installerPath = path.join(packageRoot, "Editor", "Setup", "EditorRoslynInstaller.cs");

describe("Unity editor C# execution command source-contract", () => {
  it("declares Roslyn, dynamic C# and recompile command IDs", () => {
    const source = fs.readFileSync(executionCommandsPath, "utf8");

    expect(source).toContain('"editor.roslyn.status"');
    expect(source).toContain('"editor.roslyn.install"');
    expect(source).toContain('"editor.execute_csharp.readonly"');
    expect(source).toContain('"editor.execute_csharp.readonly.status"');
    expect(source).toContain('"editor.execute_csharp"');
    expect(source).toContain('"editor.recompile"');
  });

  it("keeps dynamic C# feature-gated and policy-visible as execute/network risk", () => {
    const source = fs.readFileSync(executionCommandsPath, "utf8");

    expect(source).toContain('const string FeatureFlag = "editor_csharp_execution"');
    expect(source.match(/FeatureFlag = FeatureFlag/g)).toHaveLength(6);
    expect(source).toContain('Risk = "execute"');
    expect(source).toContain('Risk = "network"');
    expect(source).toContain('RequiresEditorStatus = new string[] { "editing" }');
  });

  it("uses unique Roslyn assembly identities and a fixed bounded read-only recovery store", () => {
    const source = fs.readFileSync(executionCommandsPath, "utf8");
    const store = fs.readFileSync(resultStorePath, "utf8");

    expect(source).toContain('return "SGGUnityMcpDynamic_" + Guid.NewGuid().ToString("N")');
    expect(source).toContain("var compilationName = CreateDynamicAssemblyName()");
    expect(source).toContain("DynamicCSharpReadonlyResultStore.TryBegin");
    expect(source).toContain('TryStartPhase("reference_resolution"');
    expect(source).toContain('TryStartPhase("compilation"');
    expect(source).toContain('TryStartPhase("main_thread_execution"');
    expect(source).toContain('TryStartPhase("result_serialization"');
    expect(store).toContain('Contract = "sgg.unity_mcp.dynamic_csharp_readonly_result.v1"');
    expect(store).toContain('ArtifactRoot = "Library/SGGUnityMcp/job-artifacts/dynamic-csharp-readonly"');
    expect(store).toContain("caller_args_hash");
    expect(store).toContain("snippet_hash");
    expect(store).toContain("origin_unity_process_start_utc");
    expect(store).toContain("bridge_response_write");
    expect(store).toContain("command_replayed");
    expect(store).toContain("hasActiveTerminal");
    expect(store).toContain("FailPublication(receipt, storageError, true)");
    expect(store).toContain("TerminalExecutionPhasesFormPrefix");
    expect(store).toContain("SameBinding(active, durable)");
    expect(store).toContain("MoveFileEx(temporaryPath, fullPath, MoveFileReplaceExisting | MoveFileWriteThrough)");
  });

  it("uses Roslyn through reflection and Unity 6 assembly-context APIs", () => {
    const source = fs.readFileSync(executionCommandsPath, "utf8");

    expect(source).toContain("Type.GetType(\"Microsoft.CodeAnalysis.CSharp.CSharpCompilation");
    expect(source).toContain("CreateFromFile");
    expect(source).toContain("assembly.GetLoadedAssemblyPath()");
    expect(source).toContain("UnityEngine.Assemblies.CurrentAssemblies.LoadFromBytes(output.ToArray())");
    expect(source).toContain("#if UNITY_6000_0_OR_NEWER");
    expect(source).not.toContain("using Microsoft.CodeAnalysis");
  });

  it("builds deterministic compilation-aware references with the Unity netstandard 2.1 facade", () => {
    const source = fs.readFileSync(executionCommandsPath, "utf8");

    expect(source).toContain("CompilationPipeline.GetAssemblies()");
    expect(source).toContain("compiledAssemblyReferences");
    expect(source).toContain("assemblyReferences");
    expect(source).toContain('Path.Combine(EditorApplication.applicationContentsPath, "NetStandard", "ref", "2.1.0", "netstandard.dll")');
    expect(source).toContain("AssemblyName.GetAssemblyName(fullPath)");
    expect(source).toContain("Array.Sort(result, StringComparer.OrdinalIgnoreCase)");
    expect(source).toContain("MaxAssemblyReferenceCount = 4096");
  });

  it("invalidates only the dynamic reference cache after Unity compilation", () => {
    const source = fs.readFileSync(executionCommandsPath, "utf8");
    const callback = source.match(/static void OnCompilationFinished\(object context\)\s*\{[\s\S]*?\n        \}/)?.[0] ?? "";

    expect(source).toContain("CompilationPipeline.compilationFinished -= OnCompilationFinished");
    expect(source).toContain("CompilationPipeline.compilationFinished += OnCompilationFinished");
    expect(callback).toContain("s_assemblyPaths = null");
    expect(callback).not.toContain("ReflectionRoslynCompiler.Reset");
  });

  it("enforces static safety checks and bounded output", () => {
    const source = fs.readFileSync(executionCommandsPath, "utf8");

    expect(source).toContain("AlwaysBlockedPatterns");
    expect(source).toContain("ReadOnlyBlockedPatterns");
    expect(source).toContain("dangerous_code_rejected");
    expect(source).toContain("max_output_chars");
    expect(source).toContain("MaxOutputChars = 20000");
  });

  it("documents dynamic C# object-return contract in the command schema", () => {
    const source = fs.readFileSync(executionCommandsPath, "utf8");
    const schemaLiteral = source.match(/const string ExecuteSchemaJson = "((?:\\.|[^"])*)";/)?.[1];

    expect(schemaLiteral).toBeTruthy();
    const schema = JSON.parse(JSON.parse(`"${schemaLiteral}"`));

    expect(schema.properties.code).toMatchObject({
      type: "string",
      minLength: 1,
      maxLength: 50000,
    });
    expect(schema.properties.code.description).toContain("public static object Execute()");
    expect(schema.properties.code.description).toContain("Leading namespace using directives are not supported");
    expect(schema.properties.code.description).toContain("return null;");
    expect(schema.properties.code.description).toContain("return value;");
    expect(schema.properties.code.description).toContain("return Mcp.Result(value);");
    expect(schema.properties.code.description).toContain("Prefer regular catalog commands");
    expect(schema.properties.response_mode).toEqual({
      type: "string",
      description: "Both modes retain independent result-serialization limits; full does not mean unlimited scalar text.",
      enum: ["full", "compact"],
      default: "full",
    });
    expect(schema.properties.max_output_chars).toEqual({
      type: "integer",
      description: "Maximum UTF-16 code units retained across captured console output entries; default 4000. Does not control returned-value serialization or whole-response size.",
      minimum: 1,
      maximum: 20000,
    });
  });

  it("discloses existing escaped-byte and traversal limits without changing their applied constants", () => {
    const source = fs.readFileSync(executionCommandsPath, "utf8");
    const serializer = fs.readFileSync(resultSerializerPath, "utf8");
    const literal = source.match(/const string OutputLimitJson = "((?:\\.|[^"])*)";/)?.[1];
    expect(literal).toBeTruthy();
    const limits = JSON.parse(JSON.parse(`"${literal}"`));
    const constants = {
      max_string_json_content_bytes: "MaxStringJsonContentBytes",
      max_visited_values: "MaxVisitedValues",
      max_enumerable_scan_items: "MaxEnumerableScanItems",
      max_name_chars: "MaxNameChars",
      max_name_json_content_bytes: "MaxNameJsonContentBytes",
    };
    for (const [field, constant] of Object.entries(constants)) {
      const applied = serializer.match(new RegExp(`const int ${constant} = (\\d+);`))?.[1];
      expect(applied).toBeTruthy();
      expect(limits[field]).toBe(Number(applied));
    }
    expect(limits).toMatchObject({ max_code_chars: 50000, max_output_chars: 20000, max_output_entries: 256, max_result_depth: 4, max_result_items: 100, max_result_utf8_bytes: 65536 });
    expect(serializer).toContain('\\"reason\\":\\"max_string_json_content_bytes\\",\\"applied_limit\\":');
    expect(serializer).toContain("MaxStringJsonContentBytes.ToString(CultureInfo.InvariantCulture)");
    expect(source).toContain("retained projected data, not omitted string tails");
  });

  it("exposes recursive collection bounds and canonical truncation authority", () => {
    const source = fs.readFileSync(executionCommandsPath, "utf8");
    const serializer = fs.readFileSync(resultSerializerPath, "utf8");

    expect(source).toContain('\\"max_result_items\\":100');
    expect(source).toContain('\\"max_result_utf8_bytes\\":65536');
    expect(source).toContain('\\"max_output_entries\\":256');
    expect(source).toContain('\\"result_summary\\":');
    expect(source).toContain('\\"result_truncated\\":');
    expect(source).toContain('\\"console_output_truncated\\":');
    expect(source).toContain("consoleOutputTruncated || serialized.Truncated");
    expect(serializer).toContain("internal const int MaxItems = 100");
    expect(serializer).toContain("internal const int MaxResultUtf8Bytes = 65536");
    expect(serializer).toContain('\\"total_count\\"');
    expect(serializer).toContain('\\"returned_count\\"');
    expect(serializer).toContain('\\"truncated\\"');
    expect(serializer).toContain("SortedDictionary<string, object>(StringComparer.Ordinal)");
    expect(serializer).toContain('"result_serialization_unsupported"');
    expect(serializer).not.toContain("JsonUtil.String(value.ToString())");
  });

  it("adds bounded compile-failure recovery details without echoing raw code", () => {
    const source = fs.readFileSync(executionCommandsPath, "utf8");
    const detailsSource = source.match(/static string CompilationFailedDetailsJson\(List<string> compileErrors, List<string> diagnosticIds, string sourceShape\)\s*\{[\s\S]*?\n        \}/)?.[0] ?? "";

    expect(source).toContain('CommandExecutionResult.Error("compilation_failed", JoinMessages(compileErrors), CompilationFailedDetailsJson(compileErrors, diagnosticIds, string.Empty))');
    expect(detailsSource).toContain("mcp_dynamic_csharp_execute_object_v1");
    expect(detailsSource).toContain("public static object Execute()");
    expect(detailsSource).toContain("accepted_return_patterns");
    expect(detailsSource).toContain("return null;");
    expect(detailsSource).toContain("return value;");
    expect(detailsSource).toContain("return Mcp.Result(value);");
    expect(detailsSource).toContain("compilation_diagnostic_category");
    expect(detailsSource).toContain("compilation_diagnostic_kind");
    expect(detailsSource).toContain("diagnostic_count");
    expect(detailsSource).toContain("diagnostics");
    expect(detailsSource).toContain("diagnostic_ids");
    expect(detailsSource).toContain("diagnostics_truncated");
    expect(detailsSource).toContain("source_shape");
    expect(detailsSource).toContain("preferred_probe_commands");
    expect(detailsSource).toContain("suggested_recovery_actions");
    expect(source).toContain("object_return_contract_violation");
    expect(source).toContain("caller_compile_error");
    expect(source).toContain('category == "caller_compile_error" ? "compile_error" : category');
    expect(source).toContain("unsupported_unity_api_shape");
    expect(source).toContain("wrapper_syntax_error");
    expect(source).toContain("CS1001");
    expect(source).toContain("Identifier expected");
    expect(source).toContain("PreferredProbeCommandsJson()");
    expect(source).toContain("unity.api.reflect.search");
    expect(source).toContain("editor.state.get");
    expect(source).toContain("logs.console.query");
    expect(source).toContain("MaxDiagnosticMessages = 5");
    expect(source).toContain("MaxDiagnosticMessageChars = 500");
    expect(detailsSource).toContain("recovery_hint");
    expect(detailsSource).not.toContain("user code");
    expect(detailsSource).not.toContain("code\"");
    expect(detailsSource).not.toContain('"source":');
  });

  it("rejects namespace using directives before wrapper generation while retaining method-body using forms", () => {
    const source = fs.readFileSync(executionCommandsPath, "utf8");
    const rejection = source.indexOf("HasLeadingUsingDirective(code)");
    const compilerAvailability = source.indexOf("ReflectionRoslynCompiler.IsAvailable");
    const wrapper = source.indexOf("WrapUserCode(code)");

    expect(rejection).toBeGreaterThan(-1);
    expect(rejection).toBeLessThan(compilerAvailability);
    expect(rejection).toBeLessThan(wrapper);
    expect(source).toContain('"leading_using_directive"');
    expect(source).toContain('ReadKeyword(code, ref index, "var")');
    expect(source).toContain('ReadKeyword(code, ref index, "static")');
    expect(source).toContain("code[index] == '('");
    expect(source).toContain("code[index] == '='");
  });

  it("keeps compiler diagnostics and recovery non-reflective for wrapper and caller categories", () => {
    const source = fs.readFileSync(executionCommandsPath, "utf8");
    const recovery = source.slice(source.indexOf("static string CompileRecoveryHint"), source.indexOf("static string CompilationPreferredProbeCommandsJson"));

    expect(recovery).toContain("caller_compile_error");
    expect(recovery).toContain("wrapper_syntax_error");
    expect(recovery).toContain("remove_leading_using_directive");
    expect(recovery).toContain("fix_csharp_compile_error");
    expect(source).toContain('return category == "unsupported_unity_api_shape" ? PreferredProbeCommandsJson() : "[]"');
    expect(source).toContain('ContainsQualifiedToken(message, "UnityEditor.")');
    expect(source).toContain('ContainsQualifiedToken(message, "UnityEngine.")');
    expect(source).not.toContain('diagnostic.ToString()');
  });

  it("adds bounded runtime exception diagnostics without stack traces", () => {
    const source = fs.readFileSync(executionCommandsPath, "utf8");
    const detailsSource = source.match(/static string RuntimeExceptionDetailsJson\(Exception exception\)\s*\{[\s\S]*?\n        \}/)?.[0] ?? "";

    expect(source).toContain('RuntimeExceptionDetailsJson(inner)');
    expect(source).toContain('RuntimeExceptionDetailsJson(exception)');
    expect(detailsSource).toContain("runtime_exception_category");
    expect(detailsSource).toContain("exception_type");
    expect(detailsSource).toContain("preferred_probe_commands");
    expect(detailsSource).toContain("suggested_recovery_actions");
    expect(source).toContain("runtime_timeout");
    expect(source).toContain("unity_state_or_api_shape");
    expect(detailsSource).not.toContain("StackTrace");
  });

  it("adds a Unity menu installer for Roslyn DLLs", () => {
    const source = fs.readFileSync(installerPath, "utf8");

    expect(source).toContain('MenuItem("SGG/Unity MCP/Install Roslyn Compiler")');
    expect(source).toContain("https://api.nuget.org/v3-flatcontainer/");
    expect(source).toContain('ProjectRelativeFolder => "Assets/" + PluginsRelativePath');
    expect(source).toContain("Microsoft.CodeAnalysis.CSharp.dll");
    expect(source).toContain('"system.runtime.compilerservices.unsafe", "6.0.0"');
    expect(source).toContain("System.Runtime.CompilerServices.Unsafe.dll");
  });
});
