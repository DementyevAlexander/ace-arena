import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it } from "vitest";

import { main } from "../src/cli.js";
import { PolicyLoadError } from "../src/policy/policy-manager.js";

const dirs: string[] = [];

afterEach(() => {
  for (const dir of dirs.splice(0)) fs.rmSync(dir, { recursive: true, force: true });
});

describe("configured policy startup", () => {
  it.each([
    ["missing", undefined, "policy_reload_failed", "missing"],
    ["invalid", "{ private startup policy", "policy_invalid", "invalid_json"],
  ] as const)("blocks Gateway startup for a %s policy without leaking details", async (_name, content, code, kind) => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-policy-startup-"));
    dirs.push(dir);
    const policyPath = path.join(dir, "private-policy-name.json");
    if (content !== undefined) fs.writeFileSync(policyPath, content);

    let thrown: unknown;
    try {
      await main(["--stdio", "--config", path.resolve("config/gateway.example.json"), "--policy", policyPath], {});
    } catch (error) {
      thrown = error;
    }

    expect(thrown).toBeInstanceOf(PolicyLoadError);
    expect(thrown).toMatchObject({ code, kind });
    expect(String(thrown)).not.toContain(policyPath);
    expect(String(thrown)).not.toContain("private startup policy");
  });
});
