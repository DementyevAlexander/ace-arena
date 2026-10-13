import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { afterEach, describe, expect, it } from "vitest";

// @ts-expect-error The directly executable Node helper has no separate declaration file.
import { checkChangelog } from "../scripts/check-changelog.mjs";
// @ts-expect-error The directly executable Node helper has no separate declaration file.
import { checkVersion } from "../scripts/check-version.mjs";

const temporaryDirectories: string[] = [];
const VERSION = "2024.2.29-2";
const NOW = new Date("2024-03-01T00:00:00.000Z");

function write(root: string, relativePath: string, value: string) {
  const filePath = path.join(root, relativePath);
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  fs.writeFileSync(filePath, value);
}

function json(root: string, relativePath: string, value: unknown) {
  write(root, relativePath, `${JSON.stringify(value, null, 2)}\n`);
}

function fixture(version = VERSION) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "versioning-check-"));
  temporaryDirectories.push(root);
  json(root, "package.json", {
    name: "sgg-unity-mcp-gateway",
    version,
    dependencies: { "@modelcontextprotocol/node": "2.0.0", "@modelcontextprotocol/sdk": "1.29.0", "@modelcontextprotocol/server": "2.0.0" },
    devDependencies: { "@modelcontextprotocol/client": "2.0.0" },
  });
  json(root, "package-lock.json", {
    version,
    packages: {
      "": {
        version,
        dependencies: { "@modelcontextprotocol/node": "2.0.0", "@modelcontextprotocol/sdk": "1.29.0", "@modelcontextprotocol/server": "2.0.0" },
        devDependencies: { "@modelcontextprotocol/client": "2.0.0" },
      },
      "node_modules/@modelcontextprotocol/client": { version: "2.0.0" },
      "node_modules/@modelcontextprotocol/node": { version: "2.0.0" },
      "node_modules/@modelcontextprotocol/sdk": { version: "1.29.0" },
      "node_modules/@modelcontextprotocol/server": { version: "2.0.0" },
    },
  });
  json(root, "unity/Packages/com.sungeargames.unity-mcp/package.json", { version });
  write(root, "unity/Packages/com.sungeargames.unity-mcp/Editor/Bridge/UnityBridgeRuntime.cs", `public const string PackageVersion = "${version}";\n`);
  json(root, "unity/Packages/com.sungeargames.unity-mcp/Editor/GatewayRuntime/gateway-runtime.json", { version });
  const rootPurl = `pkg:npm/sgg-unity-mcp-gateway@${version}`;
  const sbom = { metadata: { component: { version, "bom-ref": rootPurl, purl: rootPurl } }, dependencies: [{ ref: rootPurl, dependsOn: ["pkg:npm/dependency@1.0.0"] }] };
  json(root, "sbom.cdx.json", sbom);
  json(root, "unity/Packages/com.sungeargames.unity-mcp/Editor/GatewayRuntime/sbom.cdx.json", sbom);
  write(root, "docs/en/maintainers/manual-checks.md", `# Manual checks

## Dedicated Unity MCP Smoke

<!-- sgg-current-version-claim:manual.en.smoke.environment -->
SGG_UNITY_SMOKE_PACKAGE_VERSION             exact current package version (${version})
<!-- sgg-current-version-claim:manual.en.smoke.command -->
SGG_UNITY_SMOKE_PACKAGE_VERSION='${version}' \\\n`);
  write(root, "docs/ru/maintainers/manual-checks.md", `# Ручные проверки

## Dedicated MCP Smoke

<!-- sgg-current-version-claim:manual.ru.smoke.environment -->
SGG_UNITY_SMOKE_PACKAGE_VERSION             точная текущая package version (${version})
<!-- sgg-current-version-claim:manual.ru.smoke.command -->
SGG_UNITY_SMOKE_PACKAGE_VERSION='${version}' \\\n`);
  write(root, "docs/en/maintainers/versioning.md", `# Versioning

## Synchronized Surfaces

<!-- sgg-current-version-claim:versioning.en.current -->
Current package version: \`${version}\`.
Historical version: 2020.1.1-1
`);
  write(root, "docs/ru/maintainers/versioning.md", `# Версионирование

## Synchronized Surfaces

<!-- sgg-current-version-claim:versioning.ru.current -->
Текущая версия package: \`${version}\`.
Историческая версия: 2020.1.1-1
`);
  write(root, "CHANGELOG.md", `# Changelog

## [${version}] - 2024-02-29

### Added

- Added a public capability.

### Changed

- Changed release validation.

### Fixed

- Fixed metadata drift.

## [2024.2.29-1] - 2024-02-29

### Added

- Added the previous public capability.

### Changed

- Changed the previous release validation.

### Fixed

- Fixed the previous metadata drift.

<!-- sgg-changelog-legacy-boundary:2024.2.28-3 -->
## Historical development journal

- Added an earlier release and bumped the gateway package version to \`2024.2.28-3\`.
`);
  return root;
}

function mutateJson(root: string, relativePath: string, mutate: (value: any) => void) {
  const filePath = path.join(root, relativePath);
  const value = JSON.parse(fs.readFileSync(filePath, "utf8"));
  mutate(value);
  json(root, relativePath, value);
}

function replaceOccurrence(root: string, relativePath: string, occurrence: number, from: string, to: string) {
  const filePath = path.join(root, relativePath);
  let seen = 0;
  const text = fs.readFileSync(filePath, "utf8").replaceAll(from, (match) => ++seen === occurrence ? to : match);
  fs.writeFileSync(filePath, text);
}

afterEach(() => {
  for (const directory of temporaryDirectories.splice(0)) fs.rmSync(directory, { recursive: true, force: true });
});

describe("calendar build version contract", () => {
  it("accepts a real leap date with complete metadata and changelog parity", () => {
    const root = fixture();
    expect(checkVersion({ root, now: NOW })).toEqual([]);
    expect(checkChangelog({ root })).toEqual([]);
  });

  it.each([
    "2023.2.29-1",
    "2024.2.30-1",
    "2024.0.1-1",
    "2024.01.1-1",
    "2024.1.01-1",
    "2024.1.1-0",
    "02024.1.1-1",
    "2024.1.1-123456789012345678901",
  ])("rejects invalid calendar build %s", (version) => {
    expect(checkVersion({ root: fixture(version), now: NOW }).join(" ")).toContain("real Gregorian date");
  });

  it("rejects future UTC dates unless the exact release-prep date is allowed", () => {
    const root = fixture("2024.3.2-1");
    expect(checkVersion({ root, now: NOW }).join(" ")).toContain("after current UTC date");
    expect(checkVersion({ root, now: NOW, allowFutureDate: "2024.3.1" }).join(" ")).toContain("after current UTC date");
    expect(checkVersion({ root, now: NOW, allowFutureDate: "2024.3.2" })).toEqual([]);
  });

  it("orders builds above Number.MAX_SAFE_INTEGER exactly", () => {
    const root = fixture("2024.2.29-9007199254740993");
    const changelogPath = path.join(root, "CHANGELOG.md");
    fs.writeFileSync(changelogPath, fs.readFileSync(changelogPath, "utf8").replace("## [2024.2.29-1]", "## [2024.2.29-9007199254740992]"));
    expect(checkVersion({ root, now: NOW })).toEqual([]);
    expect(checkChangelog({ root })).toEqual([]);
    fs.writeFileSync(changelogPath, fs.readFileSync(changelogPath, "utf8").replace("9007199254740992", "9007199254740994"));
    expect(checkChangelog({ root }).join(" ")).toContain("out of descending");
  });

  it.each([
    ["root package", (root: string) => mutateJson(root, "package.json", (value) => { value.version = "2024.2.29-9"; })],
    ["lock root", (root: string) => mutateJson(root, "package-lock.json", (value) => { value.version = "2024.2.29-9"; })],
    ["lock package", (root: string) => mutateJson(root, "package-lock.json", (value) => { value.packages[""].version = "2024.2.29-9"; })],
    ["UPM", (root: string) => mutateJson(root, "unity/Packages/com.sungeargames.unity-mcp/package.json", (value) => { value.version = "2024.2.29-9"; })],
    ["Unity constant", (root: string) => write(root, "unity/Packages/com.sungeargames.unity-mcp/Editor/Bridge/UnityBridgeRuntime.cs", 'public const string PackageVersion = "2024.2.29-9";\n')],
    ["runtime manifest", (root: string) => mutateJson(root, "unity/Packages/com.sungeargames.unity-mcp/Editor/GatewayRuntime/gateway-runtime.json", (value) => { value.version = "2024.2.29-9"; })],
  ])("rejects %s version drift", (_name, mutate) => {
    const root = fixture();
    mutate(root);
    expect(checkVersion({ root, now: NOW }).some((error: string) => error.includes("must match package.json"))).toBe(true);
  });

  it.each([
    ["English manual wording", "docs/en/maintainers/manual-checks.md", 1],
    ["English manual command", "docs/en/maintainers/manual-checks.md", 2],
    ["Russian manual wording", "docs/ru/maintainers/manual-checks.md", 1],
    ["Russian manual command", "docs/ru/maintainers/manual-checks.md", 2],
    ["English versioning", "docs/en/maintainers/versioning.md", 1],
    ["Russian versioning", "docs/ru/maintainers/versioning.md", 1],
  ])("rejects independent stale current claim in %s", (_name, relativePath, occurrence) => {
    const root = fixture();
    replaceOccurrence(root, relativePath as string, occurrence as number, VERSION, "2024.2.29-9");
    expect(checkVersion({ root, now: NOW }).join(" ")).toContain("must match package.json");
  });

  it("rejects a removed stable claim ID", () => {
    const root = fixture();
    replaceOccurrence(root, "docs/en/maintainers/manual-checks.md", 1, "<!-- sgg-current-version-claim:manual.en.smoke.environment -->", "<!-- removed-current-version-claim -->");
    expect(checkVersion({ root, now: NOW }).join(" ")).toContain("missing current-version claim ID manual.en.smoke.environment");
  });

  it("rejects duplicate and unknown claim IDs", () => {
    const root = fixture();
    const filePath = path.join(root, "docs/en/maintainers/manual-checks.md");
    const original = fs.readFileSync(filePath, "utf8");
    fs.writeFileSync(filePath, `${original}\n<!-- sgg-current-version-claim:manual.en.smoke.environment -->\nSGG_UNITY_SMOKE_PACKAGE_VERSION             exact current package version (${VERSION})\n`);
    expect(checkVersion({ root, now: NOW }).join(" ")).toContain("duplicate current-version claim ID manual.en.smoke.environment");
    fs.writeFileSync(filePath, original.replace("manual.en.smoke.environment", "manual.en.smoke.unknown"));
    expect(checkVersion({ root, now: NOW }).join(" ")).toContain("unknown current-version claim ID manual.en.smoke.unknown");
  });

  it("rejects IDs swapped between exact semantic claims", () => {
    const root = fixture();
    const filePath = path.join(root, "docs/en/maintainers/manual-checks.md");
    const original = fs.readFileSync(filePath, "utf8");
    fs.writeFileSync(filePath, original
      .replace("manual.en.smoke.environment", "claim-swap-placeholder")
      .replace("manual.en.smoke.command", "manual.en.smoke.environment")
      .replace("claim-swap-placeholder", "manual.en.smoke.command"));
    expect(checkVersion({ root, now: NOW }).join(" ")).toContain("is not adjacent to its exact semantic line");
  });

  it("rejects a correct marker and version relocated to another section or file", () => {
    const root = fixture();
    const versioningPath = path.join(root, "docs/en/maintainers/versioning.md");
    const pair = `<!-- sgg-current-version-claim:versioning.en.current -->\nCurrent package version: \`${VERSION}\`.\n`;
    fs.writeFileSync(versioningPath, fs.readFileSync(versioningPath, "utf8").replace(pair, "") + `\n## Changelog\n\n${pair}`);
    expect(checkVersion({ root, now: NOW }).join(" ")).toContain("belongs in section 'Synchronized Surfaces'");

    const manualPath = path.join(root, "docs/en/maintainers/manual-checks.md");
    const russianPath = path.join(root, "docs/ru/maintainers/manual-checks.md");
    const manualPair = `<!-- sgg-current-version-claim:manual.en.smoke.environment -->\nSGG_UNITY_SMOKE_PACKAGE_VERSION             exact current package version (${VERSION})\n`;
    fs.writeFileSync(manualPath, fs.readFileSync(manualPath, "utf8").replace(manualPair, ""));
    fs.appendFileSync(russianPath, `\n${manualPair}`);
    expect(checkVersion({ root, now: NOW }).join(" ")).toContain("belongs in docs/en/maintainers/manual-checks.md");

    const freshRoot = fixture();
    const freshManual = path.join(freshRoot, "docs/en/maintainers/manual-checks.md");
    fs.writeFileSync(freshManual, fs.readFileSync(freshManual, "utf8").replace(manualPair, ""));
    write(freshRoot, "README.md", `# Decoy\n\n${manualPair}`);
    expect(checkVersion({ root: freshRoot, now: NOW }).join(" ")).toContain("README.md:3: current-version claim manual.en.smoke.environment belongs in docs/en/maintainers/manual-checks.md");
  });

  it("rejects a stale original claim even with a correct decoy", () => {
    const root = fixture();
    const filePath = path.join(root, "docs/en/maintainers/versioning.md");
    const original = fs.readFileSync(filePath, "utf8").replace(`Current package version: \`${VERSION}\`.`, "Current package version: `2024.2.29-9`.");
    const decoy = `<!-- sgg-current-version-claim:versioning.en.current -->\nCurrent package version: \`${VERSION}\`.\n`;
    fs.writeFileSync(filePath, `${original}\n## Decoy\n\n${decoy}`);
    const errors = checkVersion({ root, now: NOW }).join(" ");
    expect(errors).toContain("current-version claim versioning.en.current \"2024.2.29-9\" must match package.json");
    expect(errors).toContain("duplicate current-version claim ID versioning.en.current");
  });

  it.each([
    ["metadata component version", (value: any) => { value.metadata.component.version = "2024.2.29-9"; }],
    ["component bom-ref", (value: any) => { value.metadata.component["bom-ref"] = "pkg:npm/sgg-unity-mcp-gateway@2024.2.29-9"; }],
    ["component purl", (value: any) => { value.metadata.component.purl = "pkg:npm/sgg-unity-mcp-gateway@2024.2.29-9"; }],
    ["dependency root ref", (value: any) => { value.dependencies[0].ref = "pkg:npm/sgg-unity-mcp-gateway@2024.2.29-9"; }],
    ["additional root reference", (value: any) => { value.dependencies[0].dependsOn.push("pkg:npm/sgg-unity-mcp-gateway@2024.2.29-9"); }],
    ["malformed root reference", (value: any) => { value.dependencies[0].dependsOn.push("pkg:npm/sgg-unity-mcp-gateway@2024.2.29%ZZ"); }],
    ["malformed encoded purl", (value: any) => { value.metadata.component.purl = "pkg:npm/sgg-unity-mcp-gateway@2024.2.29%ZZ"; }],
  ])("rejects root SBOM %s drift", (_name, mutate) => {
    const root = fixture();
    mutateJson(root, "sbom.cdx.json", mutate as (value: any) => void);
    expect(checkVersion({ root, now: NOW }).some((error: string) => error.startsWith("sbom.cdx.json"))).toBe(true);
  });

  it.each([
    ["metadata component version", (value: any) => { value.metadata.component.version = "2024.2.29-9"; }],
    ["component bom-ref", (value: any) => { value.metadata.component["bom-ref"] = "pkg:npm/sgg-unity-mcp-gateway@2024.2.29-9"; }],
    ["component purl", (value: any) => { value.metadata.component.purl = "pkg:npm/sgg-unity-mcp-gateway@2024.2.29-9"; }],
    ["dependency root ref", (value: any) => { value.dependencies[0].ref = "pkg:npm/sgg-unity-mcp-gateway@2024.2.29-9"; }],
    ["additional root reference", (value: any) => { value.dependencies[0].dependsOn.push("pkg:npm/sgg-unity-mcp-gateway@2024.2.29-9"); }],
    ["malformed root reference", (value: any) => { value.dependencies[0].dependsOn.push("pkg:npm/sgg-unity-mcp-gateway@2024.2.29%ZZ"); }],
  ])("rejects embedded SBOM %s drift", (_name, mutate) => {
    const root = fixture();
    mutateJson(root, "unity/Packages/com.sungeargames.unity-mcp/Editor/GatewayRuntime/sbom.cdx.json", mutate as (value: any) => void);
    expect(checkVersion({ root, now: NOW }).some((error: string) => error.startsWith("bundled sbom.cdx.json"))).toBe(true);
  });
});

describe("public changelog contract", () => {
  it.each([
    ["missing release", (text: string) => text.replace(/^## \[[^\n]+$/m, "")],
    ["duplicate", (text: string) => `${text}- Duplicate and bumped the gateway package version to \`2024.2.28-3\`.\n`],
    ["out of order", (text: string) => text.replace("2024.2.28-3", "2024.3.1-1")],
    ["wrong top version", (text: string) => text.replace("## [2024.2.29-2]", "## [2024.2.29-3]")],
    ["wrong top date", (text: string) => text.replace("2024-02-29", "2024-02-28")],
    ["empty section", (text: string) => text.replace("### Fixed\n\n- Fixed metadata drift.", "### Fixed\n")],
    ["unfinished", (text: string) => text.replace("Fixed metadata drift.", "TODO: finish metadata drift.")],
    ["unreleased section", (text: string) => text.replace("## [2024.2.29-1]", "## Unreleased\n\n## [2024.2.29-1]")],
    ["internal noise", (text: string) => text.replace("Fixed metadata drift.", "Fixed HARDEN-14 implementation slice.")],
  ])("rejects %s changelog mutation", (_name, mutate) => {
    const root = fixture();
    const filePath = path.join(root, "CHANGELOG.md");
    fs.writeFileSync(filePath, mutate(fs.readFileSync(filePath, "utf8")));
    expect(checkChangelog({ root }).length).toBeGreaterThan(0);
  });

  it("rejects a package version that predates the prior release", () => {
    const root = fixture();
    const filePath = path.join(root, "CHANGELOG.md");
    fs.writeFileSync(filePath, fs.readFileSync(filePath, "utf8").replace("2024.2.29-1", "2024.3.1-1"));
    expect(checkChangelog({ root }).join(" ")).toMatch(/out of descending|predates/);
  });

  it("rejects a prior structured release whose display date differs from its version", () => {
    const root = fixture();
    const filePath = path.join(root, "CHANGELOG.md");
    fs.writeFileSync(filePath, fs.readFileSync(filePath, "utf8").replace("## [2024.2.29-1] - 2024-02-29", "## [2024.2.29-1] - 2024-02-28"));
    expect(checkChangelog({ root }).join(" ")).toContain("display date 2024-02-28 must match version date 2024-02-29");
  });

  it("rejects a malformed current insertion instead of skipping it", () => {
    const root = fixture();
    const filePath = path.join(root, "CHANGELOG.md");
    fs.writeFileSync(filePath, fs.readFileSync(filePath, "utf8").replace("## [2024.2.29-2]", "## [not-a-version] - 2024-02-29\n\n## [2024.2.29-2]"));
    expect(checkChangelog({ root }).join(" ")).toContain("changelog version is invalid: not-a-version");
  });

  it("rejects moved, mismatched, duplicated, and content-bypassed legacy boundaries", () => {
    const root = fixture();
    const filePath = path.join(root, "CHANGELOG.md");
    const original = fs.readFileSync(filePath, "utf8");
    fs.writeFileSync(filePath, original.replace("sgg-changelog-legacy-boundary:2024.2.28-3", "sgg-changelog-legacy-boundary:2024.2.28-2"));
    expect(checkChangelog({ root }).join(" ")).toContain("must match the first historical build entry");
    fs.writeFileSync(filePath, original.replace("## Historical development journal\n", "## Historical development journal\n\n## [bad-current] - 2024-02-28\n"));
    expect(checkChangelog({ root }).join(" ")).toContain("legacy changelog content must not contain structured releases");
    const boundary = "<!-- sgg-changelog-legacy-boundary:2024.2.28-3 -->\n## Historical development journal\n";
    const historicalBullet = "- Added an earlier release and bumped the gateway package version to `2024.2.28-3`.";
    fs.writeFileSync(filePath, original.replace(boundary, "").replace(historicalBullet, `${historicalBullet}\n\n${boundary}`));
    expect(checkChangelog({ root }).join(" ")).toContain("legacy journal build entry above the legacy boundary");
    fs.writeFileSync(filePath, `${original}\n<!-- sgg-changelog-legacy-boundary:2024.2.28-3 -->\n## Historical development journal\n`);
    expect(checkChangelog({ root }).join(" ")).toContain("exactly one machine-checkable legacy boundary");
  });
});
