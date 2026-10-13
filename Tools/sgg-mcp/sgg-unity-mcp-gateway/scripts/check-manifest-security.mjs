#!/usr/bin/env node

import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = process.cwd();
const builtInPackageId = "com.sungeargames.unity-mcp";

export function validateManifestObject(manifest, options = {}) {
  const {
    relativePath = "<manifest>",
    trustedPackages = [],
    builtInPackage = builtInPackageId,
  } = options;
  const failures = [];
  const trusted = new Set(trustedPackages);

  function fail(message) {
    failures.push(`${relativePath}: ${message}`);
  }

  if (!manifest || typeof manifest !== "object" || Array.isArray(manifest)) {
    fail("manifest must be a JSON object");
    return { ok: false, failures };
  }

  if (!manifest.package || typeof manifest.package !== "string") {
    fail("package-contributed manifest must declare package");
    return { ok: false, failures };
  }

  if (manifest.package === builtInPackage) {
    fail(`example package manifest must not impersonate ${builtInPackage}`);
  }

  if (trusted.has(manifest.package)) {
    fail(`package ${manifest.package} must not be trusted by default policy`);
  }

  if (!Array.isArray(manifest.commands)) {
    fail("commands must be an array");
    return { ok: failures.length === 0, failures };
  }

  for (const command of manifest.commands) {
    const id = command && typeof command === "object" && typeof command.id === "string"
      ? command.id
      : "<missing>";

    if (!command || typeof command !== "object" || Array.isArray(command)) {
      fail(`command ${id} must be an object`);
      continue;
    }

    if (!command.id) {
      fail("command entry missing id");
    }

    if (!command.risk || !command.idempotency || !command.timeout_ms || !(command.schema || command.schema_json)) {
      fail(`command ${id} must include risk/idempotency/timeout/schema metadata`);
    }

    if (command.result_shape === "text_artifact" && !hasBoundedOutput(command)) {
      fail(`command ${id} with text_artifact result_shape must include output_limit.max_chars`);
    }
  }

  return { ok: failures.length === 0, failures };
}

export function validateManifestFile(filePath, options = {}) {
  const relativePath = options.relativePath ?? filePath;
  try {
    const manifest = JSON.parse(fs.readFileSync(filePath, "utf8"));
    return validateManifestObject(manifest, { ...options, relativePath });
  } catch (error) {
    return { ok: false, failures: [`${relativePath}: invalid JSON: ${error.message}`] };
  }
}

export function validateRepository(options = {}) {
  const repoRoot = options.root ?? root;
  const failures = [];

  function fail(message) {
    failures.push(message);
  }

  function read(relativePath) {
    const absolutePath = path.join(repoRoot, relativePath);
    if (!fs.existsSync(absolutePath)) {
      fail(`missing ${relativePath}`);
      return "";
    }
    return fs.readFileSync(absolutePath, "utf8");
  }

  function readJson(relativePath) {
    try {
      return JSON.parse(read(relativePath));
    } catch (error) {
      fail(`${relativePath}: invalid JSON: ${error.message}`);
      return undefined;
    }
  }

  const registry = read("unity/Packages/com.sungeargames.unity-mcp/Editor/Commands/CommandRegistry.cs");
  const scanner = read("unity/Packages/com.sungeargames.unity-mcp/Editor/Commands/CommandManifestScanner.cs");
  const policy = read("src/policy/policy.ts");
  const policyExample = readJson("config/policy.example.json");

  for (const expected of [
    'Source = "manifest"',
    "PackageId = packageId",
    "ManifestPath = unityManifestPath",
    'Trust = builtIn ? "builtin" : "untrusted_package"',
    'ScanProjectAssetManifestCandidates(registry)',
    'ProjectManifestCandidateReason = "project_asset_manifests_are_not_registered"',
  ]) {
    if (!scanner.includes(expected)) {
      fail(`CommandManifestScanner.cs must assign manifest security metadata: ${expected}`);
    }
  }

  for (const expected of [
    'Source = builtIn ? "builtin" : "attribute"',
    "PackageId = packageId",
    "displayPackage",
    'Trust = builtIn ? "builtin" : "untrusted_package"',
  ]) {
    if (!registry.includes(expected)) {
      fail(`CommandRegistry.cs must assign attribute command security metadata: ${expected}`);
    }
  }

  for (const expected of ["evaluateExtensionTrust", "isUntrustedPackageCommand", "untrusted_package_default ?? \"ask\"", "exactProjectRule.action === \"allow\" && extensionTrustDecision", "wildcardProjectRule.action === \"allow\" && extensionTrustDecision", "defaultAction === \"allow\" && extensionTrustDecision"]) {
    if (!policy.includes(expected)) {
      fail(`src/policy/policy.ts must gate untrusted package commands before auto-allow: ${expected}`);
    }
  }

  if (policyExample) {
    if (policyExample.untrusted_package_default === "allow") {
      fail("config/policy.example.json: untrusted_package_default must not be allow");
    }

    const trustedPackages = policyExample.trusted_packages ?? [];
    for (const absolutePath of listManifestFiles(path.join(repoRoot, "examples"))) {
      const relativePath = path.relative(repoRoot, absolutePath);
      const result = validateManifestFile(absolutePath, { relativePath, trustedPackages });
      failures.push(...result.failures);
    }
  }

  return { ok: failures.length === 0, failures };
}

function hasBoundedOutput(command) {
  return Boolean(
    command.output_limit
      && typeof command.output_limit === "object"
      && !Array.isArray(command.output_limit)
      && typeof command.output_limit.max_chars === "number"
      && Number.isFinite(command.output_limit.max_chars)
      && command.output_limit.max_chars > 0,
  );
}

function listManifestFiles(dir) {
  if (!fs.existsSync(dir)) {
    return [];
  }
  const result = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const absolutePath = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      result.push(...listManifestFiles(absolutePath));
    } else if (entry.isFile() && entry.name === "mcp.commands.json") {
      result.push(absolutePath);
    }
  }
  return result.sort((left, right) => left.localeCompare(right));
}

function runCli() {
  const result = validateRepository({ root });
  for (const failure of result.failures) {
    console.error(`[check-manifest-security] ${failure}`);
  }

  if (result.ok) {
    console.log("[check-manifest-security] ok");
  } else {
    process.exitCode = 1;
  }
}

if (process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1])) {
  runCli();
}
