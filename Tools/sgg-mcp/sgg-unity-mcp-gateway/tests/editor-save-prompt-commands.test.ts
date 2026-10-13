import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const savePromptCommandsPath = path.join(packageRoot, "Editor", "Commands", "EditorSavePromptCommands.cs");
const bridgeServerPath = path.join(packageRoot, "Editor", "Bridge", "BridgeServer.cs");
const dispatcherPath = path.join(process.cwd(), "src", "mcp", "dispatcher.ts");
const resultsPath = path.join(process.cwd(), "src", "mcp", "results.ts");

describe("Unity editor save prompt rescue commands", () => {
  it("declares read and destructive save-prompt commands in the catalog", () => {
    const source = fs.readFileSync(savePromptCommandsPath, "utf8");

    expect(source).toContain('"editor.save_prompt.get"');
    expect(source).toContain('"editor.save_prompt.respond"');
    expect(source).toContain('Risk = "read"');
    expect(source).toContain('Risk = "destructive"');
    expect(source).toContain('Idempotency = "safe"');
    expect(source).toContain('Idempotency = "unsafe"');
    expect(source).toContain('\\"enum\\":[\\"save\\",\\"dont_save\\",\\"cancel\\"]');
    expect(source).toContain('Tags = new string[] { "editor", "status", "rescue", "save-prompt" }');
    expect(source).toContain('IsBackgroundCommand(string commandId)');
    expect(source).toContain('InvokeBackground(string commandId, string argsJson)');
    expect(source).not.toContain('public static List<PromptInfo> FindPrompts()');
    expect(source).not.toContain('public static string StatusJson(List<PromptInfo> prompts)');
    expect(source).toContain('public static string StatusJson()');
    expect(source).toContain('public static CommandExecutionResult Respond(string action, bool dryRun)');
  });

  it("limits prompt detection and clicks to Windows dialogs owned by the Unity process", () => {
    const source = fs.readFileSync(savePromptCommandsPath, "utf8");

    expect(source).toContain("#if UNITY_EDITOR_WIN");
    expect(source).toContain("EnumWindows");
    expect(source).toContain("EnumChildWindows");
    expect(source).toContain("GetWindowThreadProcessId(window, out var windowProcessId)");
    expect(source).toContain("Process.GetCurrentProcess().Id");
    expect(source).toContain('string.Equals(className, "Button", StringComparison.OrdinalIgnoreCase)');
    expect(source).toContain("LooksLikeSceneSavePrompt");
    expect(source).toContain('lower.Contains(".unity")');
    expect(source).toContain("BM_CLICK");
    expect(source).toContain("SendMessageTimeout(button.Handle, BM_CLICK");
    expect(source).toContain('const string RussianDontSaveKey = "\\u043d\\u0435\\u0441\\u043e\\u0445\\u0440\\u0430\\u043d\\u044f\\u0442\\u044c"');
    expect(source).not.toContain("EditorUtility.DisplayDialog");
    expect(source).not.toContain("EditorSceneManager");
  });

  it("runs rescue commands outside the Unity main-thread dispatcher", () => {
    const bridgeSource = fs.readFileSync(bridgeServerPath, "utf8");
    const dispatcherSource = fs.readFileSync(dispatcherPath, "utf8");
    const resultsSource = fs.readFileSync(resultsPath, "utf8");

    expect(bridgeSource).toContain("ThreadPool.QueueUserWorkItem(_ => HandleSafely(context))");
    expect(bridgeSource).toContain("void HandleSafely(HttpListenerContext context)");
    expect(bridgeSource).toContain("EditorSavePromptCommands.IsBackgroundCommand(command)");
    expect(bridgeSource).toContain("EditorSavePromptCommands.InvokeBackground(command, argsJson)");
    expect(bridgeSource.indexOf("EditorSavePromptCommands.IsBackgroundCommand(command)")).toBeLessThan(bridgeSource.indexOf('if (builtInCommand && command == "compile.wait_ready")'));
    expect(dispatcherSource).toContain('const SAVE_PROMPT_COMMANDS = new Set(["editor.save_prompt.get", "editor.save_prompt.respond"])');
    expect(dispatcherSource).toContain("allowStale: allowsStaleProjectResolution(commandId)");
    expect(dispatcherSource).toContain("return isSavePromptRescueCommand(commandId) || commandId === EDITOR_MENU_OPERATION_STATUS_COMMAND");
    expect(dispatcherSource).toContain("allowStale: isSavePromptDescribeSelector(args.selector)");
    expect(dispatcherSource).toContain("if (queueOwned || !context.queues || isSavePromptRescueCommand(commandId))");
    expect(resultsSource).toContain('"unsupported_platform"');
  });
});
