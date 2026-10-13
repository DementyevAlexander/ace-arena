#!/usr/bin/env node

import fs from "node:fs";
import path from "node:path";

const root = process.cwd();
const ignoredDirs = new Set([".git", "node_modules", "dist", "coverage"]);
const ignoredFiles = new Set(["scripts/check-forbidden-identifiers.mjs"]);
const textExtensions = new Set([".md", ".json", ".jsonc", ".js", ".mjs", ".ts", ".tsx", ".yml", ".yaml", ".cs"]);
const forbidden = [
  "github.com/romanilyin/sgg-unity-mcp-docs",
  "gateway-implementation-brief",
  "private-modules/",
  "old-docs-notes/",
  "CanonicalPathPrivate",
  "CanonicalMcp",
  "Canonical MCP",
  "Canonical MCP Tool",
  "Canonical MCP Command",
  "GitLab namespace",
  "SunGearGames/",
];

let failed = false;

function fail(message) {
  console.error(`[check-forbidden-identifiers] ${message}`);
  failed = true;
}

function walk(directory) {
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (!ignoredDirs.has(entry.name)) {
        walk(path.join(directory, entry.name));
      }
      continue;
    }

    const filePath = path.join(directory, entry.name);
    if (!textExtensions.has(path.extname(entry.name))) {
      continue;
    }

    const relativePath = path.relative(root, filePath).replaceAll(path.sep, "/");
    if (ignoredFiles.has(relativePath)) {
      continue;
    }

    const content = fs.readFileSync(filePath, "utf8");
    for (const pattern of forbidden) {
      if (content.includes(pattern)) {
        fail(`${relativePath}: contains forbidden private identifier ${JSON.stringify(pattern)}`);
      }
    }
  }
}

walk(root);

if (!failed) {
  console.log("[check-forbidden-identifiers] ok");
} else {
  process.exitCode = 1;
}
