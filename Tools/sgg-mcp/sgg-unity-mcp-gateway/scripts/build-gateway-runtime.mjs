#!/usr/bin/env node

import fs from "node:fs";
import path from "node:path";

import { build } from "esbuild";
import { LEGAL_SIDECAR_META, normalizeLegalSidecar, renderGatewayRuntimeManifest } from "./gateway-runtime-manifest.mjs";
import { createOpenCodePresentationArtifact, writeOpenCodePresentationArtifact } from "./opencode-presentation-artifact.mjs";
import { RUNTIME_RELATIVE, writeGeneratedArtifacts } from "./runtime-compliance.mjs";
import { gatewayRuntimeBuildOptions } from "./runtime-build-options.mjs";

const root = process.cwd();
const packageJson = JSON.parse(fs.readFileSync(path.join(root, "package.json"), "utf8"));
const runtimeRoot = path.join(root, RUNTIME_RELATIVE);
const configRoot = path.join(runtimeRoot, "config");
const gatewayCliPath = path.join(runtimeRoot, "gateway-cli.mjs");
const gatewayCliBuildPath = path.join(runtimeRoot, `.gateway-cli-build-${process.pid}.mjs`);
const gatewayCliLegalPath = `${gatewayCliPath}.LEGAL.txt`;
const gatewayCliBuildLegalPath = `${gatewayCliBuildPath}.LEGAL.txt`;

fs.mkdirSync(configRoot, { recursive: true });
for (const name of fs.readdirSync(runtimeRoot)) {
  if (/^\.gateway-cli-build-\d+\.mjs(?:\.LEGAL\.txt)?$/.test(name)) fs.rmSync(path.join(runtimeRoot, name), { force: true });
}
await build(gatewayRuntimeBuildOptions(root, gatewayCliBuildPath));
writeAfterSharingWindow(gatewayCliPath, fs.readFileSync(gatewayCliBuildPath));
writeAfterSharingWindow(gatewayCliLegalPath, fs.readFileSync(gatewayCliBuildLegalPath));
fs.rmSync(gatewayCliBuildPath, { force: true });
fs.rmSync(gatewayCliBuildLegalPath, { force: true });
stripTrailingHorizontalWhitespace(gatewayCliPath);
normalizeLegalSidecar(gatewayCliPath);

fs.copyFileSync(path.join(root, "config", "gateway.example.json"), path.join(configRoot, "gateway.example.json"));
fs.copyFileSync(path.join(root, "config", "policy.example.json"), path.join(configRoot, "policy.example.json"));
fs.copyFileSync(path.join(root, "scripts", "unity-editor-discovery.mjs"), path.join(runtimeRoot, "unity-editor-discovery.mjs"));
writeOpenCodePresentationArtifact(runtimeRoot, await createOpenCodePresentationArtifact(root));

await writeGeneratedArtifacts(root, gatewayCliPath);
fs.writeFileSync(path.join(runtimeRoot, "gateway-cli.LEGAL.txt.meta"), LEGAL_SIDECAR_META);
fs.writeFileSync(
  path.join(runtimeRoot, "gateway-runtime.json"),
  renderGatewayRuntimeManifest({ root, runtimeRoot, version: packageJson.version }),
);

console.log(`[build-gateway-runtime] wrote ${RUNTIME_RELATIVE}`);

function stripTrailingHorizontalWhitespace(filePath) {
  const text = fs.readFileSync(filePath, "utf8");
  const normalized = text.replace(/[ \t]+$/gm, "");
  if (normalized !== text) writeAfterSharingWindow(filePath, normalized);
}

function writeAfterSharingWindow(filePath, content) {
  const deadline = Date.now() + 2000;
  while (true) {
    try { fs.writeFileSync(filePath, content, typeof content === "string" ? "utf8" : undefined); return; }
    catch (error) {
      if (error?.code !== "EACCES" || Date.now() >= deadline) throw error;
      Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 25);
    }
  }
}
