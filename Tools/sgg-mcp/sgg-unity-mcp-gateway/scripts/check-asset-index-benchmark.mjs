#!/usr/bin/env node

import fs from "node:fs";
import path from "node:path";
import { performance } from "node:perf_hooks";

const root = process.cwd();
const fixtureRoot = path.join(root, "tests/fixtures/asset-index-benchmark");
const yamlExtensions = new Set([".asset", ".mat", ".prefab", ".unity"]);
const guidPattern = /guid:\s*([0-9a-fA-F]{32})/g;
const iterations = Number.parseInt(process.env.SGG_ASSET_INDEX_BENCHMARK_ITERATIONS ?? "500", 10);
const budgetMs = Number.parseFloat(process.env.SGG_ASSET_INDEX_BENCHMARK_MS ?? "1000");

let failed = false;

function fail(message) {
  console.error(`[check-asset-index-benchmark] ${message}`);
  failed = true;
}

function walk(directory) {
  const files = [];
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const entryPath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      files.push(...walk(entryPath));
    } else if (entry.isFile()) {
      files.push(entryPath);
    }
  }

  return files.sort((left, right) => left.localeCompare(right));
}

function relativePath(filePath) {
  return path.relative(fixtureRoot, filePath).replaceAll(path.sep, "/");
}

function loadFiles(filePaths) {
  return filePaths.map((filePath) => ({
    content: fs.readFileSync(filePath, "utf8"),
    relative: relativePath(filePath),
  }));
}

function isNullGuid(guid) {
  return /^0+$/.test(guid);
}

function scan(files) {
  const guidToAssetPath = new Map();
  const yamlFiles = [];
  let metaFiles = 0;

  for (const file of files) {
    if (file.relative.endsWith(".meta")) {
      metaFiles++;
      guidPattern.lastIndex = 0;
      const match = guidPattern.exec(file.content);
      if (match) {
        guidToAssetPath.set(match[1].toLowerCase(), file.relative.slice(0, -".meta".length));
      }
    } else if (yamlExtensions.has(path.extname(file.relative))) {
      yamlFiles.push(file);
    }
  }

  const missingScripts = new Set();
  const brokenReferences = new Set();
  for (const yamlFile of yamlFiles) {
    const lines = yamlFile.content.split(/\r?\n/);
    for (let index = 0; index < lines.length; index++) {
      const line = lines[index];
      const isScriptReference = line.includes("m_Script:");
      guidPattern.lastIndex = 0;
      for (let match = guidPattern.exec(line); match; match = guidPattern.exec(line)) {
        const guid = match[1].toLowerCase();
        if (isNullGuid(guid) || guidToAssetPath.has(guid)) {
          continue;
        }

        const location = `${guid}:${yamlFile.relative}:${index + 1}`;
        if (isScriptReference) {
          missingScripts.add(location);
        } else {
          brokenReferences.add(location);
        }
      }
    }
  }

  return {
    brokenReferenceCount: brokenReferences.size,
    guidCount: guidToAssetPath.size,
    metaFiles,
    missingScriptCount: missingScripts.size,
    yamlFiles: yamlFiles.length,
  };
}

if (!fs.existsSync(fixtureRoot)) {
  fail("missing tests/fixtures/asset-index-benchmark");
} else if (!Number.isFinite(iterations) || iterations < 1) {
  fail("SGG_ASSET_INDEX_BENCHMARK_ITERATIONS must be a positive integer");
} else if (!Number.isFinite(budgetMs) || budgetMs <= 0) {
  fail("SGG_ASSET_INDEX_BENCHMARK_MS must be a positive number");
} else {
  const files = loadFiles(walk(fixtureRoot));
  const result = scan(files);

  if (result.metaFiles !== 4 || result.guidCount !== 4 || result.yamlFiles !== 3) {
    fail(`unexpected fixture shape: ${JSON.stringify(result)}`);
  }

  if (result.missingScriptCount !== 1 || result.brokenReferenceCount !== 1) {
    fail(`unexpected reference scan result: ${JSON.stringify(result)}`);
  }

  const startedAt = performance.now();
  for (let index = 0; index < iterations; index++) {
    scan(files);
  }

  const elapsedMs = performance.now() - startedAt;
  if (elapsedMs > budgetMs) {
    fail(`fixture scan took ${elapsedMs.toFixed(1)}ms for ${iterations} iterations, budget is ${budgetMs}ms`);
  }

  if (!failed) {
    console.log(`[check-asset-index-benchmark] ok (${iterations} scans in ${elapsedMs.toFixed(1)}ms, budget ${budgetMs}ms)`);
  }
}

if (failed) {
  process.exitCode = 1;
}
