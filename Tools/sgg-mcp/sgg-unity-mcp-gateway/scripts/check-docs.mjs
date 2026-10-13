#!/usr/bin/env node

import fs from "node:fs";
import path from "node:path";

const root = process.cwd();
const fullRepository = fs.existsSync(path.join(root, "package.json")) && fs.existsSync(path.join(root, "unity"));
const errors = [];

const requiredRootFiles = [
  "README.md",
  "README.ru.md",
  "AGENTS.md",
  "AGENTS.ru.md",
  "docs/en/README.md",
  "docs/ru/README.md",
];

const requiredCanonicalPages = [
  "docs/en/maintainers/manual-checks.md",
  "docs/ru/maintainers/manual-checks.md",
  "docs/en/maintainers/versioning.md",
  "docs/ru/maintainers/versioning.md",
];

const deletedStubPaths = [
  "docs/advanced-unity-roadmap.md",
  "docs/agent-recipes.md",
  "docs/agent-usage.md",
  "docs/assistant-integration.md",
  "docs/canvas-to-uitk-migration.md",
  "docs/commands.md",
  "docs/comparison.md",
  "docs/demo-script.md",
  "docs/diagnostics.md",
  "docs/domain-command-governance.md",
  "docs/extension-governance.md",
  "docs/getting-started.md",
  "docs/installation.md",
  "docs/license-faq.md",
  "docs/manual-checks.md",
  "docs/manual-installation.md",
  "docs/module-examples.md",
  "docs/opencode-setup.md",
  "docs/plans.md",
  "docs/release-readiness.md",
  "docs/safe-profiles.md",
  "docs/security.md",
  "docs/skills.md",
  "docs/troubleshooting.md",
  "docs/unity-ai-assistant-compatibility.md",
  "docs/validation-report.md",
  "docs/versioning.md",
  "docs/wsl-windows-setup.md",
  "docs/ru/advanced-unity-roadmap.ru.md",
  "docs/ru/agent-recipes.ru.md",
  "docs/ru/agent-usage.ru.md",
  "docs/ru/assistant-integration.ru.md",
  "docs/ru/canvas-to-uitk-migration.ru.md",
  "docs/ru/commands.ru.md",
  "docs/ru/comparison.ru.md",
  "docs/ru/demo-script.ru.md",
  "docs/ru/diagnostics.ru.md",
  "docs/ru/domain-command-governance.ru.md",
  "docs/ru/extension-governance.ru.md",
  "docs/ru/getting-started.ru.md",
  "docs/ru/installation.ru.md",
  "docs/ru/license-faq.ru.md",
  "docs/ru/manual-checks.ru.md",
  "docs/ru/manual-installation.ru.md",
  "docs/ru/module-examples.ru.md",
  "docs/ru/opencode-setup.ru.md",
  "docs/ru/plans.ru.md",
  "docs/ru/release-readiness.ru.md",
  "docs/ru/safe-profiles.ru.md",
  "docs/ru/security.ru.md",
  "docs/ru/skills.ru.md",
  "docs/ru/troubleshooting.ru.md",
  "docs/ru/unity-ai-assistant-compatibility.ru.md",
  "docs/ru/validation-report.ru.md",
  "docs/ru/versioning.ru.md",
  "docs/ru/wsl-windows-setup.ru.md",
];

const carriedRepositoryTargets = new Set([
  "CHANGELOG.md",
  "CONTRIBUTING.md",
  "CONTRIBUTING.ru.md",
  "LICENSE.md",
  "LICENSE.ru.md",
  "NOTICE.md",
  "NOTICE.ru.md",
  "SECURITY.md",
  "SUPPORT.md",
  "SUPPORT.ru.md",
  "docs/commands.generated.md",
  "docs/ru/commands.generated.ru.md",
]);

function fail(message) {
  errors.push(message);
}

function exists(relativePath) {
  return fs.existsSync(path.join(root, relativePath));
}

function requireFile(relativePath) {
  if (!exists(relativePath)) fail(`missing ${relativePath}`);
}

for (const file of requiredRootFiles) requireFile(file);
for (const file of requiredCanonicalPages) requireFile(file);
for (const file of deletedStubPaths) {
  if (exists(file)) fail(`deleted documentation stub must not exist: ${file}`);
}
if (fullRepository) {
  requireFile("docs/commands.generated.md");
  requireFile("docs/ru/commands.generated.ru.md");
}

function markdownFiles(directory) {
  if (!fs.existsSync(directory)) return [];
  const result = [];
  const visit = (current) => {
    for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
      const absolute = path.join(current, entry.name);
      if (entry.isDirectory()) visit(absolute);
      else if (entry.isFile() && entry.name.endsWith(".md")) result.push(absolute);
    }
  };
  visit(directory);
  return result.sort();
}

function relativeFromRoot(absolutePath) {
  return path.relative(root, absolutePath).replaceAll(path.sep, "/");
}

const enRoot = path.join(root, "docs", "en");
const ruRoot = path.join(root, "docs", "ru");
const enFiles = markdownFiles(enRoot);
const canonicalRuFiles = markdownFiles(ruRoot).filter((absolute) => !absolute.endsWith(".ru.md"));

for (const english of enFiles) {
  const relative = path.relative(enRoot, english);
  const russian = path.join(ruRoot, relative);
  if (!fs.existsSync(russian)) fail(`${relativeFromRoot(english)} has no Russian counterpart ${relativeFromRoot(russian)}`);
}

for (const russian of canonicalRuFiles) {
  const relative = path.relative(ruRoot, russian);
  const english = path.join(enRoot, relative);
  if (!fs.existsSync(english)) fail(`${relativeFromRoot(russian)} has no English counterpart ${relativeFromRoot(english)}`);
}

function checkLanguageSwitcher(absolutePath) {
  const text = fs.readFileSync(absolutePath, "utf8");
  const head = text.split(/\r?\n/).slice(0, 12).join("\n");
  if (!head.includes("[English]") || !head.includes("[Русский]")) {
    fail(`${relativeFromRoot(absolutePath)} must contain English/Russian navigation near the top`);
  }
}

for (const file of enFiles) checkLanguageSwitcher(file);
for (const file of canonicalRuFiles) checkLanguageSwitcher(file);

const allDocs = [
  ...["README.md", "README.ru.md", "AGENTS.md", "AGENTS.ru.md"].filter(exists).map((p) => path.join(root, p)),
  ...markdownFiles(path.join(root, "docs")),
];

function stripCodeFences(text) {
  return text.replace(/```[\s\S]*?```/g, "");
}

function linkTargets(text) {
  const cleaned = stripCodeFences(text);
  const result = [];
  const markdown = /!?\[[^\]]*\]\(([^)]+)\)/g;
  const html = /\b(?:href|src)=["']([^"']+)["']/g;
  for (const match of cleaned.matchAll(markdown)) result.push(match[1].trim());
  for (const match of cleaned.matchAll(html)) result.push(match[1].trim());
  return result;
}

function normalizeTarget(rawTarget) {
  let target = rawTarget.trim();
  if (target.startsWith("<") && target.endsWith(">")) target = target.slice(1, -1);
  target = target.split("#", 1)[0].split("?", 1)[0];
  try { return decodeURIComponent(target); } catch { return target; }
}

function externalOrAnchor(rawTarget) {
  return rawTarget.startsWith("#") || /^(?:https?:|mailto:|tel:|data:)/i.test(rawTarget);
}

for (const file of allDocs) {
  const text = fs.readFileSync(file, "utf8");
  for (const rawTarget of linkTargets(text)) {
    if (!rawTarget || externalOrAnchor(rawTarget)) continue;
    const target = normalizeTarget(rawTarget);
    if (!target) continue;
    const absoluteTarget = path.resolve(path.dirname(file), target);
    if (fs.existsSync(absoluteTarget)) continue;
    const relativeTarget = relativeFromRoot(absoluteTarget);
    if (!fullRepository && carriedRepositoryTargets.has(relativeTarget)) continue;
    fail(`${relativeFromRoot(file)} links to missing ${rawTarget} (${relativeTarget})`);
  }
}

function assertIncludes(relativePath, expected) {
  if (!exists(relativePath)) return;
  const text = fs.readFileSync(path.join(root, relativePath), "utf8");
  if (!text.includes(expected)) fail(`${relativePath} must include ${JSON.stringify(expected)}`);
}

for (const file of ["README.md", "docs/ru/README.md"]) {
  assertIncludes(file, "MCP form elicitation");
  assertIncludes(file, "OpenCode/OpenChamber");
  assertIncludes(file, "sgg_unity_projects");
  assertIncludes(file, "extensions/quick-start.md");
}

if (exists("README.ru.md")) {
  const russianReadme = fs.readFileSync(path.join(root, "README.ru.md"), "utf8");
  if (russianReadme.trim().length < 1000 || !russianReadme.includes("## ") || !russianReadme.includes("sgg_unity_projects")) {
    fail("README.ru.md must contain substantive Russian project documentation, not a redirect stub");
  }
}

assertIncludes("docs/en/guides/permissions.md", "Native MCP Elicitation");
assertIncludes("docs/ru/guides/permissions.md", "Native MCP Elicitation");
for (const language of ["en", "ru"]) {
  const file = `docs/${language}/connect/opencode.md`;
  for (const contract of ["mcp.servers.sgg-unity-mcp", "{env:SGG_UNITY_MCP_TOKEN}", '"oauth": false', "Code Mode", "opencode-v2-client-follow-up.md"]) assertIncludes(file, contract);
}
assertIncludes("docs/en/reference/commands.md", "../../commands.generated.md");
assertIncludes("docs/ru/reference/commands.md", "../../commands.generated.md");
assertIncludes("docs/en/maintainers/versioning.md", "sgg-current-version-claim:versioning.en.current");
assertIncludes("docs/ru/maintainers/versioning.md", "sgg-current-version-claim:versioning.ru.current");
assertIncludes("docs/en/maintainers/manual-checks.md", "sgg-current-version-claim:manual.en.smoke.environment");
assertIncludes("docs/en/maintainers/manual-checks.md", "sgg-current-version-claim:manual.en.smoke.command");
assertIncludes("docs/ru/maintainers/manual-checks.md", "sgg-current-version-claim:manual.ru.smoke.environment");
assertIncludes("docs/ru/maintainers/manual-checks.md", "sgg-current-version-claim:manual.ru.smoke.command");

if (errors.length > 0) {
  for (const error of errors) console.error(`[check-docs] ${error}`);
  process.exitCode = 1;
} else {
  console.log(`[check-docs] ok (${enFiles.length} canonical English pages, ${canonicalRuFiles.length} canonical Russian pages)`);
}
