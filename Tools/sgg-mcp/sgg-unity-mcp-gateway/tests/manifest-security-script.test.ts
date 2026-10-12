import { spawnSync } from "node:child_process";
import path from "node:path";
import { pathToFileURL } from "node:url";

import { describe, expect, it } from "vitest";

const root = process.cwd();
const scriptPath = path.join(root, "scripts", "check-manifest-security.mjs");
const examplesRoot = path.join(root, "examples", "extensions");

type ValidationResult = { ok: boolean; failures: string[] };
type ManifestSecurityModule = {
  validateManifestFile: (filePath: string, options?: { relativePath?: string }) => ValidationResult;
  validateRepository: (options?: { root?: string }) => ValidationResult;
};

describe("manifest security check script", () => {
  it("passes current repository fixtures without Unity batchmode", async () => {
    const module = await loadScriptModule();
    const result = module.validateRepository({ root });
    const cli = spawnSync(process.execPath, [scriptPath], { cwd: root, encoding: "utf8" });

    expect(result).toEqual({ ok: true, failures: [] });
    expect(cli.status).toBe(0);
    expect(cli.stdout).toContain("[check-manifest-security] ok");
    expect(`${cli.stdout}${cli.stderr}`).not.toMatch(/batchmode|Unity Editor/i);
  }, 15000);

  it("validates the good manifest fixture cleanly", async () => {
    const module = await loadScriptModule();
    const result = module.validateManifestFile(
      path.join(examplesRoot, "good-manifest", "mcp.commands.example.json"),
      { relativePath: "examples/extensions/good-manifest/mcp.commands.example.json" },
    );

    expect(result).toEqual({ ok: true, failures: [] });
  });

  it.each([
    ["bad-missing-risk", "must include risk/idempotency/timeout/schema metadata"],
    ["bad-builtin-impersonation", "must not impersonate com.sungeargames.unity-mcp"],
    ["bad-unbounded-output", "text_artifact result_shape must include output_limit.max_chars"],
  ])("rejects %s with the intended reason", async (directory, reason) => {
    const module = await loadScriptModule();
    const relativePath = `examples/extensions/${directory}/mcp.commands.bad.json`;
    const result = module.validateManifestFile(path.join(root, relativePath), { relativePath });

    expect(result.ok).toBe(false);
    expect(result.failures.join("\n")).toContain(reason);
  });
});

async function loadScriptModule(): Promise<ManifestSecurityModule> {
  return await import(pathToFileURL(scriptPath).href) as ManifestSecurityModule;
}
