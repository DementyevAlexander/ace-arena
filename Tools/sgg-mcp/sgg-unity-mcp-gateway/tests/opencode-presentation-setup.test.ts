import fs from "node:fs";
import { createHash } from "node:crypto";
import os from "node:os";
import path from "node:path";

import { parse } from "jsonc-parser";
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from "vitest";

import { runSetupCli } from "../src/cli.js";
import { parseOpencodePresentationSetupOptions } from "../src/setup/opencode-presentation.js";
// @ts-expect-error The directly executable build helper has no separate declaration file.
import { createOpenCodePresentationArtifact } from "../scripts/opencode-presentation-artifact.mjs";

let artifactRoot: string;
const legacyArtifactRoot = path.join(process.cwd(), "integrations/opencode-sgg-unity-presentation/test/fixtures/legacy-1.0.0");
const tempDirs: string[] = [];

beforeAll(async () => {
  artifactRoot = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-presentation-bundle-"));
  const artifact = await createOpenCodePresentationArtifact(process.cwd());
  for (const [name, content] of artifact.files) fs.writeFileSync(path.join(artifactRoot, name), content);
});

afterAll(() => fs.rmSync(artifactRoot, { recursive: true, force: true }));

afterEach(() => {
  vi.restoreAllMocks();
  for (const dir of tempDirs.splice(0)) fs.rmSync(dir, { recursive: true, force: true });
});

describe("OpenCode presentation setup lifecycle", () => {
  it("requires an exact workspace and one explicit action", () => {
    expect(parseOpencodePresentationSetupOptions(["--workspace", "workspace with spaces", "--dry-run", "--json"])).toEqual({
      workspace: "workspace with spaces",
      action: "dry-run",
      json: true,
      help: false,
    });
    expect(() => parseOpencodePresentationSetupOptions(["--status"])).toThrow("--workspace is required");
    expect(() => parseOpencodePresentationSetupOptions(["--workspace", ".", "--status", "--write"])).toThrow("choose exactly one action");
  });

  it("rejects drive-relative, rooted-drive, and UNC workspace forms instead of rebasing them", () => {
    for (const workspace of ["C:relative", "\\rooted", "\\\\server\\share"]) {
      const result = invoke(workspace, "--status");
      expect(result.code).toBe(1);
      expect(result.error).toContain("foreign absolute path is unavailable on this host");
    }
  });

  it("reports install work without changing a fresh workspace during dry-run", () => {
    const workspace = makeWorkspace("dry run with spaces");
    const result = invoke(workspace, "--dry-run");

    expect(result.code).toBe(0);
    expect(result.json).toMatchObject({
      action: "dry-run",
      artifact: { state: "absent", bundled_version: "2.0.0" },
      config: { state: "missing", file: "opencode.jsonc" },
      changes: ["install_bundled_artifact", "add_owned_config_entry"],
      applied: false,
      restart_required: false,
    });
    expect(fs.existsSync(path.join(workspace, ".opencode"))).toBe(false);
    expect(result.output).not.toContain(artifactRoot);
  });

  it("reports the requested workspace alias while binding lifecycle access to its real target", () => {
    const target = makeWorkspace("aliased target");
    const aliasRoot = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-opencode-presentation-alias-"));
    tempDirs.push(aliasRoot);
    const alias = path.join(aliasRoot, "workspace alias");
    fs.symlinkSync(target, alias, process.platform === "win32" ? "junction" : "dir");

    const result = invoke(alias, "--status");

    expect(result.code).toBe(0);
    expect(result.json.workspace).toBe(path.resolve(alias));
    expect(result.json.artifact.state).toBe("absent");
  });

  it.runIf(process.platform === "linux" && /^\/mnt\/[A-Za-z]\//.test(process.cwd()))("projects a Windows workspace path onto the current WSL host", () => {
    const workspace = fs.mkdtempSync(path.join(process.cwd(), ".sgg-opencode-presentation-wsl-"));
    tempDirs.push(workspace);
    const windowsWorkspace = `${workspace[5]!.toUpperCase()}:${workspace.slice(6)}`;

    const result = invoke(windowsWorkspace, "--status");

    expect(result.code).toBe(0);
    expect(result.json.workspace).toBe(workspace);
  });

  it.runIf(process.platform === "linux" && /^\/mnt\/[A-Za-z]\//.test(process.cwd()))("blocks case-only aliases into an owned artifact on a Windows-backed WSL mount", () => {
    const workspace = fs.mkdtempSync(path.join(process.cwd(), ".sgg-opencode-presentation-case-"));
    tempDirs.push(workspace);
    writeConfig(workspace, "{\n  \"plugin\": [\"./.OPENCODE/SGG-UNITY-PRESENTATION/server.js\"]\n}\n");

    const result = invoke(workspace, "--write");

    expect(result.code).toBe(1);
    expect(result.json.blocked_reasons).toContain("owned_plugin_entry_conflict");
    expect(fs.existsSync(path.join(workspace, ".opencode", "sgg-unity-presentation"))).toBe(false);
  });

  it("fails closed when a workspace alias is retargeted between inspection and mutation", () => {
    const firstTarget = makeWorkspace("first alias target");
    const secondTarget = makeWorkspace("second alias target");
    const aliasRoot = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-opencode-presentation-retarget-"));
    tempDirs.push(aliasRoot);
    const alias = path.join(aliasRoot, "workspace alias");
    const symlinkType = process.platform === "win32" ? "junction" : "dir";
    fs.symlinkSync(firstTarget, alias, symlinkType);
    const nativeRealpath = fs.realpathSync.native.bind(fs.realpathSync);
    let aliasChecks = 0;
    vi.spyOn(fs.realpathSync, "native").mockImplementation(((...args: Parameters<typeof fs.realpathSync.native>) => {
      const result = nativeRealpath(...args);
      if (path.resolve(String(args[0])) === path.resolve(alias) && ++aliasChecks === 2) {
        fs.rmSync(alias, { recursive: true, force: true });
        fs.symlinkSync(secondTarget, alias, symlinkType);
      }
      return result;
    }) as typeof fs.realpathSync.native);

    const result = invoke(alias, "--write");

    expect(result.code).toBe(1);
    expect(result.error).toContain("workspace identity changed before write");
    expect(fs.existsSync(path.join(firstTarget, ".opencode"))).toBe(false);
    expect(fs.existsSync(path.join(secondTarget, ".opencode"))).toBe(false);
  });

  it("installs exact files and preserves JSONC comments, formatting, and unrelated plugins", () => {
    const workspace = makeWorkspace("commented install");
    const configPath = writeConfig(workspace, `{
\t// retained top-level comment
\t"plugins": [
\t\t"custom-plugin@1.0.0" // retained plugin comment
\t],
\t"permission": { "bash": "ask" }
}\n`);

    const result = invoke(workspace, "--write");
    const source = fs.readFileSync(configPath, "utf8");
    const config = parse(source) as { plugins: string[]; permission: Record<string, string> };
    const installed = path.join(workspace, ".opencode", "sgg-unity-presentation");

    expect(result.code).toBe(0);
    expect(result.json).toMatchObject({ artifact: { state: "current" }, config: { state: "configured", owned_entries: 1 }, applied: true, restart_required: true });
    expect(fs.readdirSync(installed).sort()).toEqual(["package.json", "server.js", "sgg-plugin-manifest.json"]);
    expect(config.plugins).toEqual(["custom-plugin@1.0.0", "./.opencode/sgg-unity-presentation"]);
    expect(config.permission).toEqual({ bash: "ask" });
    expect(source).toContain("// retained top-level comment");
    expect(source).toContain("// retained plugin comment");
    expect(source).toContain("\t\"plugins\"");
  });

  it("extends the root project config without changing existing MCP settings", () => {
    const workspace = makeWorkspace("existing root MCP config");
    const configPath = writeConfig(workspace, `{
  "$schema": "https://opencode.ai/config.json",
  // Keep the working project transport exactly as configured.
  "mcp": {
    "sgg-unity-mcp": {
      "type": "local",
      "command": ["powershell.exe", "-File", "C:\\\\Tools\\\\sgg-unity-mcp.ps1"],
      "enabled": true,
      "timeout": 3900000
    }
  }
}\n`);
    const originalMcp = (parse(fs.readFileSync(configPath, "utf8")) as Record<string, unknown>).mcp;

    const result = invoke(workspace, "--write");
    const source = fs.readFileSync(configPath, "utf8");
    const config = parse(source) as { plugins: string[]; mcp: unknown };

    expect(result.code).toBe(0);
    expect(config.plugins).toEqual(["./.opencode/sgg-unity-presentation"]);
    expect(config.mcp).toEqual(originalMcp);
    expect(source).toContain("Keep the working project transport exactly as configured.");
    expect(fs.existsSync(path.join(workspace, ".opencode", "opencode.jsonc"))).toBe(false);
  });

  it("deduplicates only exact owned entries", () => {
    const workspace = makeWorkspace("dedupe");
    const configPath = writeConfig(workspace, `{
  "plugin": [
    "./.opencode/sgg-unity-presentation",
    "custom-plugin",
    "./.opencode/sgg-unity-presentation",
    ["configured-plugin", { "mode": "safe" }]
  ]
}\n`);

    const result = invoke(workspace, "--write");
    const config = parse(fs.readFileSync(configPath, "utf8")) as { plugin: unknown[]; plugins: unknown[] };

    expect(result.code).toBe(0);
    expect(result.json.changes).toContain("deduplicate_owned_config_entry");
    expect(config.plugins).toEqual(["./.opencode/sgg-unity-presentation"]);
    expect(config.plugin).toContain("custom-plugin");
    expect(config.plugin).toContainEqual(["configured-plugin", { mode: "safe" }]);
  });

  it("is idempotent after a successful install", () => {
    const workspace = makeWorkspace("idempotent");
    expect(invoke(workspace, "--write").code).toBe(0);

    const second = invoke(workspace, "--write");
    expect(second.code).toBe(0);
    expect(second.json).toMatchObject({ changes: ["noop"], applied: false, restart_required: false });
  });

  it("preserves native owned object options and unrelated unknown entries verbatim", () => {
    const workspace = makeWorkspace("native options");
    const source = `{
  "plugins": [
    { "package": "custom-plugin", "options": { "unknown": 42 }, "future": true },
    // keep owned plugin options and comments
    { "package": "./.opencode/sgg-unity-presentation", "options": { "serverPrefixes": ["unity-dev"] }, "future": "retained" },
    { "unrecognized": ["future plugin entry"] }
  ],
  "plugin": [["legacy-unrelated", { "preserve": true }]]
}\n`;
    const configPath = writeConfig(workspace, source);
    expect(invoke(workspace, "--write").code).toBe(0);
    expect(fs.readFileSync(configPath, "utf8")).toBe(source);
    expect(invoke(workspace, "--write").json.changes).toEqual(["noop"]);
    expect(invoke(workspace, "--remove").code).toBe(0);
    const config = parse(fs.readFileSync(configPath, "utf8"));
    expect(config.plugins).toEqual([
      { package: "custom-plugin", options: { unknown: 42 }, future: true },
      { unrecognized: ["future plugin entry"] },
    ]);
    expect(config.plugin).toEqual([["legacy-unrelated", { preserve: true }]]);
  });

  it("migrates only the owned legacy tuple into native plugins without copying unrelated entries", () => {
    const workspace = makeWorkspace("legacy options");
    const configPath = writeConfig(workspace, `{
  // keep global comment
  "plugin": [
    "legacy-unrelated", // keep unrelated comment
    ["./.opencode/sgg-unity-presentation", { "serverPrefixes": ["unity-dev"], "unknown": { "value": true } }],
    ["another-unrelated", { "private": "retained" }]
  ],
  "plugins": [{ "package": "native-unrelated", "options": { "mode": "safe" } }]
}\n`);
    const result = invoke(workspace, "--write");
    expect(result.code).toBe(0);
    expect(result.json.changes).toContain("migrate_owned_legacy_config_entry");
    const source = fs.readFileSync(configPath, "utf8");
    const config = parse(source);
    expect(config.plugin).toEqual(["legacy-unrelated", ["another-unrelated", { private: "retained" }]]);
    expect(config.plugins).toEqual([
      { package: "native-unrelated", options: { mode: "safe" } },
      { package: "./.opencode/sgg-unity-presentation", options: { serverPrefixes: ["unity-dev"], unknown: { value: true } } },
    ]);
    expect(source).toContain("// keep global comment");
    expect(source).toContain("// keep unrelated comment");
  });

  it("preserves comments nested inside migrated options and all unrelated option comments", () => {
    const workspace = makeWorkspace("commented legacy options");
    const configPath = writeConfig(workspace, `{
  "plugin": [
    ["./.opencode/sgg-unity-presentation", {
      // keep server alias comment
      "serverPrefixes": ["unity-dev"], /* keep alias note */
      "unknown": true
    }],
    ["unrelated", { /* unrelated inline options */ "retain": true }]
  ]
}\n`);
    const result = invoke(workspace, "--write");
    expect(result.error).toBe("");
    expect(result.code).toBe(0);
    const source = fs.readFileSync(configPath, "utf8");
    for (const comment of ["// keep server alias comment", "/* keep alias note */", "/* unrelated inline options */"]) expect(source).toContain(comment);
    expect(parse(source).plugins).toEqual([{ package: "./.opencode/sgg-unity-presentation", options: { serverPrefixes: ["unity-dev"], unknown: true } }]);
  });

  it.each([
    '["./.opencode/sgg-unity-presentation","unrelated"]',
    '["before","./.opencode/sgg-unity-presentation","after"]',
    '["unrelated","./.opencode/sgg-unity-presentation"]',
    '["unrelated","./.opencode/sgg-unity-presentation", /* trailing comment */]',
    '["./.opencode/sgg-unity-presentation"]',
  ])("removes only the exact owned entry from compact native arrays: %s", (plugins) => {
    const workspace = makeWorkspace("compact arrays");
    const configPath = writeConfig(workspace, `{ "plugins": ${plugins} }\n`);
    expect(invoke(workspace, "--write").code).toBe(0);
    expect(invoke(workspace, "--remove").code).toBe(0);
    const source = fs.readFileSync(configPath, "utf8");
    expect(parse(source).plugins).toEqual((parse(`{ "plugins": ${plugins} }`).plugins as string[]).filter((entry) => entry !== "./.opencode/sgg-unity-presentation"));
    if (plugins.includes("trailing comment")) expect(source).toContain("/* trailing comment */");
  });

  it("deduplicates identical native object entries without discarding their options", () => {
    const workspace = makeWorkspace("native duplicate objects");
    const owned = { package: "./.opencode/sgg-unity-presentation", options: { serverPrefixes: ["unity-dev"] } };
    const configPath = writeConfig(workspace, `${JSON.stringify({ plugins: [owned, "unrelated", owned] })}\n`);
    const result = invoke(workspace, "--write");
    expect(result.code).toBe(0);
    expect(result.json.changes).toContain("deduplicate_owned_config_entry");
    expect(parse(fs.readFileSync(configPath, "utf8")).plugins).toEqual([owned, "unrelated"]);
  });

  it.each([
    { plugin: ["./.opencode/sgg-unity-presentation"], plugins: ["./.opencode/sgg-unity-presentation"] },
    { plugin: [["./.opencode/sgg-unity-presentation", { serverPrefixes: ["a"] }]], plugins: [{ package: "./.opencode/sgg-unity-presentation", options: { serverPrefixes: ["b"] } }] },
  ])("fails closed for simultaneous native and legacy owned registrations: %j", (config) => {
    const workspace = makeWorkspace("native legacy conflict");
    const source = `${JSON.stringify(config)}\n`;
    const configPath = writeConfig(workspace, source);
    const result = invoke(workspace, "--write");
    expect(result.code).toBe(1);
    expect(result.json.blocked_reasons).toContain("owned_plugin_native_legacy_conflict");
    expect(fs.readFileSync(configPath, "utf8")).toBe(source);
    expect(fs.existsSync(path.join(workspace, ".opencode"))).toBe(false);
  });

  it.each(["-sgg-unity.presentation", "-sgg-unity.*", "-*", "-./.opencode/sgg-unity-presentation"])("honors human disable directive %s without enabling or removing the plugin", (directive) => {
    const workspace = makeWorkspace("disabled plugin");
    const source = `${JSON.stringify({ plugin: ["unrelated"], plugins: [directive] })}\n`;
    const configPath = writeConfig(workspace, source);
    for (const action of ["--write", "--remove"] as const) {
      const result = invoke(workspace, action);
      expect(result.code).toBe(1);
      expect(result.json.blocked_reasons).toContain("owned_plugin_disabled");
    }
    expect(fs.readFileSync(configPath, "utf8")).toBe(source);
    expect(fs.existsSync(path.join(workspace, ".opencode"))).toBe(false);
  });

  it.each([
    [{ package: "./.opencode/sgg-unity-presentation/server.js", options: {} }],
    [{ package: "./.opencode/sgg-unity-presentation", options: { serverPrefixes: ["a"] } }, { package: "./.opencode/sgg-unity-presentation", options: { serverPrefixes: ["b"] } }],
    [["./.opencode/sgg-unity-presentation", {}]],
    ["@sungeargames/opencode-sgg-unity-presentation@2.0.0"],
  ])("blocks conflicting native owned objects, paths, and registrations: %j", (...plugins) => {
    const workspace = makeWorkspace("native path conflict");
    writeConfig(workspace, `${JSON.stringify({ plugins })}\n`);
    const result = invoke(workspace, "--write");
    expect(result.code).toBe(1);
    expect(result.json.blocked_reasons).toContain("owned_plugin_entry_conflict");
  });

  it("rejects duplicate native config keys", () => {
    const workspace = makeWorkspace("duplicate native key");
    writeConfig(workspace, '{ "plugins": [], "plugins": [] }\n');
    expect(invoke(workspace, "--write").json.blocked_reasons).toContain("duplicate_plugin_config_key");
  });

  it("refuses simultaneous opencode.json and opencode.jsonc without mutation", () => {
    const workspace = makeWorkspace("dual config");
    const opencode = path.join(workspace, ".opencode");
    fs.writeFileSync(path.join(workspace, "opencode.json"), "{}\n");
    fs.writeFileSync(path.join(workspace, "opencode.jsonc"), "{ // private comment\n}\n");

    const result = invoke(workspace, "--write");

    expect(result.code).toBe(1);
    expect(result.json.blocked_reasons).toContain("dual_opencode_config");
    expect(fs.existsSync(path.join(opencode, "sgg-unity-presentation"))).toBe(false);
    expect(fs.readFileSync(path.join(workspace, "opencode.jsonc"), "utf8")).toContain("private comment");
  });

  it("refuses an unknown artifact without changing config or files", () => {
    const workspace = makeWorkspace("unknown artifact");
    const configPath = writeConfig(workspace, "{\n  \"plugin\": []\n}\n");
    const target = path.join(workspace, ".opencode", "sgg-unity-presentation");
    fs.mkdirSync(target, { recursive: true });
    fs.writeFileSync(path.join(target, "foreign.txt"), "do not replace\n");

    const result = invoke(workspace, "--write");

    expect(result.code).toBe(1);
    expect(result.json.blocked_reasons).toContain("installed_artifact_unknown");
    expect(fs.readFileSync(path.join(target, "foreign.txt"), "utf8")).toBe("do not replace\n");
    expect(parse(fs.readFileSync(configPath, "utf8"))).toEqual({ plugin: [] });
  });

  it("refuses update and removal of a modified owned artifact", () => {
    const workspace = makeWorkspace("modified artifact");
    expect(invoke(workspace, "--write").code).toBe(0);
    const serverPath = path.join(workspace, ".opencode", "sgg-unity-presentation", "server.js");
    fs.appendFileSync(serverPath, "// local modification\n");

    const update = invoke(workspace, "--write");
    const remove = invoke(workspace, "--remove");

    expect(update.code).toBe(1);
    expect(remove.code).toBe(1);
    expect(update.json.blocked_reasons).toContain("installed_artifact_modified");
    expect(remove.json.blocked_reasons).toContain("installed_artifact_modified");
    expect(fs.readFileSync(serverPath, "utf8")).toContain("local modification");
  });

  it("refuses a self-consistent generation whose manifest is not trusted", () => {
    const workspace = makeWorkspace("owned update");
    expect(invoke(workspace, "--write").code).toBe(0);
    makeInstalledArtifactOutdated(workspace);
    expect(invoke(workspace, "--status").json.artifact.state).toBe("unknown");

    const update = invoke(workspace, "--write");

    expect(update.code).toBe(1);
    expect(update.json.blocked_reasons).toContain("installed_artifact_unknown");
  });

  it("updates only a generation listed by the bundled trusted-manifest allowlist", () => {
    const workspace = makeWorkspace("trusted update");
    expect(invoke(workspace, "--write").code).toBe(0);
    makeInstalledArtifactOutdated(workspace);
    const target = path.join(workspace, ".opencode", "sgg-unity-presentation");
    const trustedArtifactRoot = createTrustedBundledArtifact(target);
    expect(invoke(workspace, "--status", trustedArtifactRoot).json.artifact.state).toBe("outdated");

    const update = invoke(workspace, "--write", trustedArtifactRoot);

    expect(update.code).toBe(0);
    expect(update.json).toMatchObject({ artifact: { state: "current", installed_version: "2.0.0" }, applied: true });
    expect(update.json.changes).toContain("update_owned_artifact");
  });

  it("migrates the exact known 1.0.0 three-file artifact and legacy entry atomically", () => {
    const workspace = makeWorkspace("known old artifact");
    const target = path.join(workspace, ".opencode", "sgg-unity-presentation");
    fs.mkdirSync(path.dirname(target));
    copyBundledArtifact(target, legacyArtifactRoot);
    const knownHash = "bb8319e8aba5bf8ada436ea95c5f1ba47324fc9bba7044630394c4aea6cdad5c";
    expect(sha256(fs.readFileSync(path.join(target, "sgg-plugin-manifest.json")))).toBe(knownHash);
    const configPath = writeConfig(workspace, '{ "plugin": ["unrelated", "./.opencode/sgg-unity-presentation"] }\n');
    expect(invoke(workspace, "--status").json.artifact).toMatchObject({ state: "outdated", installed_version: "1.0.0" });
    const result = invoke(workspace, "--write");
    expect(result.error).toBe("");
    expect(result.code).toBe(0);
    expect(result.json.changes).toEqual(["update_owned_artifact", "migrate_owned_legacy_config_entry"]);
    expect(result.json.artifact).toMatchObject({ state: "current", installed_version: "2.0.0" });
    expect(parse(fs.readFileSync(configPath, "utf8"))).toEqual({ plugin: ["unrelated"], plugins: ["./.opencode/sgg-unity-presentation"] });
    expect(JSON.parse(fs.readFileSync(path.join(target, "package.json"), "utf8")).exports).toEqual({ ".": "./server.js" });
  });

  it("rejects a modified known legacy artifact despite its trusted manifest hash", () => {
    const workspace = makeWorkspace("tampered old artifact");
    const target = path.join(workspace, ".opencode", "sgg-unity-presentation");
    fs.mkdirSync(path.dirname(target));
    copyBundledArtifact(target, legacyArtifactRoot);
    fs.appendFileSync(path.join(target, "server.js"), "// modified\n");
    writeConfig(workspace, '{ "plugin": ["./.opencode/sgg-unity-presentation"] }\n');
    expect(invoke(workspace, "--write").json.blocked_reasons).toContain("installed_artifact_modified");
  });

  it("rolls the exact legacy artifact and its config back when native migration cannot commit", () => {
    const workspace = makeWorkspace("legacy rollback");
    const target = path.join(workspace, ".opencode", "sgg-unity-presentation");
    fs.mkdirSync(path.dirname(target));
    copyBundledArtifact(target, legacyArtifactRoot);
    const source = '{ "plugin": [["./.opencode/sgg-unity-presentation", { "serverPrefixes": ["unity-dev"] }]] }\n';
    const configPath = writeConfig(workspace, source);
    const original = fs.linkSync.bind(fs);
    vi.spyOn(fs, "linkSync").mockImplementation((from, to) => {
      if (to === configPath) throw new Error("injected migration commit failure");
      return original(from, to);
    });
    const result = invoke(workspace, "--write");
    expect(result.code).toBe(1);
    expect(fs.readFileSync(configPath, "utf8")).toBe(source);
    for (const name of ["package.json", "server.js", "sgg-plugin-manifest.json"]) {
      expect(fs.readFileSync(path.join(target, name))).toEqual(fs.readFileSync(path.join(legacyArtifactRoot, name)));
    }
  });

  it("removes only the owned artifact and exact config entry", () => {
    const workspace = makeWorkspace("remove");
    const configPath = writeConfig(workspace, `{
  // keep remove comment
  "plugin": ["custom-plugin"]
}\n`);
    expect(invoke(workspace, "--write").code).toBe(0);

    const result = invoke(workspace, "--remove");
    const source = fs.readFileSync(configPath, "utf8");
    const config = parse(source) as { plugin: string[] };

    expect(result.code).toBe(0);
    expect(result.json).toMatchObject({ artifact: { state: "absent" }, config: { state: "not_configured", owned_entries: 0 }, applied: true, restart_required: true });
    expect(fs.existsSync(path.join(workspace, ".opencode", "sgg-unity-presentation"))).toBe(false);
    expect(config.plugin).toEqual(["custom-plugin"]);
    expect(source).toContain("// keep remove comment");
  });

  it("rolls artifact and config back when the final config replacement fails", () => {
    const workspace = makeWorkspace("rollback");
    expect(invoke(workspace, "--write").code).toBe(0);
    makeInstalledArtifactOutdated(workspace);
    const configPath = path.join(workspace, "opencode.jsonc");
    fs.writeFileSync(configPath, "{\n  \"plugin\": [\"custom-plugin\"]\n}\n");
    const trustedArtifactRoot = createTrustedBundledArtifact(path.join(workspace, ".opencode", "sgg-unity-presentation"));
    const serverPath = path.join(workspace, ".opencode", "sgg-unity-presentation", "server.js");
    const oldServer = fs.readFileSync(serverPath);
    const originalLink = fs.linkSync.bind(fs);
    vi.spyOn(fs, "linkSync").mockImplementation((from, to) => {
      if (String(from).includes("presentation-config-") && String(from).endsWith(".tmp") && to === configPath) throw new Error("injected replacement failure");
      return originalLink(from, to);
    });

    const result = invoke(workspace, "--write", trustedArtifactRoot);

    expect(result.code).toBe(1);
    expect(result.error).toContain("presentation transaction failed");
    expect(fs.readFileSync(serverPath).equals(oldServer)).toBe(true);
    expect(parse(fs.readFileSync(configPath, "utf8"))).toEqual({ plugin: ["custom-plugin"] });
    expect(fs.readdirSync(path.join(workspace, ".opencode")).filter((name) => name.includes("presentation-") && name !== "sgg-unity-presentation")).toEqual([]);
  });

  it("fails closed for malformed config and invalid owned tuple entries", () => {
    const malformed = makeWorkspace("malformed config");
    writeConfig(malformed, "{ private config");
    const malformedResult = invoke(malformed, "--write");
    expect(malformedResult.code).toBe(1);
    expect(malformedResult.json.blocked_reasons).toContain("opencode_config_invalid");

    const tuple = makeWorkspace("tuple config");
    writeConfig(tuple, "{\n  \"plugin\": [[\"./.opencode/sgg-unity-presentation\", false]]\n}\n");
    const tupleResult = invoke(tuple, "--write");
    expect(tupleResult.code).toBe(1);
    expect(tupleResult.json.blocked_reasons).toContain("owned_plugin_entry_conflict");
  });

  it("blocks non-owned path aliases that resolve inside the managed artifact", () => {
    const workspace = makeWorkspace("path aliases");
    writeConfig(workspace, `{
  "plugin": [
    "./.opencode/sgg-unity-presentation/",
    "./.opencode/sgg-unity-presentation/server.js"
  ]
}\n`);

    const result = invoke(workspace, "--write");

    expect(result.code).toBe(1);
    expect(result.json.blocked_reasons).toContain("owned_plugin_entry_conflict");
    expect(fs.existsSync(path.join(workspace, ".opencode", "sgg-unity-presentation"))).toBe(false);
  });

  it.skipIf(process.platform === "win32")("classifies dangling config and artifact symlinks as conflicts instead of absent", () => {
    const configWorkspace = makeWorkspace("dangling config link");
    const configRoot = configWorkspace;
    fs.symlinkSync(path.join(configRoot, "missing.jsonc"), path.join(configRoot, "opencode.jsonc"));
    const configResult = invoke(configWorkspace, "--write");
    expect(configResult.code).toBe(1);
    expect(configResult.json.blocked_reasons).toContain("opencode_config_conflict");

    const artifactWorkspace = makeWorkspace("dangling artifact link");
    writeConfig(artifactWorkspace, "{}\n");
    const artifactLink = path.join(artifactWorkspace, ".opencode", "sgg-unity-presentation");
    fs.mkdirSync(path.dirname(artifactLink));
    fs.symlinkSync(path.join(artifactWorkspace, "missing-artifact"), artifactLink);
    const artifactResult = invoke(artifactWorkspace, "--write");
    expect(artifactResult.code).toBe(1);
    expect(artifactResult.json.blocked_reasons).toContain("installed_artifact_unknown");
    expect(fs.lstatSync(artifactLink).isSymbolicLink()).toBe(true);
  });

  it("durably rolls back an interrupted artifact commit before retrying", () => {
    const workspace = makeWorkspace("interrupted rollback");
    expect(invoke(workspace, "--write").code).toBe(0);
    makeInstalledArtifactOutdated(workspace);
    const root = path.join(workspace, ".opencode");
    const target = path.join(root, "sgg-unity-presentation");
    const configPath = path.join(workspace, "opencode.jsonc");
    fs.writeFileSync(configPath, "{\n  \"plugin\": [\"custom-plugin\"]\n}\n");
    const trustedArtifactRoot = createTrustedBundledArtifact(target);
    const originalFingerprint = installedArtifactFingerprint(target);
    const originalConfigFingerprint = sha256(fs.readFileSync(configPath));
    const nonce = "11111111-1111-4111-8111-111111111111";
    const backup = path.join(root, `.sgg-unity-presentation-backup-${nonce}`);
    fs.renameSync(target, backup);
    copyBundledArtifact(target, trustedArtifactRoot);
    fs.writeFileSync(path.join(root, ".sgg-unity-presentation-transaction.json"), `${JSON.stringify({
      schema_version: 2,
      nonce,
      artifact_operation: "install",
      artifact_original_state: "outdated",
      artifact_original_fingerprint: originalFingerprint,
      config_file: "opencode.jsonc",
      config_scope: "workspace_root",
      config_original_exists: true,
      config_original_fingerprint: originalConfigFingerprint,
      config_candidate_fingerprint: "a".repeat(64),
    })}\n`);
    const originalRename = fs.renameSync.bind(fs);
    let restoredBeforeRetry = false;
    vi.spyOn(fs, "renameSync").mockImplementation((from, to) => {
      if (from === backup && to === target) restoredBeforeRetry = true;
      return originalRename(from, to);
    });

    const result = invoke(workspace, "--write", trustedArtifactRoot);

    expect(result.code).toBe(0);
    expect(restoredBeforeRetry).toBe(true);
    expect(result.json).toMatchObject({ artifact: { state: "current" }, config: { state: "configured" }, recovery: "rolled_back", applied: true });
    expect(fs.existsSync(path.join(root, ".sgg-unity-presentation-transaction.json"))).toBe(false);
  });

  it("finalizes an interrupted transaction whose desired generation was fully committed", () => {
    const workspace = makeWorkspace("interrupted finalize");
    expect(invoke(workspace, "--write").code).toBe(0);
    const root = path.join(workspace, ".opencode");
    const configPath = path.join(workspace, "opencode.jsonc");
    const nonce = "22222222-2222-4222-8222-222222222222";
    fs.writeFileSync(path.join(root, ".sgg-unity-presentation-transaction.json"), `${JSON.stringify({
      schema_version: 2,
      nonce,
      artifact_operation: "install",
      artifact_original_state: "absent",
      artifact_original_fingerprint: "absent",
      config_file: "opencode.jsonc",
      config_scope: "workspace_root",
      config_original_exists: false,
      config_original_fingerprint: "missing",
      config_candidate_fingerprint: sha256(fs.readFileSync(configPath)),
    })}\n`);

    const result = invoke(workspace, "--write");

    expect(result.code).toBe(0);
    expect(result.json).toMatchObject({ changes: ["finalize_interrupted_transaction"], recovery: "finalized", applied: true, restart_required: true, artifact: { state: "current" }, config: { state: "configured" } });
    expect(fs.existsSync(path.join(root, ".sgg-unity-presentation-transaction.json"))).toBe(false);
  });

  it("restores a validated config backup when a crash left the target absent", () => {
    const workspace = makeWorkspace("interrupted config gap");
    expect(invoke(workspace, "--write").code).toBe(0);
    const root = path.join(workspace, ".opencode");
    const target = path.join(root, "sgg-unity-presentation");
    const configPath = path.join(workspace, "opencode.jsonc");
    const originalConfig = "{\n  // restore me\n  \"plugin\": [\"custom-plugin\"]\n}\n";
    fs.writeFileSync(configPath, originalConfig);
    const nonce = "66666666-6666-4666-8666-666666666666";
    const configBackup = path.join(workspace, `.sgg-unity-presentation-config-${nonce}.bak`);
    fs.renameSync(configPath, configBackup);
    fs.writeFileSync(path.join(root, ".sgg-unity-presentation-transaction.json"), `${JSON.stringify({
      schema_version: 2,
      nonce,
      artifact_operation: "none",
      artifact_original_state: "current",
      artifact_original_fingerprint: installedArtifactFingerprint(target),
      config_file: "opencode.jsonc",
      config_scope: "workspace_root",
      config_original_exists: true,
      config_original_fingerprint: sha256(Buffer.from(originalConfig)),
      config_candidate_fingerprint: "b".repeat(64),
    })}\n`);

    const result = invoke(workspace, "--remove");

    expect(result.code).toBe(0);
    expect(result.json.recovery).toBe("rolled_back");
    expect(fs.readFileSync(configPath, "utf8")).toBe(originalConfig);
    expect(fs.existsSync(configBackup)).toBe(false);
    expect(fs.existsSync(target)).toBe(false);
  });

  it("does not delete a config authorized only by a forged recovery record", () => {
    const workspace = makeWorkspace("forged recovery");
    const configPath = writeConfig(workspace, "{\n  \"theme\": \"private-theme\"\n}\n");
    const root = path.join(workspace, ".opencode");
    fs.mkdirSync(root);
    fs.writeFileSync(path.join(root, ".sgg-unity-presentation-transaction.json"), `${JSON.stringify({
      schema_version: 2,
      nonce: "33333333-3333-4333-8333-333333333333",
      artifact_operation: "install",
      artifact_original_state: "absent",
      artifact_original_fingerprint: "absent",
      config_file: "opencode.jsonc",
      config_scope: "workspace_root",
      config_original_exists: false,
      config_original_fingerprint: "missing",
      config_candidate_fingerprint: sha256(fs.readFileSync(configPath)),
    })}\n`);

    const result = invoke(workspace, "--write");

    expect(result.code).toBe(1);
    expect(result.error).toContain("cannot be removed without an original backup");
    expect(fs.readFileSync(configPath, "utf8")).toContain("private-theme");
  });

  it("reclaims a dead complete lock but refuses live and malformed locks", () => {
    const staleWorkspace = makeWorkspace("stale lock");
    const staleRoot = path.join(staleWorkspace, ".opencode");
    fs.mkdirSync(staleRoot);
    const staleLock = path.join(staleRoot, ".sgg-unity-presentation.lock");
    fs.writeFileSync(staleLock, `${JSON.stringify({ schema_version: 1, pid: 987654321, nonce: "44444444-4444-4444-8444-444444444444" })}\n`);
    const originalKill = process.kill.bind(process);
    vi.spyOn(process, "kill").mockImplementation((pid, signal) => {
      if (pid === 987654321) throw Object.assign(new Error("dead"), { code: "ESRCH" });
      return originalKill(pid, signal);
    });
    expect(invoke(staleWorkspace, "--write").code).toBe(0);
    expect(fs.existsSync(staleLock)).toBe(false);
    vi.restoreAllMocks();

    const liveWorkspace = makeWorkspace("live lock");
    const liveRoot = path.join(liveWorkspace, ".opencode");
    fs.mkdirSync(liveRoot);
    fs.writeFileSync(path.join(liveRoot, ".sgg-unity-presentation.lock"), `${JSON.stringify({ schema_version: 1, pid: process.pid, nonce: "55555555-5555-4555-8555-555555555555" })}\n`);
    const live = invoke(liveWorkspace, "--write");
    expect(live.code).toBe(1);
    expect(live.error).toContain("already running");
    expect(fs.existsSync(path.join(liveRoot, "sgg-unity-presentation"))).toBe(false);

    const malformedWorkspace = makeWorkspace("malformed lock");
    const malformedRoot = path.join(malformedWorkspace, ".opencode");
    fs.mkdirSync(malformedRoot);
    fs.writeFileSync(path.join(malformedRoot, ".sgg-unity-presentation.lock"), "partial-lock");
    const malformed = invoke(malformedWorkspace, "--write");
    expect(malformed.code).toBe(1);
    expect(malformed.error).toContain("mutation lock is invalid");
    expect(fs.existsSync(path.join(malformedRoot, "sgg-unity-presentation"))).toBe(false);
  });

  it.skipIf(process.platform === "win32")("sanitizes control characters in human-readable workspace output", () => {
    const workspace = makeWorkspace("line\n\u001b[31mred");
    const output: string[] = [];
    const code = runSetupCli(["opencode-presentation", "--workspace", workspace, "--status"], {
      presentationArtifactRoot: artifactRoot,
      stdout: (text) => output.push(text),
    });
    const text = output.join("");
    expect(code).toBe(0);
    expect(text).not.toContain("\u001b");
    expect(text.match(/workspace:/g)).toHaveLength(1);
    expect(text).not.toContain("line\n");
  });
});

function makeWorkspace(name: string): string {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-opencode-presentation-"));
  tempDirs.push(root);
  const workspace = path.join(root, name);
  fs.mkdirSync(workspace);
  return workspace;
}

function writeConfig(workspace: string, source: string): string {
  const configPath = path.join(workspace, "opencode.jsonc");
  fs.writeFileSync(configPath, source);
  return configPath;
}

function invoke(workspace: string, action: "--status" | "--dry-run" | "--write" | "--remove", sourceArtifactRoot = artifactRoot) {
  const output: string[] = [];
  const errors: string[] = [];
  const code = runSetupCli(["opencode-presentation", "--workspace", workspace, action, "--json"], {
    presentationArtifactRoot: sourceArtifactRoot,
    stdout: (text) => output.push(text),
    stderr: (text) => errors.push(text),
  });
  const text = output.join("");
  return {
    code,
    output: text,
    error: errors.join(""),
    json: text ? JSON.parse(text) as Record<string, any> : {},
  };
}

function makeInstalledArtifactOutdated(workspace: string): void {
  const target = path.join(workspace, ".opencode", "sgg-unity-presentation");
  const packagePath = path.join(target, "package.json");
  const serverPath = path.join(target, "server.js");
  const manifestPath = path.join(target, "sgg-plugin-manifest.json");
  const packageJson = JSON.parse(fs.readFileSync(packagePath, "utf8"));
  packageJson.version = "0.9.0";
  const packageBytes = Buffer.from(`${JSON.stringify(packageJson, null, 2)}\n`);
  const serverBytes = Buffer.concat([fs.readFileSync(serverPath), Buffer.from("// reviewed old generation\n")]);
  const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
  manifest.version = "0.9.0";
  manifest.files = [
    { path: "package.json", bytes: packageBytes.length, sha256: sha256(packageBytes) },
    { path: "server.js", bytes: serverBytes.length, sha256: sha256(serverBytes) },
  ];
  fs.writeFileSync(packagePath, packageBytes);
  fs.writeFileSync(serverPath, serverBytes);
  fs.writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);
}

function sha256(bytes: Buffer): string {
  return createHash("sha256").update(bytes).digest("hex");
}

function installedArtifactFingerprint(target: string): string {
  const names = fs.readdirSync(target).sort();
  const hash = createHash("sha256");
  hash.update(names.join("\0"));
  for (const name of ["package.json", "server.js", "sgg-plugin-manifest.json"]) {
    hash.update("\0");
    hash.update(name);
    hash.update(fs.readFileSync(path.join(target, name)));
  }
  return hash.digest("hex");
}

function copyBundledArtifact(target: string, sourceArtifactRoot = artifactRoot): void {
  fs.mkdirSync(target);
  for (const name of ["package.json", "server.js", "sgg-plugin-manifest.json"]) {
    fs.copyFileSync(path.join(sourceArtifactRoot, name), path.join(target, name));
  }
}

function createTrustedBundledArtifact(installedTarget: string): string {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-trusted-presentation-"));
  tempDirs.push(root);
  for (const name of ["package.json", "server.js", "sgg-plugin-manifest.json"]) fs.copyFileSync(path.join(artifactRoot, name), path.join(root, name));
  const manifestPath = path.join(root, "sgg-plugin-manifest.json");
  const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
  manifest.trusted_previous_manifest_sha256 = [sha256(fs.readFileSync(path.join(installedTarget, "sgg-plugin-manifest.json")))];
  fs.writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);
  return root;
}
