#!/usr/bin/env node

import fs from "node:fs";
import path from "node:path";

import { createReleaseEvidenceTemplate, getReleaseContext, resolveEvidencePath } from "./check-release.mjs";

try {
  const root = process.cwd();
  const outputPath = resolveEvidencePath(process.argv.slice(2), process.env, root);
  const context = getReleaseContext(root);
  fs.mkdirSync(path.dirname(outputPath), { recursive: true });
  const checkedOutputPath = resolveEvidencePath(process.argv.slice(2), process.env, root);
  const noFollow = fs.constants.O_NOFOLLOW ?? 0;
  const handle = fs.openSync(checkedOutputPath, fs.constants.O_WRONLY | fs.constants.O_CREAT | fs.constants.O_TRUNC | noFollow, 0o600);
  try {
    fs.writeFileSync(handle, `${JSON.stringify(createReleaseEvidenceTemplate(context), null, 2)}\n`);
  } finally {
    fs.closeSync(handle);
  }
  console.log(`[generate-release-evidence-template] wrote ${path.relative(root, outputPath)}`);
} catch (error) {
  console.error(`[generate-release-evidence-template] ${error instanceof Error ? error.message : String(error)}`);
  process.exitCode = 1;
}
