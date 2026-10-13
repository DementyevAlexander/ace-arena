#!/usr/bin/env node

import fs from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

export const MAX_BUILD_DIGITS = 20;
export const VERSION_RE = /^(?<year>[1-9][0-9]{3})\.(?<month>[1-9]|1[0-2])\.(?<day>[1-9]|[12][0-9]|3[01])-(?<build>[1-9][0-9]{0,19})$/;
const REQUIRED_MCP_SDK_VERSION = "1.29.0";
const REQUIRED_MODERN_MCP_SDK_VERSION = "2.0.0";
const CURRENT_VERSION_MARKER_PREFIX = "<!-- sgg-current-version-claim:";
const CURRENT_VERSION_MARKER_RE = /^<!-- sgg-current-version-claim:([a-z0-9.-]{1,80}) -->$/;
const DOCUMENT_VERSION_CLAIMS = Object.freeze({
  "manual.en.smoke.environment": Object.freeze({
    file: "docs/en/maintainers/manual-checks.md",
    section: "Dedicated Unity MCP Smoke",
    line: /^SGG_UNITY_SMOKE_PACKAGE_VERSION {13}exact current package version \(([1-9][0-9]{3}\.(?:[1-9]|1[0-2])\.(?:[1-9]|[12][0-9]|3[01])-[1-9][0-9]{0,19})\)$/,
  }),
  "manual.en.smoke.command": Object.freeze({
    file: "docs/en/maintainers/manual-checks.md",
    section: "Dedicated Unity MCP Smoke",
    line: /^SGG_UNITY_SMOKE_PACKAGE_VERSION='([1-9][0-9]{3}\.(?:[1-9]|1[0-2])\.(?:[1-9]|[12][0-9]|3[01])-[1-9][0-9]{0,19})' \\$/,
  }),
  "manual.ru.smoke.environment": Object.freeze({
    file: "docs/ru/maintainers/manual-checks.md",
    section: "Dedicated MCP Smoke",
    line: /^SGG_UNITY_SMOKE_PACKAGE_VERSION {13}точная текущая package version \(([1-9][0-9]{3}\.(?:[1-9]|1[0-2])\.(?:[1-9]|[12][0-9]|3[01])-[1-9][0-9]{0,19})\)$/,
  }),
  "manual.ru.smoke.command": Object.freeze({
    file: "docs/ru/maintainers/manual-checks.md",
    section: "Dedicated MCP Smoke",
    line: /^SGG_UNITY_SMOKE_PACKAGE_VERSION='([1-9][0-9]{3}\.(?:[1-9]|1[0-2])\.(?:[1-9]|[12][0-9]|3[01])-[1-9][0-9]{0,19})' \\$/,
  }),
  "versioning.en.current": Object.freeze({
    file: "docs/en/maintainers/versioning.md",
    section: "Synchronized Surfaces",
    line: /^Current package version: `([1-9][0-9]{3}\.(?:[1-9]|1[0-2])\.(?:[1-9]|[12][0-9]|3[01])-[1-9][0-9]{0,19})`\.$/,
  }),
  "versioning.ru.current": Object.freeze({
    file: "docs/ru/maintainers/versioning.md",
    section: "Synchronized Surfaces",
    line: /^Текущая версия package: `([1-9][0-9]{3}\.(?:[1-9]|1[0-2])\.(?:[1-9]|[12][0-9]|3[01])-[1-9][0-9]{0,19})`\.$/,
  }),
});

function readJson(root, relativePath, errors) {
  try {
    return JSON.parse(fs.readFileSync(path.join(root, relativePath), "utf8"));
  } catch (error) {
    errors.push(`${relativePath}: ${error instanceof Error ? error.message : String(error)}`);
    return undefined;
  }
}

export function parseCalendarVersion(version) {
  if (typeof version !== "string") return undefined;
  const match = VERSION_RE.exec(version);
  if (!match?.groups) return undefined;
  const year = Number(match.groups.year);
  const month = Number(match.groups.month);
  const day = Number(match.groups.day);
  const buildText = match.groups.build;
  const build = BigInt(buildText);
  const date = new Date(Date.UTC(year, month - 1, day));
  if (date.getUTCFullYear() !== year || date.getUTCMonth() !== month - 1 || date.getUTCDate() !== day) return undefined;
  return { version, year, month, day, build, buildText, date, dateText: `${year}-${String(month).padStart(2, "0")}-${String(day).padStart(2, "0")}` };
}

function textVersion(root, relativePath, pattern, errors) {
  try {
    const text = fs.readFileSync(path.join(root, relativePath), "utf8");
    const match = pattern.exec(text);
    if (!match?.[1]) errors.push(`${relativePath}: current version field is missing or malformed`);
    return match?.[1];
  } catch (error) {
    errors.push(`${relativePath}: ${error instanceof Error ? error.message : String(error)}`);
    return undefined;
  }
}

function validateDocumentVersions(root, rootVersion, errors) {
  const expectedFiles = Object.values(DOCUMENT_VERSION_CLAIMS).map((claim) => claim.file);
  const files = [...new Set([...markdownFiles(root), ...expectedFiles])].sort();
  const found = new Map();
  for (const relativePath of files) {
    let lines;
    try {
      lines = fs.readFileSync(path.join(root, relativePath), "utf8").split("\n");
    } catch (error) {
      errors.push(`${relativePath}: ${error instanceof Error ? error.message : String(error)}`);
      continue;
    }
    let section;
    for (let index = 0; index < lines.length; index++) {
      const heading = /^## (.+)$/.exec(lines[index]);
      if (heading) section = heading[1];
      if (!lines[index].includes(CURRENT_VERSION_MARKER_PREFIX)) continue;
      const marker = CURRENT_VERSION_MARKER_RE.exec(lines[index].trim());
      if (!marker) {
        errors.push(`${relativePath}:${index + 1}: malformed current-version claim marker`);
        continue;
      }
      const id = marker[1];
      const schema = DOCUMENT_VERSION_CLAIMS[id];
      if (!schema) {
        errors.push(`${relativePath}:${index + 1}: unknown current-version claim ID ${id}`);
        continue;
      }
      if (found.has(id)) errors.push(`${relativePath}:${index + 1}: duplicate current-version claim ID ${id}`);
      else found.set(id, { relativePath, line: index + 1 });
      if (schema.file !== relativePath) errors.push(`${relativePath}:${index + 1}: current-version claim ${id} belongs in ${schema.file}`);
      if (schema.section !== section) errors.push(`${relativePath}:${index + 1}: current-version claim ${id} belongs in section '${schema.section}'`);
      const claimLine = lines[index + 1] ?? "";
      const claim = schema.line.exec(claimLine);
      if (!claim) errors.push(`${relativePath}:${index + 2}: current-version claim ${id} is not adjacent to its exact semantic line`);
      else if (claim[1] !== rootVersion) errors.push(`${relativePath}:${index + 2}: current-version claim ${id} ${JSON.stringify(claim[1])} must match package.json version ${JSON.stringify(rootVersion)}`);
    }
  }
  for (const id of Object.keys(DOCUMENT_VERSION_CLAIMS)) {
    if (!found.has(id)) errors.push(`missing current-version claim ID ${id}`);
  }
}

function markdownFiles(root) {
  const result = [];
  const visit = (directory, relativeDirectory = "") => {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      if (entry.name === ".git" || entry.name === "node_modules" || entry.name === "tmp") continue;
      const relativePath = relativeDirectory ? `${relativeDirectory}/${entry.name}` : entry.name;
      if (entry.isDirectory()) visit(path.join(directory, entry.name), relativePath);
      else if (entry.isFile() && entry.name.endsWith(".md")) result.push(relativePath);
    }
  };
  visit(root);
  return result;
}

function parseNpmPurl(value) {
  if (typeof value !== "string" || !value.startsWith("pkg:npm/")) return undefined;
  const identity = value.slice("pkg:npm/".length);
  if (/[?#]/.test(identity)) return undefined;
  const separator = identity.lastIndexOf("@");
  if (separator <= 0 || separator === identity.length - 1) return undefined;
  try {
    return { name: decodeURIComponent(identity.slice(0, separator)), version: decodeURIComponent(identity.slice(separator + 1)) };
  } catch {
    return undefined;
  }
}

function validateSbom(label, sbom, packageName, rootVersion, errors) {
  const component = sbom?.metadata?.component;
  const expectedPurl = `pkg:npm/${encodeURIComponent(packageName).replaceAll("%2F", "/")}@${encodeURIComponent(rootVersion)}`;
  const rootPurlPrefix = expectedPurl.slice(0, expectedPurl.lastIndexOf("@") + 1);
  if (component?.version !== rootVersion) errors.push(`${label} metadata.component.version: ${JSON.stringify(component?.version)} must match package.json version ${JSON.stringify(rootVersion)}`);
  const parsedPurl = parseNpmPurl(component?.purl);
  if (!parsedPurl || parsedPurl.name !== packageName || parsedPurl.version !== rootVersion) {
    errors.push(`${label} metadata.component.purl must identify ${packageName}@${rootVersion}`);
  }
  if (component?.["bom-ref"] !== component?.purl || component?.["bom-ref"] !== expectedPurl) {
    errors.push(`${label} metadata.component.bom-ref must exactly match the canonical root purl ${expectedPurl}`);
  }
  const dependencies = Array.isArray(sbom?.dependencies) ? sbom.dependencies : [];
  const rootDependencies = dependencies.filter((entry) => parseNpmPurl(entry?.ref)?.name === packageName);
  if (rootDependencies.length !== 1 || rootDependencies[0]?.ref !== expectedPurl) {
    errors.push(`${label} dependencies must contain exactly one canonical root ref ${expectedPurl}`);
  }
  const visit = (value, location) => {
    if (typeof value === "string") {
      const parsed = parseNpmPurl(value);
      if ((parsed?.name === packageName || value.startsWith(rootPurlPrefix)) && value !== expectedPurl) errors.push(`${label} ${location}: root package reference must equal ${expectedPurl}`);
      return;
    }
    if (Array.isArray(value)) value.forEach((item, index) => visit(item, `${location}[${index}]`));
    else if (value && typeof value === "object") for (const [key, item] of Object.entries(value)) visit(item, `${location}.${key}`);
  };
  visit(sbom, "$" );
}

export function checkVersion(options = {}) {
  const root = options.root ?? process.cwd();
  const now = options.now ?? new Date();
  const allowFutureDate = options.allowFutureDate ?? process.env.SGG_RELEASE_ALLOW_FUTURE_DATE;
  const errors = [];
  const rootPackage = readJson(root, "package.json", errors);
  const rootVersion = rootPackage?.version;
  const parsed = parseCalendarVersion(rootVersion);

  if (!parsed) {
    errors.push(`package.json: version must be a real Gregorian date in YYYY.M.D-N form without leading zeroes and with a 1..${MAX_BUILD_DIGITS}-digit build >= 1; got ${JSON.stringify(rootVersion)}`);
  } else {
    const today = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate()));
    if (parsed.date > today && allowFutureDate !== `${parsed.year}.${parsed.month}.${parsed.day}`) {
      errors.push(`package.json: release date ${parsed.dateText} is after current UTC date; set SGG_RELEASE_ALLOW_FUTURE_DATE=${parsed.year}.${parsed.month}.${parsed.day} only for explicit release preparation`);
    }
  }

  if (rootPackage?.dependencies?.["@modelcontextprotocol/sdk"] !== REQUIRED_MCP_SDK_VERSION) {
    errors.push(`package.json: @modelcontextprotocol/sdk must be pinned exactly to ${REQUIRED_MCP_SDK_VERSION}`);
  }
  for (const name of ["@modelcontextprotocol/node", "@modelcontextprotocol/server"]) {
    if (rootPackage?.dependencies?.[name] !== REQUIRED_MODERN_MCP_SDK_VERSION) {
      errors.push(`package.json: ${name} must be pinned exactly to ${REQUIRED_MODERN_MCP_SDK_VERSION}`);
    }
  }
  if (rootPackage?.devDependencies?.["@modelcontextprotocol/client"] !== REQUIRED_MODERN_MCP_SDK_VERSION) {
    errors.push(`package.json: @modelcontextprotocol/client must be pinned exactly to ${REQUIRED_MODERN_MCP_SDK_VERSION}`);
  }

  const lock = readJson(root, "package-lock.json", errors);
  const upm = readJson(root, "unity/Packages/com.sungeargames.unity-mcp/package.json", errors);
  const runtime = readJson(root, "unity/Packages/com.sungeargames.unity-mcp/Editor/GatewayRuntime/gateway-runtime.json", errors);
  const rootSbom = readJson(root, "sbom.cdx.json", errors);
  const runtimeSbom = readJson(root, "unity/Packages/com.sungeargames.unity-mcp/Editor/GatewayRuntime/sbom.cdx.json", errors);
  const surfaces = [
    ["package-lock.json version", lock?.version],
    ['package-lock.json packages[""].version', lock?.packages?.[""]?.version],
    ["Unity package.json version", upm?.version],
    ["UnityBridgeRuntime.PackageVersion", textVersion(root, "unity/Packages/com.sungeargames.unity-mcp/Editor/Bridge/UnityBridgeRuntime.cs", /PackageVersion\s*=\s*"([^"]+)"/, errors)],
    ["gateway-runtime.json version", runtime?.version],
  ];
  for (const [label, value] of surfaces) {
    if (value !== rootVersion) errors.push(`${label}: ${JSON.stringify(value)} must match package.json version ${JSON.stringify(rootVersion)}`);
  }
  validateDocumentVersions(root, rootVersion, errors);
  validateSbom("sbom.cdx.json", rootSbom, rootPackage?.name, rootVersion, errors);
  validateSbom("bundled sbom.cdx.json", runtimeSbom, rootPackage?.name, rootVersion, errors);

  if (lock?.packages?.[""]?.dependencies?.["@modelcontextprotocol/sdk"] !== REQUIRED_MCP_SDK_VERSION) {
    errors.push(`package-lock.json: root @modelcontextprotocol/sdk dependency must be pinned exactly to ${REQUIRED_MCP_SDK_VERSION}`);
  }
  if (lock?.packages?.["node_modules/@modelcontextprotocol/sdk"]?.version !== REQUIRED_MCP_SDK_VERSION) {
    errors.push(`package-lock.json: resolved @modelcontextprotocol/sdk must be ${REQUIRED_MCP_SDK_VERSION}`);
  }
  for (const name of ["@modelcontextprotocol/node", "@modelcontextprotocol/server"]) {
    if (lock?.packages?.[""]?.dependencies?.[name] !== REQUIRED_MODERN_MCP_SDK_VERSION) {
      errors.push(`package-lock.json: root ${name} dependency must be pinned exactly to ${REQUIRED_MODERN_MCP_SDK_VERSION}`);
    }
    if (lock?.packages?.[`node_modules/${name}`]?.version !== REQUIRED_MODERN_MCP_SDK_VERSION) {
      errors.push(`package-lock.json: resolved ${name} must be ${REQUIRED_MODERN_MCP_SDK_VERSION}`);
    }
  }
  if (lock?.packages?.[""]?.devDependencies?.["@modelcontextprotocol/client"] !== REQUIRED_MODERN_MCP_SDK_VERSION) {
    errors.push(`package-lock.json: root @modelcontextprotocol/client dev dependency must be pinned exactly to ${REQUIRED_MODERN_MCP_SDK_VERSION}`);
  }
  if (lock?.packages?.["node_modules/@modelcontextprotocol/client"]?.version !== REQUIRED_MODERN_MCP_SDK_VERSION) {
    errors.push(`package-lock.json: resolved @modelcontextprotocol/client must be ${REQUIRED_MODERN_MCP_SDK_VERSION}`);
  }
  return errors;
}

const isMain = process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href;
if (isMain) {
  const errors = checkVersion();
  for (const error of errors) console.error(`[check-version] ${error}`);
  if (errors.length > 0) process.exitCode = 1;
  else console.log("[check-version] ok");
}
