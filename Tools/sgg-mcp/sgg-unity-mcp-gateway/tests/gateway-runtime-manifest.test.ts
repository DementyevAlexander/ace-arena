import crypto from "node:crypto";
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { describe, expect, it } from "vitest";

import {
  createGatewayRuntimeManifest,
  fileBytesMismatch,
  LEGAL_SIDECAR_PATH,
  normalizeLegalSidecar,
// @ts-expect-error The directly executable Node helper has no separate declaration file.
} from "../scripts/gateway-runtime-manifest.mjs";

const root = process.cwd();
const bundledRuntimeRelative = "unity/Packages/com.sungeargames.unity-mcp/Editor/GatewayRuntime";

describe("gateway runtime manifest v3", () => {
  it("retains all bundled artifact hashes after a Windows-style Git checkout", () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-runtime-checkout-"));
    const repoRoot = path.join(tempRoot, "repo");
    const checkoutRoot = path.join(tempRoot, "checkout");
    const originalRuntimeRoot = path.join(root, bundledRuntimeRelative);
    const manifest = JSON.parse(fs.readFileSync(path.join(originalRuntimeRoot, "gateway-runtime.json"), "utf8"));
    const artifacts = Object.values(manifest.artifacts) as { path: string; sha256: string }[];
    const relativePaths = ["gateway-runtime.json", ...artifacts.map((artifact) => artifact.path)];

    try {
      fs.mkdirSync(repoRoot);
      fs.copyFileSync(path.join(root, ".gitattributes"), path.join(repoRoot, ".gitattributes"));
      const indexedPaths = relativePaths.map((relativePath) => {
        const gitPath = `${bundledRuntimeRelative}/${relativePath}`;
        const target = path.join(repoRoot, gitPath);
        fs.mkdirSync(path.dirname(target), { recursive: true });
        fs.copyFileSync(path.join(originalRuntimeRoot, relativePath), target);
        return gitPath;
      });
      runGit(repoRoot, ["init", "-q"]);
      runGit(repoRoot, ["-c", "core.autocrlf=false", "add", "--", ".gitattributes", ...indexedPaths]);
      runGit(repoRoot, ["-c", "core.autocrlf=true", "checkout-index", `--prefix=${checkoutRoot}${path.sep}`, "--", ...indexedPaths]);

      for (const artifact of artifacts) {
        const actual = crypto.createHash("sha256")
          .update(fs.readFileSync(path.join(checkoutRoot, bundledRuntimeRelative, artifact.path))).digest("hex");
        expect(actual, `${artifact.path} changed during Git checkout`).toBe(artifact.sha256);
      }
    } finally {
      fs.rmSync(tempRoot, { recursive: true, force: true });
    }
  });

  it("binds fixed artifact paths to their exact SHA-256 hashes", () => {
    withRuntimeFixture((runtimeRoot) => {
      const manifest = createGatewayRuntimeManifest({ root, runtimeRoot, version: "2026.7.11-21" });
      expect(manifest).toMatchObject({
        schema_version: 3,
        version: "2026.7.11-21",
        entrypoint: "gateway-cli.mjs",
        gateway_virtual_commands_count: 62,
        config: "config/gateway.example.json",
        policy: "config/policy.example.json",
      });
      expect(manifest.artifacts).toEqual({
        gateway_cli: artifact("gateway-cli.mjs", "cli\n"),
        legal_comments: artifact("gateway-cli.LEGAL.txt", "/*! legal */\n"),
        third_party_notices: artifact("THIRD_PARTY_NOTICES.md", "notices\n"),
        sbom: artifact("sbom.cdx.json", "{}\n"),
        opencode_presentation_manifest: artifact("opencode-presentation/sgg-plugin-manifest.json", "{}\n"),
        opencode_presentation_package: artifact("opencode-presentation/package.json", "{\"name\":\"presentation\"}\n"),
        opencode_presentation_server: artifact("opencode-presentation/server.js", "export default {}\n"),
      });

      for (const [artifactName, relativePath] of [
        ["gateway_cli", "gateway-cli.mjs"],
        ["legal_comments", "gateway-cli.LEGAL.txt"],
        ["third_party_notices", "THIRD_PARTY_NOTICES.md"],
        ["sbom", "sbom.cdx.json"],
        ["opencode_presentation_manifest", "opencode-presentation/sgg-plugin-manifest.json"],
        ["opencode_presentation_package", "opencode-presentation/package.json"],
        ["opencode_presentation_server", "opencode-presentation/server.js"],
      ] as const) {
        const original = fs.readFileSync(path.join(runtimeRoot, relativePath));
        expect(fileBytesMismatch(path.join(runtimeRoot, relativePath), original)).toBeUndefined();
        fs.appendFileSync(path.join(runtimeRoot, relativePath), "drift\n");
        expect(fileBytesMismatch(path.join(runtimeRoot, relativePath), original)).toBe("drift");
        expect(createGatewayRuntimeManifest({ root, runtimeRoot, version: "2026.7.11-21" }).artifacts[artifactName].sha256)
          .not.toBe(manifest.artifacts[artifactName].sha256);
        fs.writeFileSync(path.join(runtimeRoot, relativePath), original);
      }
    });
  });

  it("fails when a required compliance artifact is missing", () => {
    for (const relativePath of [
      "gateway-cli.mjs",
      "gateway-cli.LEGAL.txt",
      "THIRD_PARTY_NOTICES.md",
      "sbom.cdx.json",
      "opencode-presentation/sgg-plugin-manifest.json",
      "opencode-presentation/package.json",
      "opencode-presentation/server.js",
    ]) {
      withRuntimeFixture((runtimeRoot) => {
        fs.rmSync(path.join(runtimeRoot, relativePath));
        expect(fileBytesMismatch(path.join(runtimeRoot, relativePath), Buffer.alloc(0))).toBe("missing");
        expect(() => createGatewayRuntimeManifest({ root, runtimeRoot, version: "2026.7.11-21" }))
          .toThrow(`required runtime artifact is missing: ${relativePath}`);
      });
    }
  });

  it("normalizes actual esbuild naming and rejects missing or empty legal output", () => {
    const runtimeRoot = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-legal-sidecar-"));
    const cliPath = path.join(runtimeRoot, "gateway-cli.mjs");
    try {
      expect(() => normalizeLegalSidecar(cliPath)).toThrow("did not emit required legal comments sidecar");
      fs.writeFileSync(`${cliPath}.LEGAL.txt`, "");
      expect(() => normalizeLegalSidecar(cliPath)).toThrow("empty or contains no preserved legal comment");
      fs.writeFileSync(`${cliPath}.LEGAL.txt`, "/*! preserved */\n");
      expect(normalizeLegalSidecar(cliPath)).toBe(path.join(runtimeRoot, LEGAL_SIDECAR_PATH));
      expect(fs.readFileSync(path.join(runtimeRoot, LEGAL_SIDECAR_PATH), "utf8")).toBe("/*! preserved */\n");
    } finally {
      fs.rmSync(runtimeRoot, { recursive: true, force: true });
    }
  });

  it("keeps build and workflow compliance gates mandatory", () => {
    const buildSource = fs.readFileSync(path.join(root, "scripts/build-gateway-runtime.mjs"), "utf8");
    const presentationBuildSource = fs.readFileSync(path.join(root, "scripts/build-opencode-presentation.mjs"), "utf8");
    const checkSource = fs.readFileSync(path.join(root, "scripts/check-gateway-runtime.mjs"), "utf8");
    const packageJson = JSON.parse(fs.readFileSync(path.join(root, "package.json"), "utf8"));
    const checksWorkflow = fs.readFileSync(path.join(root, ".github/workflows/checks.yml"), "utf8");
    const releaseWorkflow = fs.readFileSync(path.join(root, ".github/workflows/release.yml"), "utf8");
    const workflowCheckSource = fs.readFileSync(path.join(root, "scripts/check-manual-workflow.mjs"), "utf8");

    expect(buildSource).toContain("await writeGeneratedArtifacts(root, gatewayCliPath)");
    expect(buildSource).toMatch(/await writeGeneratedArtifacts[\s\S]*renderGatewayRuntimeManifest/);
    expect(presentationBuildSource.indexOf("renderGatewayRuntimeManifest({ root, runtimeRoot, version: packageJson.version })"))
      .toBeGreaterThan(presentationBuildSource.indexOf("writeOpenCodePresentationArtifact(runtimeRoot, artifact)"));
    expect(checkSource).toContain("compareBytes(LEGAL_SIDECAR_PATH");
    expect(checkSource).toContain("compareCompliance(NOTICE_PATH");
    expect(checkSource).toContain("compareCompliance(SBOM_PATH");
    expect(checkSource).toContain('"check-test-run-publication-worker.mjs"');
    expect(checkSource).toContain("workerValidation.status !== 0");
    expect(packageJson.scripts["check:static"]).toBe("node scripts/check-profile.mjs static");
    expect(checksWorkflow).toContain("npm run check:gateway-runtime");
    expect(checksWorkflow).toContain("npm run check:notices");
    expect(workflowCheckSource).toMatch(/requiredChecksWorkflowCommands[\s\S]*npm run check:gateway-runtime/);
    expect(releaseWorkflow).toContain("npm run build:gateway-runtime\n      - run: npm run check:notices");
    expect(releaseWorkflow).toContain("git diff --exit-code -- THIRD_PARTY_NOTICES.md sbom.cdx.json");
  });
});

function runGit(cwd: string, args: string[]) {
  const result = spawnSync("git", args, { cwd, encoding: "utf8" });
  if (result.error || result.status !== 0) {
    throw new Error(`git ${args.slice(0, 3).join(" ")} failed: ${result.error?.message ?? result.stderr}`);
  }
}

function withRuntimeFixture(run: (runtimeRoot: string) => void) {
  const runtimeRoot = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-runtime-manifest-"));
  try {
    fs.writeFileSync(path.join(runtimeRoot, "gateway-cli.mjs"), "cli\n");
    fs.writeFileSync(path.join(runtimeRoot, "gateway-cli.LEGAL.txt"), "/*! legal */\n");
    fs.writeFileSync(path.join(runtimeRoot, "THIRD_PARTY_NOTICES.md"), "notices\n");
    fs.writeFileSync(path.join(runtimeRoot, "sbom.cdx.json"), "{}\n");
    fs.mkdirSync(path.join(runtimeRoot, "opencode-presentation"));
    fs.writeFileSync(path.join(runtimeRoot, "opencode-presentation/sgg-plugin-manifest.json"), "{}\n");
    fs.writeFileSync(path.join(runtimeRoot, "opencode-presentation/package.json"), "{\"name\":\"presentation\"}\n");
    fs.writeFileSync(path.join(runtimeRoot, "opencode-presentation/server.js"), "export default {}\n");
    run(runtimeRoot);
  } finally {
    fs.rmSync(runtimeRoot, { recursive: true, force: true });
  }
}

function artifact(relativePath: string, content: string) {
  return { path: relativePath, sha256: crypto.createHash("sha256").update(content).digest("hex") };
}
