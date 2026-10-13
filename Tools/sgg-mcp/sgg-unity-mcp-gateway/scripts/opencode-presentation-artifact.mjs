import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";

import { build } from "esbuild";

export const OPENCODE_PRESENTATION_RUNTIME_RELATIVE = "opencode-presentation";
export const OPENCODE_PRESENTATION_MANIFEST = "sgg-plugin-manifest.json";

const SOURCE_RELATIVE = "integrations/opencode-sgg-unity-presentation";
const ARTIFACT_FILES = ["package.json", "server.js"];
const GENERATED_FILES = [...ARTIFACT_FILES, OPENCODE_PRESENTATION_MANIFEST];
const GENERATED_META_FILES = GENERATED_FILES.map((file) => `${file}.meta`);
const MAX_PACKAGE_BYTES = 64 * 1024;
const MAX_SERVER_BYTES = 2 * 1024 * 1024;
const MAX_MANIFEST_BYTES = 64 * 1024;

function sha256(content) {
  return crypto.createHash("sha256").update(content).digest("hex");
}

function jsonBytes(value) {
  return Buffer.from(`${JSON.stringify(value, null, 2)}\n`);
}

export async function createOpenCodePresentationArtifact(root = process.cwd()) {
  const sourceRoot = path.join(root, SOURCE_RELATIVE);
  const sourcePackage = JSON.parse(fs.readFileSync(path.join(sourceRoot, "package.json"), "utf8"));
  const result = await build({
    entryPoints: [path.join(sourceRoot, "src", "server.ts")],
    bundle: true,
    write: false,
    format: "esm",
    platform: "neutral",
    target: "es2022",
    charset: "utf8",
    legalComments: "none",
    sourcemap: false,
    external: ["@opencode/plugin"],
  });
  const output = result.outputFiles?.[0];
  if (!output) throw new Error("OpenCode presentation build emitted no server artifact");

  const files = new Map([
    ["package.json", jsonBytes({
      name: sourcePackage.name,
      version: sourcePackage.version,
      type: "module",
      private: true,
      exports: { ".": "./server.js" },
      engines: sourcePackage.engines,
      dependencies: sourcePackage.dependencies,
    })],
    ["server.js", Buffer.from(output.contents)],
  ]);
  const trustedPreviousManifestSha256 = sourcePackage.sgg_presentation?.trusted_previous_manifest_sha256 ?? [];
  const manifest = {
    schema_version: 1,
    plugin_id: "sgg-unity.presentation",
    version: sourcePackage.version,
    opencode_range: sourcePackage.engines?.opencode,
    trusted_previous_manifest_sha256: trustedPreviousManifestSha256,
    files: ARTIFACT_FILES.map((file) => {
      const content = files.get(file);
      if (!content) throw new Error(`missing generated presentation artifact ${file}`);
      return { path: file, bytes: content.length, sha256: sha256(content) };
    }),
  };
  files.set(OPENCODE_PRESENTATION_MANIFEST, jsonBytes(manifest));
  validateOpenCodePresentationArtifact({ files, manifest });
  return { files, manifest };
}

export function validateOpenCodePresentationArtifact(artifact) {
  const { files, manifest } = artifact;
  if (!(files instanceof Map)
    || files.size !== GENERATED_FILES.length
    || manifest?.schema_version !== 1
    || manifest?.plugin_id !== "sgg-unity.presentation"
    || typeof manifest.version !== "string"
    || !/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/.test(manifest.version)
    || typeof manifest.opencode_range !== "string"
    || manifest.opencode_range.length === 0
    || manifest.opencode_range.length > 128
    || !Array.isArray(manifest.trusted_previous_manifest_sha256)
    || manifest.trusted_previous_manifest_sha256.length > 32
    || manifest.trusted_previous_manifest_sha256.some((hash) => typeof hash !== "string" || !/^[a-f0-9]{64}$/.test(hash))
    || new Set(manifest.trusted_previous_manifest_sha256).size !== manifest.trusted_previous_manifest_sha256.length
    || !Array.isArray(manifest.files)
    || manifest.files.length !== ARTIFACT_FILES.length) throw new Error("invalid OpenCode presentation artifact contract");
  for (const [name, limit] of [["package.json", MAX_PACKAGE_BYTES], ["server.js", MAX_SERVER_BYTES], [OPENCODE_PRESENTATION_MANIFEST, MAX_MANIFEST_BYTES]]) {
    const content = files.get(name);
    if (!Buffer.isBuffer(content) || content.length > limit) throw new Error(`invalid OpenCode presentation artifact ${name}`);
  }
  const manifestPaths = new Set();
  for (const entry of manifest.files) {
    const content = files.get(entry?.path);
    if (!ARTIFACT_FILES.includes(entry?.path)
      || manifestPaths.has(entry.path)
      || !Buffer.isBuffer(content)
      || entry.bytes !== content.length
      || entry.sha256 !== sha256(content)) throw new Error("invalid OpenCode presentation artifact manifest");
    manifestPaths.add(entry.path);
  }
  const packageJson = JSON.parse(files.get("package.json").toString("utf8"));
  if (packageJson.name !== "@sungeargames/opencode-sgg-unity-presentation"
    || packageJson.version !== manifest.version
    || packageJson.type !== "module"
    || packageJson.private !== true
    || packageJson.exports?.["."] !== "./server.js"
    || Object.keys(packageJson.exports).length !== 1
    || packageJson.engines?.opencode !== ">=2.0.25 <3"
    || packageJson.engines?.opencode !== manifest.opencode_range
    || packageJson.dependencies?.["@opencode/plugin"] !== "2.0.25"
    || Object.keys(packageJson.dependencies).length !== 1) throw new Error("invalid OpenCode presentation package contract");
}

export function writeOpenCodePresentationArtifact(runtimeRoot, artifact) {
  const targetRoot = path.join(runtimeRoot, OPENCODE_PRESENTATION_RUNTIME_RELATIVE);
  fs.mkdirSync(targetRoot, { recursive: true });
  for (const entry of fs.readdirSync(targetRoot, { withFileTypes: true })) {
    if (entry.isFile() && (GENERATED_FILES.includes(entry.name) || GENERATED_META_FILES.includes(entry.name))) continue;
    fs.rmSync(path.join(targetRoot, entry.name), { recursive: true, force: true });
  }
  for (const [relativePath, content] of artifact.files) {
    fs.writeFileSync(path.join(targetRoot, relativePath), content);
  }
}

export function unexpectedOpenCodePresentationFiles(runtimeRoot) {
  const targetRoot = path.join(runtimeRoot, OPENCODE_PRESENTATION_RUNTIME_RELATIVE);
  if (!fs.existsSync(targetRoot)) return ["<missing-directory>"];
  return fs.readdirSync(targetRoot, { withFileTypes: true })
    .filter((entry) => !(entry.isFile() && (GENERATED_FILES.includes(entry.name) || GENERATED_META_FILES.includes(entry.name))))
    .map((entry) => entry.name)
    .sort();
}
