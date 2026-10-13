#!/usr/bin/env node

import { createGeneratedArtifacts, RUNTIME_RELATIVE, SBOM_META, SBOM_PATH, writeOrCheck } from "./runtime-compliance.mjs";

const root = process.cwd();
const check = process.argv.includes("--check");
const { compliance, sbom } = await createGeneratedArtifacts(root);
writeOrCheck(root, [SBOM_PATH, `${RUNTIME_RELATIVE}/${SBOM_PATH}`], sbom, check);
writeOrCheck(root, [`${RUNTIME_RELATIVE}/${SBOM_PATH}.meta`], SBOM_META, check);
console.log(`[generate-sbom] ${check ? "ok" : `wrote ${compliance.components.length} runtime components`}`);
