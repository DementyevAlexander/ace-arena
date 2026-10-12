#!/usr/bin/env node

import fs from "node:fs";
import path from "node:path";

import { renderGatewayRuntimeManifest } from "./gateway-runtime-manifest.mjs";
import { RUNTIME_RELATIVE } from "./runtime-compliance.mjs";
import { createOpenCodePresentationArtifact, writeOpenCodePresentationArtifact } from "./opencode-presentation-artifact.mjs";

const root = process.cwd();
const runtimeRoot = path.join(root, RUNTIME_RELATIVE);
const packageJson = JSON.parse(fs.readFileSync(path.join(root, "package.json"), "utf8"));
const artifact = await createOpenCodePresentationArtifact(root);
writeOpenCodePresentationArtifact(runtimeRoot, artifact);
fs.writeFileSync(
  path.join(runtimeRoot, "gateway-runtime.json"),
  renderGatewayRuntimeManifest({ root, runtimeRoot, version: packageJson.version }),
);
console.log(`[build-opencode-presentation] wrote ${RUNTIME_RELATIVE}/opencode-presentation and refreshed gateway-runtime.json`);
