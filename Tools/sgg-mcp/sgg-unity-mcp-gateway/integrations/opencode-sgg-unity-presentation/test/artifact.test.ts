import crypto from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

import { describe, expect, test } from "vitest";

import {
  createOpenCodePresentationArtifact,
  OPENCODE_PRESENTATION_MANIFEST,
  unexpectedOpenCodePresentationFiles,
  validateOpenCodePresentationArtifact,
// @ts-expect-error The directly executable Node helper has no separate declaration file.
} from "../../../scripts/opencode-presentation-artifact.mjs";

const integrationRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const repositoryRoot = path.resolve(integrationRoot, "../..");

function sha256(content: Uint8Array): string {
  return crypto.createHash("sha256").update(content).digest("hex");
}

describe("OpenCode presentation artifact", () => {
  test("builds a deterministic fixed file set with self-consistent hashes", async () => {
    const first = await createOpenCodePresentationArtifact(repositoryRoot);
    const second = await createOpenCodePresentationArtifact(repositoryRoot);

    expect([...first.files.keys()]).toEqual(["package.json", "server.js", OPENCODE_PRESENTATION_MANIFEST]);
    expect([...second.files.entries()]).toEqual([...first.files.entries()]);
    expect(first.manifest).toMatchObject({
      schema_version: 1,
      plugin_id: "sgg-unity.presentation",
      version: "2.0.0",
      opencode_range: ">=2.0.25 <3",
      trusted_previous_manifest_sha256: ["bb8319e8aba5bf8ada436ea95c5f1ba47324fc9bba7044630394c4aea6cdad5c"],
    });
    for (const file of first.manifest.files) {
      const content = first.files.get(file.path);
      expect(content).toBeDefined();
      expect(file.bytes).toBe(content?.length);
      expect(file.sha256).toBe(sha256(content ?? new Uint8Array()));
    }
  });

  test("builds a native main package loadable with the external released OpenCode API", async () => {
    const expected = await createOpenCodePresentationArtifact(repositoryRoot);
    // Stage under the package to use the normal host dependency resolution, not a bundled API copy.
    const stage = fs.mkdtempSync(path.join(integrationRoot, ".artifact-test-"));
    try {
      for (const [relativePath, content] of expected.files) fs.writeFileSync(path.join(stage, relativePath), content);
      const packageJson = JSON.parse(fs.readFileSync(path.join(stage, "package.json"), "utf8"));
      expect(packageJson.exports).toEqual({ ".": "./server.js" });
      expect(packageJson.dependencies).toEqual({ "@opencode/plugin": "2.0.25" });
      const server = fs.readFileSync(path.join(stage, "server.js"), "utf8");
      expect(server).toContain('from "@opencode/plugin"');
      const loaded = await import(pathToFileURL(path.join(stage, packageJson.exports["."])).href);
      expect(loaded.default?.id).toBe("sgg-unity.presentation");
      expect(loaded.default?.setup).toBeTypeOf("function");
      expect(loaded.default).not.toHaveProperty("server");
    } finally {
      fs.rmSync(stage, { recursive: true, force: true });
    }
  }, 30_000);

  test("rejects legacy-only entrypoints and incompatible/missing host dependencies", async () => {
    const original = await createOpenCodePresentationArtifact(repositoryRoot);
    for (const patch of [
      { exports: { "./server": "./server.js" } },
      { exports: { ".": "./server.js", "./server": "./server.js" } },
      { dependencies: {} },
      { dependencies: { "@opencode/plugin": "latest" } },
      { engines: { opencode: ">=1.18.21 <2" } },
    ]) {
      const artifact = { files: new Map(original.files), manifest: structuredClone(original.manifest) };
      const packageJson = { ...JSON.parse((artifact.files.get("package.json") as Buffer).toString("utf8")), ...patch };
      const bytes = Buffer.from(`${JSON.stringify(packageJson, null, 2)}\n`);
      artifact.files.set("package.json", bytes);
      const entry = artifact.manifest.files.find((file: { path: string }) => file.path === "package.json");
      entry.bytes = bytes.length;
      entry.sha256 = sha256(bytes);
      expect(() => validateOpenCodePresentationArtifact(artifact)).toThrow("invalid OpenCode presentation package contract");
    }
  });

  test("rejects oversized generated files and undeclared meta entries", async () => {
    const artifact = await createOpenCodePresentationArtifact(repositoryRoot);
    const oversized = {
      files: new Map(artifact.files),
      manifest: structuredClone(artifact.manifest),
    };
    const server = Buffer.alloc(2 * 1024 * 1024 + 1);
    oversized.files.set("server.js", server);
    const entry = oversized.manifest.files.find((file: { path: string }) => file.path === "server.js");
    entry.bytes = server.length;
    entry.sha256 = sha256(server);
    expect(() => validateOpenCodePresentationArtifact(oversized)).toThrow("invalid OpenCode presentation artifact server.js");

    const root = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-artifact-meta-test-"));
    try {
      const target = path.join(root, "opencode-presentation");
      fs.mkdirSync(target);
      fs.writeFileSync(path.join(target, "evil.meta"), "undeclared\n");
      expect(unexpectedOpenCodePresentationFiles(root)).toEqual(["evil.meta"]);
    } finally {
      fs.rmSync(root, { recursive: true, force: true });
    }
  });
});
