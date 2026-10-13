#!/usr/bin/env node

import fs from "node:fs";
import path from "node:path";

const root = process.cwd();
const skillRelativePath = "examples/opencode/SKILL.md";
const skillPath = path.join(root, skillRelativePath);
const maxLines = 80;
const forbiddenLanguage = [
  /bypass\s+(permission|policy|approval|confirmation)/i,
  /ignore\s+(permission|policy|approval|confirmation)/i,
  /disable\s+(permission|policy|approval|confirmation)/i,
  /auto[- ]?approve/i,
  /allow\s+all/i,
  /permit\s+all/i,
  /edit\s+policy\s+to\s+allow/i,
];

let failed = false;

function fail(message) {
  console.error(`[check-skill] ${message}`);
  failed = true;
}

if (!fs.existsSync(skillPath)) {
  fail(`missing ${skillRelativePath}`);
} else {
  const text = fs.readFileSync(skillPath, "utf8");
  const lines = text.split("\n");
  if (lines.length > maxLines) {
    fail(`${skillRelativePath}: keep entrypoint short (${lines.length} lines, max ${maxLines})`);
  }
  for (const expression of forbiddenLanguage) {
    const match = text.match(expression);
    if (match) {
      fail(`${skillRelativePath}: permission-expansion language matched ${expression}: ${match[0]}`);
    }
  }

  const references = [...text.matchAll(/`(examples\/opencode\/references\/[^`]+\.md)`/g)].map((match) => match[1]);
  if (references.length === 0) {
    fail(`${skillRelativePath}: must link focused reference files`);
  }
  for (const reference of references) {
    if (!fs.existsSync(path.join(root, reference))) {
      fail(`${skillRelativePath}: missing referenced file ${reference}`);
    }
  }
}

if (!failed) {
  console.log("[check-skill] ok");
} else {
  process.exitCode = 1;
}
