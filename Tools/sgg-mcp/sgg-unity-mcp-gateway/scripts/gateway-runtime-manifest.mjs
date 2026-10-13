import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";

import { codePointCompare, NOTICE_PATH, SBOM_PATH } from "./runtime-compliance.mjs";
import { OPENCODE_PRESENTATION_MANIFEST, OPENCODE_PRESENTATION_RUNTIME_RELATIVE } from "./opencode-presentation-artifact.mjs";

export const GATEWAY_CLI_PATH = "gateway-cli.mjs";
export const LEGAL_SIDECAR_PATH = "gateway-cli.LEGAL.txt";
export const RUNTIME_MANIFEST_PATH = "gateway-runtime.json";
export const LEGAL_SIDECAR_META = `fileFormatVersion: 2
guid: 79932f6a329b41b09d105e04e8ea6811
TextScriptImporter:
  externalObjects: {}
  userData:
  assetBundleName:
  assetBundleVariant:
`;

export function normalizeLegalSidecar(gatewayCliPath) {
  const esbuildPath = `${gatewayCliPath}.LEGAL.txt`;
  const requiredPath = path.join(path.dirname(gatewayCliPath), LEGAL_SIDECAR_PATH);
  if (!fs.existsSync(esbuildPath)) {
    throw new Error(`esbuild did not emit required legal comments sidecar ${path.basename(esbuildPath)}`);
  }

  const bytes = fs.readFileSync(esbuildPath);
  if (bytes.length === 0 || !bytes.toString("utf8").trim() || !/\/\*!|@license|@preserve|@cc_on/.test(bytes.toString("utf8"))) {
    throw new Error(`esbuild legal comments sidecar ${path.basename(esbuildPath)} is empty or contains no preserved legal comment`);
  }

  fs.rmSync(requiredPath, { force: true });
  fs.renameSync(esbuildPath, requiredPath);
  return requiredPath;
}

export function createGatewayRuntimeManifest({ root, runtimeRoot, version }) {
  return {
    schema_version: 3,
    version,
    entrypoint: GATEWAY_CLI_PATH,
    gateway_virtual_commands_count: countGatewayVirtualCommands(root),
    config: "config/gateway.example.json",
    policy: "config/policy.example.json",
    artifacts: {
      gateway_cli: artifact(runtimeRoot, GATEWAY_CLI_PATH),
      legal_comments: artifact(runtimeRoot, LEGAL_SIDECAR_PATH),
      third_party_notices: artifact(runtimeRoot, NOTICE_PATH),
      sbom: artifact(runtimeRoot, SBOM_PATH),
      opencode_presentation_manifest: artifact(runtimeRoot, path.join(OPENCODE_PRESENTATION_RUNTIME_RELATIVE, OPENCODE_PRESENTATION_MANIFEST)),
      opencode_presentation_package: artifact(runtimeRoot, path.join(OPENCODE_PRESENTATION_RUNTIME_RELATIVE, "package.json")),
      opencode_presentation_server: artifact(runtimeRoot, path.join(OPENCODE_PRESENTATION_RUNTIME_RELATIVE, "server.js")),
    },
  };
}

export function renderGatewayRuntimeManifest(options) {
  return `${JSON.stringify(createGatewayRuntimeManifest(options), null, 2)}\n`;
}

export function sha256File(filePath) {
  return crypto.createHash("sha256").update(fs.readFileSync(filePath)).digest("hex");
}

export function fileBytesMismatch(filePath, expected) {
  if (!fs.existsSync(filePath)) return "missing";
  return fs.readFileSync(filePath).equals(expected) ? undefined : "drift";
}

function artifact(runtimeRoot, relativePath) {
  const filePath = path.join(runtimeRoot, relativePath);
  if (!fs.existsSync(filePath)) {
    throw new Error(`required runtime artifact is missing: ${relativePath}`);
  }
  return { path: relativePath, sha256: sha256File(filePath) };
}

function countGatewayVirtualCommands(root) {
  const virtualCommandsDir = path.join(root, "src", "mcp");
  let count = 0;
  const files = fs.readdirSync(virtualCommandsDir)
    .filter((fileName) => fileName.endsWith("-commands.ts"))
    .sort(codePointCompare);

  for (const fileName of files) {
    const text = fs.readFileSync(path.join(virtualCommandsDir, fileName), "utf8");
    const arrayRegex = /export\s+const\s+[A-Z0-9_]*COMMANDS\s*:\s*CommandDescriptor\[\]\s*=\s*\[/g;
    for (const match of text.matchAll(arrayRegex)) {
      const arrayStart = match.index + match[0].lastIndexOf("[");
      const arrayEnd = findMatching(text, arrayStart, "[", "]");
      if (arrayEnd < 0) {
        throw new Error(`${path.relative(root, path.join(virtualCommandsDir, fileName))}: malformed CommandDescriptor array`);
      }
      count += topLevelObjectCount(text.slice(arrayStart + 1, arrayEnd));
    }
  }
  return count;
}

function findMatching(text, start, openChar, closeChar) {
  let depth = 0;
  let inString = false;
  let escaped = false;
  for (let index = start; index < text.length; index++) {
    const char = text[index];
    if (escaped) {
      escaped = false;
    } else if (char === "\\") {
      escaped = true;
    } else if (char === '"') {
      inString = !inString;
    } else if (!inString && char === openChar) {
      depth++;
    } else if (!inString && char === closeChar && --depth === 0) {
      return index;
    }
  }
  return -1;
}

function topLevelObjectCount(value) {
  let count = 0;
  let depth = 0;
  let inString = false;
  let escaped = false;
  for (const char of value) {
    if (escaped) {
      escaped = false;
    } else if (char === "\\") {
      escaped = true;
    } else if (char === '"') {
      inString = !inString;
    } else if (!inString && char === "{") {
      if (depth++ === 0) count++;
    } else if (!inString && char === "}") {
      depth--;
    }
  }
  return count;
}
