#!/usr/bin/env node

import { spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { build } from "esbuild";
import { fileBytesMismatch, LEGAL_SIDECAR_META, LEGAL_SIDECAR_PATH, normalizeLegalSidecar, renderGatewayRuntimeManifest } from "./gateway-runtime-manifest.mjs";
import { createGeneratedArtifacts, NOTICE_META, NOTICE_PATH, RUNTIME_RELATIVE, SBOM_META, SBOM_PATH } from "./runtime-compliance.mjs";
import { gatewayRuntimeBuildOptions } from "./runtime-build-options.mjs";
import { createOpenCodePresentationArtifact, OPENCODE_PRESENTATION_RUNTIME_RELATIVE, unexpectedOpenCodePresentationFiles, writeOpenCodePresentationArtifact } from "./opencode-presentation-artifact.mjs";

const root = process.cwd();
const packageJson = JSON.parse(fs.readFileSync(path.join(root, "package.json"), "utf8"));
const runtimeRoot = path.join(root, RUNTIME_RELATIVE);
const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-unity-mcp-runtime-"));
const tmpGatewayCliPath = path.join(tmpRoot, "gateway-cli.mjs");
let failed = false;

try {
  await build(gatewayRuntimeBuildOptions(root, tmpGatewayCliPath));
  stripTrailingHorizontalWhitespace(tmpGatewayCliPath);
  normalizeLegalSidecar(tmpGatewayCliPath);

  const validation = spawnSync(process.execPath, [tmpGatewayCliPath, "--help"], {
    cwd: tmpRoot,
    encoding: "utf8",
    timeout: 20000,
  });
  if (validation.status !== 0) {
    fail(`temporary bundled gateway-cli.mjs failed --help validation: ${trimForLog(validation.stderr || validation.stdout)}`);
  }

  const workerValidation = spawnSync(process.execPath, [path.join(root, "scripts", "check-test-run-publication-worker.mjs"), tmpGatewayCliPath], {
    cwd: root, encoding: "utf8", timeout: 30000,
  });
  if (workerValidation.status !== 0) {
    fail(`temporary bundled operation worker failed validation: ${trimForLog(workerValidation.stderr || workerValidation.stdout)}`);
  }

  compareBytes("gateway-cli.mjs", fs.readFileSync(tmpGatewayCliPath));
  compareBytes(LEGAL_SIDECAR_PATH, fs.readFileSync(path.join(tmpRoot, LEGAL_SIDECAR_PATH)));

  const generated = await createGeneratedArtifacts(root, tmpGatewayCliPath);
  fs.writeFileSync(path.join(tmpRoot, NOTICE_PATH), generated.notices);
  fs.writeFileSync(path.join(tmpRoot, SBOM_PATH), generated.sbom);
  compareCompliance(NOTICE_PATH, generated.notices, NOTICE_META);
  compareCompliance(SBOM_PATH, generated.sbom, SBOM_META);
  compareBytes(`${LEGAL_SIDECAR_PATH}.meta`, Buffer.from(LEGAL_SIDECAR_META));

  const presentation = await createOpenCodePresentationArtifact(root);
  writeOpenCodePresentationArtifact(tmpRoot, presentation);
  for (const [relativePath, content] of presentation.files) {
    compareBytes(path.join(OPENCODE_PRESENTATION_RUNTIME_RELATIVE, relativePath), content);
  }
  const unexpectedPresentationFiles = unexpectedOpenCodePresentationFiles(runtimeRoot);
  if (unexpectedPresentationFiles.length) {
    fail(`unexpected OpenCode presentation artifact files: ${unexpectedPresentationFiles.join(", ")}`);
  }

  const expectedManifest = renderGatewayRuntimeManifest({ root, runtimeRoot: tmpRoot, version: packageJson.version });
  compareBytes("gateway-runtime.json", Buffer.from(expectedManifest));

  for (const [sourceRelative, bundledRelative] of [
    ["config/gateway.example.json", "config/gateway.example.json"],
    ["config/policy.example.json", "config/policy.example.json"],
  ]) {
    compareBytes(bundledRelative, readRequired(path.join(root, sourceRelative), sourceRelative));
  }
} catch (error) {
  fail(error.stack ?? error.message);
} finally {
  fs.rmSync(tmpRoot, { recursive: true, force: true });
}

if (failed) process.exitCode = 1;
else console.log("[check-gateway-runtime] ok");

function compareCompliance(relativePath, content, meta) {
  const expected = Buffer.from(content);
  compareBytes(relativePath, expected);
  compareRootBytes(relativePath, expected);
  compareBytes(`${relativePath}.meta`, Buffer.from(meta));
}

function compareBytes(relativePath, expected) {
  const filePath = path.join(runtimeRoot, relativePath);
  const mismatch = fileBytesMismatch(filePath, expected);
  if (mismatch === "missing") {
    fail(`missing ${RUNTIME_RELATIVE}/${relativePath}`);
  } else if (mismatch === "drift") {
    fail(`${RUNTIME_RELATIVE}/${relativePath} is stale; run npm run build:gateway-runtime`);
  }
}

function compareRootBytes(relativePath, expected) {
  const filePath = path.join(root, relativePath);
  const mismatch = fileBytesMismatch(filePath, expected);
  if (mismatch === "missing") fail(`missing ${relativePath}`);
  else if (mismatch === "drift") fail(`${relativePath} is stale; run npm run build:gateway-runtime`);
}

function readRequired(filePath, displayPath) {
  if (!fs.existsSync(filePath)) throw new Error(`missing ${displayPath}`);
  return fs.readFileSync(filePath);
}

function fail(message) {
  console.error(`[check-gateway-runtime] ${message}`);
  failed = true;
}

function trimForLog(text) {
  const value = String(text || "").trim();
  return value.length <= 1200 ? value : `${value.slice(0, 1200)}...`;
}

function stripTrailingHorizontalWhitespace(filePath) {
  const text = fs.readFileSync(filePath, "utf8");
  const normalized = text.replace(/[ \t]+$/gm, "");
  if (normalized !== text) fs.writeFileSync(filePath, normalized, "utf8");
}
