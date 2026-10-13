import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";

import { build } from "esbuild";

import { gatewayRuntimeBuildOptions } from "./runtime-build-options.mjs";

export const REVIEW_REQUIRED = "REVIEW REQUIRED";
export const NOTICE_PATH = "THIRD_PARTY_NOTICES.md";
export const SBOM_PATH = "sbom.cdx.json";
export const RUNTIME_RELATIVE = "unity/Packages/com.sungeargames.unity-mcp/Editor/GatewayRuntime";
export const RUNTIME_COMPONENT_COUNT = 22;
export const NOTICE_META = unityTextMeta("5c8dd0375e0b4b2eb27eb11dbbd5e00a");
export const SBOM_META = unityTextMeta("f964bc2e21424c15abf8d18e3a6d5893");
export const EXPECTED_RUNTIME_COMPONENTS = [
  "@hono/node-server",
  "@modelcontextprotocol/core",
  "@modelcontextprotocol/node",
  "@modelcontextprotocol/sdk",
  "@modelcontextprotocol/server",
  "@romanilyin/canonicalpath",
  "ajv",
  "ajv-formats",
  "cross-spawn",
  "eventsource-parser",
  "fast-deep-equal",
  "fast-uri",
  "isexe",
  "json-schema-traverse",
  "jsonc-parser",
  "path-key",
  "pkce-challenge",
  "shebang-command",
  "shebang-regex",
  "which",
  "zod",
  "zod-to-json-schema",
];

const SPDX_LICENSES = new Set(["BSD-3-Clause", "ISC", "MIT"]);
const LEGAL_FILE_RE = /^(?:licen[cs]e|notice|copying)(?:[._-].*)?$/i;
const textDecoder = new TextDecoder("utf-8", { fatal: true });

export function codePointCompare(left, right) {
  return left < right ? -1 : left > right ? 1 : 0;
}

export function normalizeLegalText(text) {
  return text.replace(/\r\n?/g, "\n")
    .split("\n")
    .map((line) => line.replace(/[ \t]+$/, ""))
    .join("\n")
    .replace(/\n*$/, "");
}

export async function loadRuntimeCompliance(root = process.cwd()) {
  const packageJson = readJson(path.join(root, "package.json"));
  const lock = readJson(path.join(root, "package-lock.json"));
  const runtimeOutfile = path.join(root, RUNTIME_RELATIVE, "gateway-cli.mjs");
  const result = await build({
    ...gatewayRuntimeBuildOptions(root, runtimeOutfile),
    write: false,
  });
  const packageLocations = packageLocationsFromMetafile(result.metafile);
  const components = packageLocations.map(({ name, lockKey }) => {
    const lockRecord = lock.packages?.[lockKey];
    if (!lockRecord) {
      review(`${name}: bundled package is unknown to package-lock.json`);
    }

    const packageRoot = path.join(root, lockKey);
    const installedPackage = readJson(path.join(packageRoot, "package.json"));
    const legalFiles = fs.readdirSync(packageRoot, { withFileTypes: true })
      .filter((entry) => entry.isFile() && LEGAL_FILE_RE.test(entry.name))
      .map((entry) => ({
        name: entry.name,
        text: normalizeLegalText(readUtf8(path.join(packageRoot, entry.name))),
      }))
      .sort((left, right) => codePointCompare(left.name, right.name));

    return validatePackageRecord({ name, lockRecord, installedPackage, legalFiles });
  });

  components.sort((left, right) => codePointCompare(left.name, right.name));
  const componentNames = components.map(({ name }) => name);
  assertExactComponentNames(componentNames, packageLocations.map(({ name }) => name));
  assertExactComponentNames(componentNames, EXPECTED_RUNTIME_COMPONENTS);
  assertExactComponentNames(Object.keys(packageJson.dependencies ?? {}), ["@modelcontextprotocol/node", "@modelcontextprotocol/sdk", "@modelcontextprotocol/server", "@romanilyin/canonicalpath", "jsonc-parser", "zod"]);
  if (componentNames.includes("esbuild")) {
    review("build-only esbuild was bundled as a runtime component");
  }

  const esbuild = packageFromLock(root, lock, "esbuild");
  return { root, packageJson, lock, components, esbuild };
}

export function packageLocationsFromMetafile(metafile) {
  if (!metafile || typeof metafile.inputs !== "object" || metafile.inputs === null) {
    review("esbuild metafile inputs are missing");
  }

  const locations = new Map();
  for (const input of Object.keys(metafile.inputs).sort(codePointCompare)) {
    const normalized = input.replaceAll("\\", "/");
    const segments = normalized.split("/");
    const marker = segments.lastIndexOf("node_modules");
    if (marker < 0 || marker + 1 >= segments.length) {
      continue;
    }
    const packageEnd = segments[marker + 1].startsWith("@") ? marker + 3 : marker + 2;
    if (packageEnd > segments.length) {
      review(`malformed bundled package input: ${normalized}`);
    }
    const lockKey = segments.slice(0, packageEnd).join("/");
    const name = segments.slice(marker + 1, packageEnd).join("/");
    const existing = locations.get(name);
    if (existing && existing !== lockKey) {
      review(`${name}: multiple bundled installations are unsupported`);
    }
    locations.set(name, lockKey);
  }

  if (locations.size === 0) {
    review("esbuild metafile contains no bundled npm packages");
  }
  return [...locations].map(([name, lockKey]) => ({ name, lockKey }))
    .sort((left, right) => codePointCompare(left.name, right.name));
}

export function validatePackageRecord({ name, lockRecord, installedPackage, legalFiles }) {
  if (!lockRecord || typeof lockRecord !== "object") {
    review(`${name}: missing package-lock record`);
  }
  for (const field of ["version", "license", "resolved", "integrity"]) {
    if (typeof lockRecord[field] !== "string" || lockRecord[field].length === 0) {
      review(`${name}: package-lock ${field} is missing`);
    }
  }
  if (installedPackage?.name !== name) {
    review(`${name}: installed package name mismatch`);
  }
  if (installedPackage?.version !== lockRecord.version) {
    review(`${name}: installed version does not match package-lock`);
  }
  if (installedPackage?.license !== lockRecord.license) {
    review(`${name}: installed license does not match package-lock`);
  }
  validateSpdxLicense(name, lockRecord.license);
  const integrityHex = integrityToHex(name, lockRecord.integrity);
  if (!Array.isArray(legalFiles) || legalFiles.length === 0 || legalFiles.some((file) => !file.text)) {
    review(`${name}: installed legal file is missing or empty`);
  }

  return {
    name,
    version: lockRecord.version,
    license: lockRecord.license,
    resolved: lockRecord.resolved,
    integrity: lockRecord.integrity,
    integrityHex,
    legalFiles,
  };
}

export function assertExactComponentNames(actualNames, expectedNames) {
  const actual = [...actualNames].sort(codePointCompare);
  const expected = [...expectedNames].sort(codePointCompare);
  if (new Set(actual).size !== actual.length || JSON.stringify(actual) !== JSON.stringify(expected)) {
    review("generated component set has a missing or unexpected package");
  }
}

export function renderThirdPartyNotices(compliance) {
  const sections = [
    "# Third-Party Notices",
    "",
    "The Stinger Royalty-Free EULA 1.0 applies to the SGG Unity MCP Gateway itself. It does not replace, override, or restrict the licenses of the third-party materials listed below.",
    "",
    "## Bundled npm Runtime Libraries",
  ];

  for (const component of compliance.components) {
    sections.push(
      "",
      `### ${component.name} ${component.version}`,
      "",
      `SPDX license: \`${component.license}\``,
      "",
      `Resolved: \`${component.resolved}\``,
      "",
      `Integrity: \`${component.integrity}\``,
    );
    for (const legalFile of component.legalFiles) {
      sections.push("", `#### ${legalFile.name}`, "", `Legal source: \`${legalFile.name}\``, "", "```text", legalFile.text, "```");
    }
  }

  sections.push(
    "",
    "## Noto Sans",
    "",
    "The bundled Noto Sans fallback font is Copyright 2018 The Noto Project Authors and is licensed under the SIL Open Font License, Version 1.1.",
    "",
    "```text",
    compliance.notoText,
    "```",
    "",
    "## SerializeReferenceDropdownIntegration",
    "",
    "The SerializeReference YAML scanning logic is based in part on ideas and parsing behavior from SerializeReferenceDropdownIntegration by Alexey Taranov, licensed under the MIT License.",
    "",
    "```text",
    ALEXEY_TARANOV_MIT,
    "```",
    "",
  );
  return sections.join("\n");
}

export function renderCycloneDx(compliance) {
  const rootRef = npmPurl(compliance.packageJson.name, compliance.packageJson.version);
  const runtimeRefs = compliance.components.map((component) => npmPurl(component.name, component.version));
  const runtimeCli = fs.readFileSync(compliance.runtimeCliPath ?? path.join(compliance.root, RUNTIME_RELATIVE, "gateway-cli.mjs"));
  const lockBytes = fs.readFileSync(path.join(compliance.root, "package-lock.json"));
  const document = {
    $schema: "https://cyclonedx.org/schema/bom-1.6.schema.json",
    bomFormat: "CycloneDX",
    specVersion: "1.6",
    version: 1,
    metadata: {
      tools: {
        components: [sbomComponent(compliance.esbuild, "application")],
      },
      component: {
        type: "application",
        "bom-ref": rootRef,
        name: compliance.packageJson.name,
        version: compliance.packageJson.version,
        purl: rootRef,
        properties: [
          { name: "sgg:artifact:gateway-cli:sha256", value: sha256(runtimeCli) },
          { name: "sgg:source:package-lock:sha256", value: sha256(lockBytes) },
        ],
      },
    },
    components: compliance.components.map((component) => sbomComponent(component, "library")),
    dependencies: [
      { ref: rootRef, dependsOn: runtimeRefs },
      ...runtimeRefs.map((ref) => ({ ref, dependsOn: [] })),
    ],
  };
  return `${JSON.stringify(document, null, 2)}\n`;
}

export async function createGeneratedArtifacts(root = process.cwd(), runtimeCliPath) {
  const compliance = await loadRuntimeCompliance(root);
  compliance.runtimeCliPath = runtimeCliPath;
  compliance.notoText = normalizeLegalText(readUtf8(path.join(root, "unity/Packages/com.sungeargames.unity-mcp/Editor/UI/Fonts/NotoSans-OFL.txt")));
  return {
    compliance,
    notices: renderThirdPartyNotices(compliance),
    sbom: renderCycloneDx(compliance),
  };
}

export async function writeGeneratedArtifacts(root = process.cwd(), runtimeCliPath) {
  const generated = await createGeneratedArtifacts(root, runtimeCliPath);
  writeOrCheck(root, [NOTICE_PATH, `${RUNTIME_RELATIVE}/${NOTICE_PATH}`], generated.notices, false);
  writeOrCheck(root, [SBOM_PATH, `${RUNTIME_RELATIVE}/${SBOM_PATH}`], generated.sbom, false);
  writeOrCheck(root, [`${RUNTIME_RELATIVE}/${NOTICE_PATH}.meta`], NOTICE_META, false);
  writeOrCheck(root, [`${RUNTIME_RELATIVE}/${SBOM_PATH}.meta`], SBOM_META, false);
  return generated;
}

export function writeOrCheck(root, relativePaths, content, check) {
  const bytes = Buffer.from(content, "utf8");
  for (const relativePath of relativePaths) {
    const filePath = path.join(root, relativePath);
    if (check) {
      if (!fs.existsSync(filePath) || !fs.readFileSync(filePath).equals(bytes)) {
        throw new Error(`${REVIEW_REQUIRED}: ${relativePath} is missing or stale`);
      }
    } else {
      fs.writeFileSync(filePath, bytes);
    }
  }
}

function packageFromLock(root, lock, name) {
  const lockKey = `node_modules/${name}`;
  const lockRecord = lock.packages?.[lockKey];
  if (!lockRecord) {
    review(`${name}: build tool is unknown to package-lock.json`);
  }
  const installedPackage = readJson(path.join(root, lockKey, "package.json"));
  const packageRoot = path.join(root, lockKey);
  const legalFiles = fs.readdirSync(packageRoot, { withFileTypes: true })
    .filter((entry) => entry.isFile() && LEGAL_FILE_RE.test(entry.name))
    .map((entry) => ({ name: entry.name, text: normalizeLegalText(readUtf8(path.join(packageRoot, entry.name))) }))
    .sort((left, right) => codePointCompare(left.name, right.name));
  return validatePackageRecord({ name, lockRecord, installedPackage, legalFiles });
}

function sbomComponent(component, type) {
  const purl = npmPurl(component.name, component.version);
  return {
    type,
    "bom-ref": purl,
    name: component.name,
    version: component.version,
    hashes: [{ alg: "SHA-512", content: component.integrityHex }],
    licenses: [{ license: { id: component.license } }],
    purl,
    externalReferences: [{ type: "distribution", url: component.resolved }],
  };
}

function npmPurl(name, version) {
  const encodedName = name.startsWith("@") ? `%40${name.slice(1)}` : name;
  return `pkg:npm/${encodedName}@${version}`;
}

function validateSpdxLicense(name, license) {
  if (/LicenseRef|SEE\s+LICENSE|UNLICENSED/i.test(license) || !SPDX_LICENSES.has(license)) {
    review(`${name}: license is unknown, unlicensed, or not an approved SPDX identifier`);
  }
}

function integrityToHex(name, integrity) {
  if (!integrity.startsWith("sha512-")) {
    review(`${name}: package-lock integrity must be SHA-512`);
  }
  let bytes;
  try {
    bytes = Buffer.from(integrity.slice(7), "base64");
  } catch {
    review(`${name}: package-lock integrity is malformed`);
  }
  if (bytes.length !== 64 || bytes.toString("base64") !== integrity.slice(7)) {
    review(`${name}: package-lock integrity is malformed`);
  }
  return bytes.toString("hex");
}

function readJson(filePath) {
  try {
    return JSON.parse(readUtf8(filePath));
  } catch (error) {
    review(`required JSON is missing or invalid: ${error.message}`);
  }
}

function readUtf8(filePath) {
  return textDecoder.decode(fs.readFileSync(filePath));
}

function sha256(bytes) {
  return crypto.createHash("sha256").update(bytes).digest("hex");
}

function unityTextMeta(guid) {
  return `fileFormatVersion: 2
guid: ${guid}
TextScriptImporter:
  externalObjects: {}
  userData:
  assetBundleName:
  assetBundleVariant:
`;
}

function review(message) {
  throw new Error(`${REVIEW_REQUIRED}: ${message}`);
}

export const ALEXEY_TARANOV_MIT = `MIT License

Copyright (c) 2026 Alexey Taranov

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.`;
