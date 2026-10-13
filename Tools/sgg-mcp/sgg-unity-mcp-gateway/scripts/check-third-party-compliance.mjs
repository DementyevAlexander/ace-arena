#!/usr/bin/env node

import { createGeneratedArtifacts, NOTICE_META, NOTICE_PATH, RUNTIME_RELATIVE, SBOM_META, SBOM_PATH, writeOrCheck } from "./runtime-compliance.mjs";

try {
  const root = process.cwd();
  const { compliance, notices, sbom } = await createGeneratedArtifacts(root);
  writeOrCheck(root, [NOTICE_PATH, `${RUNTIME_RELATIVE}/${NOTICE_PATH}`], notices, true);
  writeOrCheck(root, [SBOM_PATH, `${RUNTIME_RELATIVE}/${SBOM_PATH}`], sbom, true);
  writeOrCheck(root, [`${RUNTIME_RELATIVE}/${NOTICE_PATH}.meta`], NOTICE_META, true);
  writeOrCheck(root, [`${RUNTIME_RELATIVE}/${SBOM_PATH}.meta`], SBOM_META, true);
  console.log(`[check-third-party-compliance] ok (${compliance.components.length} runtime components)`);
} catch (error) {
  console.error(`[check-third-party-compliance] ${error.message}`);
  process.exitCode = 1;
}
