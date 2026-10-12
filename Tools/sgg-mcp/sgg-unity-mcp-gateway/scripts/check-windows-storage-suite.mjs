#!/usr/bin/env node

import fs from "node:fs";

const expectedTests = [
  "tests/safe-storage.test.ts",
  "tests/audit.test.ts",
  "tests/approval-store.test.ts",
  "tests/windows-safe-storage.test.ts",
  "tests/windows-project-artifact-reader.test.ts",
];
const packageJson = JSON.parse(fs.readFileSync("package.json", "utf8"));
const command = packageJson.scripts?.["test:windows-storage"];
const workflow = fs.readFileSync(".github/workflows/checks.yml", "utf8");

for (const test of expectedTests) {
  if (!fs.statSync(test).isFile()) throw new Error(`Windows storage test is missing: ${test}`);
  if (typeof command !== "string" || !command.includes(test)) throw new Error(`test:windows-storage does not include ${test}`);
}
if (!workflow.includes("npm run test:windows-storage")) throw new Error("Windows CI does not run test:windows-storage");

console.log("[check-windows-storage-suite] ok");
