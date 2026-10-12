import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { pathToFileURL } from "node:url";

import { afterEach, describe, expect, it } from "vitest";

const scriptPath = path.join(process.cwd(), "scripts", "unity-editor-discovery.mjs");
const tempDirs: string[] = [];

afterEach(() => {
  for (const dir of tempDirs.splice(0)) {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

describe("Unity editor discovery script", () => {
  it("parses standalone Unity CLI JSON output", async () => {
    const { parseUnityCliEditorsJson } = await import(pathToFileURL(scriptPath).href);
    const fakeFs = { existsSync: (value: string) => value.endsWith("/Editor/Unity.exe") };

    const editors = parseUnityCliEditorsJson(JSON.stringify({
      editors: [
        { version: "6000.4.10f1", path: "/Unity/Hub/Editor/6000.4.10f1" },
      ],
    }), "unity_cli", fakeFs);

    expect(editors).toEqual([
      {
        version: "6000.4.10f1",
        executable_path: "/Unity/Hub/Editor/6000.4.10f1/Editor/Unity.exe",
        install_path: "/Unity/Hub/Editor/6000.4.10f1",
        source: "unity_cli",
        exists: true,
      },
    ]);
  });

  it("normalizes Windows executable paths from CLI output", async () => {
    const { parseUnityCliEditorsJson } = await import(pathToFileURL(scriptPath).href);
    const unityExe = "C:\\Program Files\\Unity\\Hub\\Editor\\2022.3.62f1\\Editor\\Unity.exe";
    const fakeFs = { existsSync: (value: string) => value === unityExe };

    const editors = parseUnityCliEditorsJson(JSON.stringify({ editors: [{ version: "2022.3.62f1", executablePath: unityExe }] }), "unity_cli", fakeFs);

    expect(editors[0]).toMatchObject({
      version: "2022.3.62f1",
      executable_path: unityExe,
      install_path: "C:\\Program Files\\Unity\\Hub\\Editor\\2022.3.62f1",
      exists: true,
    });
  });

  it("parses Hub CLI text output", async () => {
    const { parseEditorListText } = await import(pathToFileURL(scriptPath).href);
    const fakeFs = { existsSync: (value: string) => value.endsWith("/Editor/Unity.exe") };

    const editors = parseEditorListText("6000.6.0f1 installed at /mnt/c/Program Files/Unity/Hub/Editor/6000.6.0f1\n", "hub_cli", fakeFs);

    expect(editors[0]).toMatchObject({
      version: "6000.6.0f1",
      executable_path: "/mnt/c/Program Files/Unity/Hub/Editor/6000.6.0f1/Editor/Unity.exe",
      source: "hub_cli",
      exists: true,
    });
  });

  it("scans explicitly configured editor roots", async () => {
    const { scanUnityEditorRoots } = await import(pathToFileURL(scriptPath).href);
    const root = makeEditorRoot("6000.6.0f1");

    const result = scanUnityEditorRoots([root]);

    expect(result.editors).toHaveLength(1);
    expect(result.editors[0]).toMatchObject({ version: "6000.6.0f1", source: "editor_root", exists: true });
    expect(result.editors[0].executable_path).toBe(path.join(root, "6000.6.0f1", "Editor", "Unity.exe"));
  });

  it("falls back to configured roots when CLI probes fail", async () => {
    const { discoverUnityEditors } = await import(pathToFileURL(scriptPath).href);
    const root = makeEditorRoot("2022.3.62f1");
    const calls: string[] = [];

    const result = discoverUnityEditors({
      env: { SGG_UNITY_EDITOR_ROOTS: root },
      platform: "linux",
      runCommand: (command: string, args: string[]) => {
        calls.push(`${command} ${args.join(" ")}`);
        return { status: 1, stdout: "", stderr: "not found" };
      },
    });

    expect(calls[0]).toBe("unity editors -i --format json");
    expect(result.editors[0]).toMatchObject({ version: "2022.3.62f1", source: "editor_root" });
    expect(result.probes.some((probe: { source: string }) => probe.source === "hub_cli")).toBe(true);
  });

  it("selects the newest matching editor for partial version selectors", async () => {
    const { findUnityEditor } = await import(pathToFileURL(scriptPath).href);

    const editor = findUnityEditor([
      { version: "6000.4.9f1", executable_path: "/a", install_path: "/a", source: "test", exists: true },
      { version: "6000.4.10f1", executable_path: "/b", install_path: "/b", source: "test", exists: true },
    ], "6000.4");

    expect(editor.version).toBe("6000.4.10f1");
  });

  it("supports JSON CLI output with explicit roots", async () => {
    const { runUnityEditorDiscoveryCli } = await import(pathToFileURL(scriptPath).href);
    const root = makeEditorRoot("6000.3.15f1");
    const output: string[] = [];

    const exitCode = runUnityEditorDiscoveryCli(["--json", "--roots", root, "--roots-only"], {
      stdout: (text: string) => output.push(text),
    });

    expect(exitCode).toBe(0);
    expect(JSON.parse(output.join(""))).toMatchObject({
      status: "ok",
      editors: [{ version: "6000.3.15f1", source: "editor_root" }],
    });
  });
});

function makeEditorRoot(version: string): string {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-unity-editors-"));
  tempDirs.push(root);
  const editorDir = path.join(root, version, "Editor");
  fs.mkdirSync(editorDir, { recursive: true });
  fs.writeFileSync(path.join(editorDir, "Unity.exe"), "");
  return root;
}
