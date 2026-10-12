#!/usr/bin/env node

import { createGeneratedArtifacts, NOTICE_META, NOTICE_PATH, RUNTIME_RELATIVE, writeOrCheck } from "./runtime-compliance.mjs";

const root = process.cwd();
const check = process.argv.includes("--check");
const { compliance, notices } = await createGeneratedArtifacts(root);
writeOrCheck(root, [NOTICE_PATH, `${RUNTIME_RELATIVE}/${NOTICE_PATH}`], notices, check);
writeOrCheck(root, [`${RUNTIME_RELATIVE}/${NOTICE_PATH}.meta`], NOTICE_META, check);
console.log(`[generate-third-party-notices] ${check ? "ok" : `wrote ${compliance.components.length} runtime components`}`);
