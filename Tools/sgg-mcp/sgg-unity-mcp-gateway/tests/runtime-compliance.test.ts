import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";

import { beforeAll, describe, expect, it } from "vitest";

import {
  ALEXEY_TARANOV_MIT,
  assertExactComponentNames,
  createGeneratedArtifacts,
  EXPECTED_RUNTIME_COMPONENTS,
  loadRuntimeCompliance,
  normalizeLegalText,
  packageLocationsFromMetafile,
  renderCycloneDx,
  renderThirdPartyNotices,
  REVIEW_REQUIRED,
  RUNTIME_COMPONENT_COUNT,
  validatePackageRecord,
  writeOrCheck,
// @ts-expect-error The directly executable Node script has no separate declaration file.
} from "../scripts/runtime-compliance.mjs";

const root = process.cwd();
const expectedNoticeSha256 = "8e6df03f0ebba1bc7a8498024cd2365cd1ef044708e6087c441512075d724f92";
const expectedSbomSha256 = sha256(fs.readFileSync(path.join(root, "sbom.cdx.json"), "utf8"));
type RuntimeComponent = {
  name: string;
  version: string;
  resolved: string;
  integrity: string;
  integrityHex: string;
  legalFiles: Array<{ name: string; text: string }>;
};
const expectedNames = [
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

let artifacts: Awaited<ReturnType<typeof createGeneratedArtifacts>>;

beforeAll(async () => {
  artifacts = await createGeneratedArtifacts(root);
}, 30000);

describe("deterministic runtime compliance", () => {
  it("pins the exact current esbuild metafile closure", () => {
    const names = artifacts.compliance.components.map((component: RuntimeComponent) => component.name);
    expect(RUNTIME_COMPONENT_COUNT).toBe(22);
    expect(EXPECTED_RUNTIME_COMPONENTS).toEqual(expectedNames);
    expect(names).toEqual(expectedNames);
    expect(names).toContain("@modelcontextprotocol/sdk");
    expect(names).toContain("@modelcontextprotocol/server");
    expect(names).toContain("@modelcontextprotocol/node");
    expect(names).toContain("zod");
    expect(names).toContain("@romanilyin/canonicalpath");
    expect(names).not.toContain("esbuild");
  });

  it("uses exact lock metadata, full installed legal text, and SHA-512 hex", () => {
    for (const component of artifacts.compliance.components) {
      expect(component.version).toBeTruthy();
      expect(component.resolved).toMatch(/^https:\/\/registry\.npmjs\.org\//);
      expect(component.integrity).toMatch(/^sha512-/);
      expect(component.integrityHex).toMatch(/^[0-9a-f]{128}$/);
      expect(artifacts.notices).toContain(`Resolved: \`${component.resolved}\``);
      expect(artifacts.notices).toContain(`Integrity: \`${component.integrity}\``);
      expect(component.legalFiles.length).toBeGreaterThan(0);
      for (const legalFile of component.legalFiles) {
        expect(artifacts.notices).toContain(`Legal source: \`${legalFile.name}\``);
        expect(artifacts.notices).toContain(legalFile.text);
      }
    }
  });

  it("reproduces byte-identical LF UTF-8 output in code-point order", async () => {
    const second = await createGeneratedArtifacts(root);
    expect(second.notices).toBe(artifacts.notices);
    expect(second.sbom).toBe(artifacts.sbom);
    expect(Buffer.from(artifacts.notices, "utf8").toString("utf8")).toBe(artifacts.notices);
    expect(artifacts.notices).not.toContain("\r");
    expect(artifacts.sbom).not.toContain("\r");
    expect(artifacts.notices.endsWith("\n")).toBe(true);
    expect(artifacts.sbom.endsWith("\n")).toBe(true);
    expect(artifacts.notices.indexOf("### @hono/node-server")).toBeLessThan(artifacts.notices.indexOf("### ajv"));
    expect(sha256(artifacts.notices)).toBe(expectedNoticeSha256);
    expect(sha256(artifacts.sbom)).toBe(expectedSbomSha256);
    expect(fs.readFileSync(path.join(root, "sbom.cdx.json"), "utf8")).toBe(artifacts.sbom);
    expect(fs.readFileSync(path.join(root, "unity/Packages/com.sungeargames.unity-mcp/Editor/GatewayRuntime/THIRD_PARTY_NOTICES.md"), "utf8")).toBe(artifacts.notices);
    expect(fs.readFileSync(path.join(root, "unity/Packages/com.sungeargames.unity-mcp/Editor/GatewayRuntime/sbom.cdx.json"), "utf8")).toBe(artifacts.sbom);
  }, 30000);

  it("normalizes legal inputs and keeps generated text fixtures whitespace-clean", () => {
    expect(normalizeLegalText("first  \r\nsecond\t\r\n\r\n")).toBe("first\nsecond");
    expect(artifacts.notices).not.toMatch(/[ \t]+$/m);
    expect(artifacts.notices.endsWith("\n")).toBe(true);
    for (const name of ["THIRD_PARTY_NOTICES.md.meta", "sbom.cdx.json.meta"]) {
      const meta = fs.readFileSync(path.join(root, "unity/Packages/com.sungeargames.unity-mcp/Editor/GatewayRuntime", name), "utf8");
      expect(meta).not.toMatch(/[ \t]+$/m);
      expect(meta.endsWith("\n")).toBe(true);
    }
  });

  it("emits CycloneDX 1.6 with root, runtime libraries, tool metadata, and no serial or timestamp", () => {
    const sbom = JSON.parse(artifacts.sbom);
    expect(sbom).toMatchObject({
      $schema: "https://cyclonedx.org/schema/bom-1.6.schema.json",
      bomFormat: "CycloneDX",
      specVersion: "1.6",
      version: 1,
    });
    expect(sbom.serialNumber).toBeUndefined();
    expect(sbom.metadata.timestamp).toBeUndefined();
    expect(sbom.metadata.component).toMatchObject({
      type: "application",
      name: "sgg-unity-mcp-gateway",
      version: JSON.parse(fs.readFileSync(path.join(root, "package.json"), "utf8")).version,
    });
    expect(sbom.components).toHaveLength(expectedNames.length);
    expect(sbom.components.map((component: { name: string }) => component.name)).toEqual(expectedNames);
    expect(sbom.components.every((component: { type: string; purl: string; hashes: unknown[] }) =>
      component.type === "library" && component.purl.startsWith("pkg:npm/") && component.hashes.length === 1)).toBe(true);
    expect(sbom.metadata.tools.components).toHaveLength(1);
    expect(sbom.metadata.tools.components[0]).toMatchObject({ name: "esbuild", type: "application" });
    expect(sbom.components.some((component: { name: string }) => component.name === "esbuild")).toBe(false);
    expect(sbom.dependencies).toHaveLength(expectedNames.length + 1);
  });

  it("includes non-cyclic CLI and lock hashes without machine paths or generated timestamps", () => {
    const rootProperties = JSON.parse(artifacts.sbom).metadata.component.properties;
    expect(rootProperties).toEqual([
      { name: "sgg:artifact:gateway-cli:sha256", value: expect.stringMatching(/^[0-9a-f]{64}$/) },
      { name: "sgg:source:package-lock:sha256", value: expect.stringMatching(/^[0-9a-f]{64}$/) },
    ]);
    expect(artifacts.sbom).not.toContain(root);
    expect(artifacts.notices).not.toContain(root);
    expect(artifacts.sbom).not.toMatch(/"(?:timestamp|serialNumber)"/);
  });

  it("preserves the Noto OFL, Alexey Taranov MIT notice, and EULA boundary", () => {
    const noto = fs.readFileSync(path.join(root, "unity/Packages/com.sungeargames.unity-mcp/Editor/UI/Fonts/NotoSans-OFL.txt"), "utf8").trimEnd();
    expect(artifacts.notices).toContain(noto);
    expect(artifacts.notices).toContain(ALEXEY_TARANOV_MIT);
    expect(artifacts.notices).toContain("does not replace, override, or restrict the licenses");
  });

  it.each([
    ["missing version", { version: undefined }, validInstalled(), validLegal()],
    ["missing resolved", { resolved: undefined }, validInstalled(), validLegal()],
    ["missing integrity", { integrity: undefined }, validInstalled(), validLegal()],
    ["version mismatch", {}, { ...validInstalled(), version: "0.0.0" }, validLegal()],
    ["license mismatch", {}, { ...validInstalled(), license: "ISC" }, validLegal()],
    ["unknown license", { license: "Unknown" }, { ...validInstalled(), license: "Unknown" }, validLegal()],
    ["unlicensed", { license: "UNLICENSED" }, { ...validInstalled(), license: "UNLICENSED" }, validLegal()],
    ["LicenseRef", { license: "LicenseRef-Custom" }, { ...validInstalled(), license: "LicenseRef-Custom" }, validLegal()],
    ["SEE LICENSE", { license: "SEE LICENSE IN LICENSE" }, { ...validInstalled(), license: "SEE LICENSE IN LICENSE" }, validLegal()],
    ["missing legal file", {}, validInstalled(), []],
  ])("rejects %s with the fixed review marker", (_label, lockPatch, installed, legalFiles) => {
    expect(() => validatePackageRecord({
      name: "fixture",
      lockRecord: { ...validLock(), ...lockPatch },
      installedPackage: installed,
      legalFiles,
    })).toThrow(REVIEW_REQUIRED);
  });

  it("rejects unknown metafile packages and missing or unexpected generated components", () => {
    expect(packageLocationsFromMetafile({ inputs: { "node_modules/unknown/index.js": { bytes: 1 } } })).toEqual([
      { name: "unknown", lockKey: "node_modules/unknown" },
    ]);
    expect(() => assertExactComponentNames(["a", "unexpected"], ["a"])).toThrow(REVIEW_REQUIRED);
    expect(() => assertExactComponentNames(["a"], ["a", "missing"])).toThrow(REVIEW_REQUIRED);
    expect(() => validatePackageRecord({ name: "unknown", lockRecord: undefined, installedPackage: {}, legalFiles: [] })).toThrow(REVIEW_REQUIRED);
  });

  it("renders exclusively from the validated model", async () => {
    const compliance = await loadRuntimeCompliance(root);
    compliance.notoText = artifacts.compliance.notoText;
    expect(renderThirdPartyNotices(compliance)).toBe(artifacts.notices);
    expect(renderCycloneDx(compliance)).toBe(artifacts.sbom);
  }, 30000);

  it("fails compliance checks for missing and drifted generated files", () => {
    const fixtureRoot = fs.mkdtempSync(path.join(process.env.TMPDIR ?? "/tmp", "sgg-compliance-check-"));
    try {
      expect(() => writeOrCheck(fixtureRoot, ["missing.txt"], "expected\n", true)).toThrow(REVIEW_REQUIRED);
      fs.writeFileSync(path.join(fixtureRoot, "missing.txt"), "drifted\n");
      expect(() => writeOrCheck(fixtureRoot, ["missing.txt"], "expected\n", true)).toThrow(REVIEW_REQUIRED);
    } finally {
      fs.rmSync(fixtureRoot, { recursive: true, force: true });
    }
  });
});

function validLock() {
  return {
    version: "1.0.0",
    license: "MIT",
    resolved: "https://registry.npmjs.org/fixture/-/fixture-1.0.0.tgz",
    integrity: `sha512-${Buffer.alloc(64).toString("base64")}`,
  };
}

function validInstalled() {
  return { name: "fixture", version: "1.0.0", license: "MIT" };
}

function validLegal() {
  return [{ name: "LICENSE", text: "MIT License" }];
}

function sha256(value: string) {
  return crypto.createHash("sha256").update(value, "utf8").digest("hex");
}
