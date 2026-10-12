#!/usr/bin/env node

import fs from "node:fs";
import path from "node:path";
import { CHECK_PROFILES } from "./check-profile.mjs";
import { isReleaseCheckCommand, significantWorkflowLines, workflowRunCommands } from "./workflow-contract.mjs";

const root = process.cwd();
const packageJsonPath = path.join(root, "package.json");
const forbiddenAutomaticTriggers = ["push:", "pull_request_target:", "schedule:", "workflow_run:"];
const requiredScripts = [
  "build:gateway-runtime",
  "typecheck",
  "test",
  "eval:agent-harness",
  "check:agent-evals",
  "check:version",
  "check:docs",
  "check:command-docs",
  "check:forbidden-identifiers",
  "check:unity-package",
  "check:unity-editor",
  "check:unity-smoke",
  "check:release",
  "generate:release-evidence-template",
  "check:asset-index-benchmark",
  "check:gateway-runtime",
  "check:changelog",
  "check:workflow",
  "check:command-descriptors",
  "check:policy",
  "check:skill",
  "check:manifest-security",
  "check:notices",
  "check:fast",
  "check:static",
  "check:all",
];
const requiredChecksWorkflowCommands = [
  "npm ci",
  "npm run typecheck",
  "npm test",
  "npm run check:version",
  "npm run check:docs",
  "npm run check:command-docs",
  "npm run check:forbidden-identifiers",
  "npm run check:command-descriptors",
  "npm run check:manifest-security",
  "npm run check:gateway-runtime",
  "npm run check:notices",
  "npm run check:changelog",
  "npm run check:workflow",
  "npm run check:static",
];
const requiredReleaseWorkflowCommands = [
  "npm ci",
  "npm run check:static",
  "npm run build:gateway-runtime",
  "npm run check:notices",
  "git diff --exit-code -- THIRD_PARTY_NOTICES.md sbom.cdx.json unity/Packages/com.sungeargames.unity-mcp/Editor/GatewayRuntime",
];

let failed = false;

function fail(message) {
  console.error(`[check-manual-workflow] ${message}`);
  failed = true;
}

function readWorkflow(relativePath) {
  const workflowPath = path.join(root, relativePath);
  if (!fs.existsSync(workflowPath)) {
    fail(`missing ${relativePath}`);
    return undefined;
  }

  const workflow = fs.readFileSync(workflowPath, "utf8");
  return {
    workflow,
    significantLines: significantWorkflowLines(workflow),
  };
}

function checkChecksWorkflow(relativePath) {
  const data = readWorkflow(relativePath);
  if (!data) {
    return;
  }

  const { workflow, significantLines } = data;
  const runCommands = workflowRunCommands(workflow);

  if (!significantLines.includes("pull_request:")) {
    fail(`${relativePath} must run the safe public PR subset on pull_request`);
  }
  if (!significantLines.includes("workflow_dispatch:")) {
    fail(`${relativePath} must keep manual workflow_dispatch for the full local gate`);
  }
  for (const trigger of forbiddenAutomaticTriggers) {
    if (significantLines.includes(trigger)) {
      fail(`${relativePath} must not use automatic trigger ${trigger}`);
    }
  }
  for (const command of requiredChecksWorkflowCommands) {
    if (!runCommands.includes(command)) {
      fail(`${relativePath} must contain an explicit - run: ${command} entry`);
    }
  }
  if (!significantLines.some((line) => line.includes("github.event_name == 'pull_request'"))) {
    fail(`${relativePath} must scope the safe PR job to pull_request`);
  }
  if (!significantLines.some((line) => line.includes("github.event_name == 'workflow_dispatch'"))) {
    fail(`${relativePath} must scope the full gate to workflow_dispatch`);
  }
}

function checkManualOnlyWorkflow(relativePath, requiredCommands) {
  const data = readWorkflow(relativePath);
  if (!data) {
    return;
  }

  const { workflow, significantLines } = data;
  const runCommands = workflowRunCommands(workflow);

  if (!significantLines.includes("workflow_dispatch:")) {
    fail(`${relativePath} must be manually triggered with workflow_dispatch`);
  }

  for (const trigger of [...forbiddenAutomaticTriggers, "pull_request:"]) {
    if (significantLines.includes(trigger)) {
      fail(`${relativePath} must not use automatic trigger ${trigger}`);
    }
  }

  for (const command of requiredCommands) {
    if (!runCommands.includes(command)) {
      fail(`${relativePath} must contain an explicit - run: ${command} entry`);
    }
  }
}

checkChecksWorkflow(".github/workflows/checks.yml");
checkManualOnlyWorkflow(".github/workflows/release.yml", requiredReleaseWorkflowCommands);

if (!fs.existsSync(packageJsonPath)) {
  fail("missing package.json");
} else {
  const packageJson = JSON.parse(fs.readFileSync(packageJsonPath, "utf8"));
  const scripts = packageJson.scripts ?? {};
  for (const script of requiredScripts) {
    if (typeof scripts[script] !== "string" || scripts[script].length === 0) {
      fail(`package.json must define script ${script}`);
    }
  }

  if (scripts["check:fast"] !== "node scripts/check-profile.mjs fast") {
    fail("package.json check:fast must use the fast declarative profile");
  }
  if (scripts["check:static"] !== "node scripts/check-profile.mjs static") {
    fail("package.json check:static must use the static declarative profile");
  }
  if (scripts["check:all"] !== "npm run check:static") {
    fail("package.json check:all must be an unambiguous check:static compatibility alias");
  }
  if (scripts["check:unity-smoke"] !== "node scripts/check-unity-smoke.mjs") {
    fail("package.json check:unity-smoke must use the bounded manual Unity smoke runner");
  }
  if (scripts["check:release"] !== "node scripts/check-release.mjs") {
    fail("package.json check:release must use the fail-closed local release evidence verifier");
  }
  if (scripts["generate:release-evidence-template"] !== "node scripts/generate-release-evidence-template.mjs") {
    fail("package.json generate:release-evidence-template must use the not-run template generator");
  }

  for (const [profile, members] of Object.entries(CHECK_PROFILES)) {
    for (const member of members) {
      if (typeof scripts[member] !== "string" || scripts[member].length === 0) {
        fail(`${profile} profile references missing package.json script ${member}`);
      }
    }
    if (members.includes("check:unity-editor") || members.includes("check:unity-editor-matrix") || members.includes("check:unity-smoke")) {
      fail(`${profile} profile must not invoke Unity Editor validation`);
    }
    if (members.includes("check:release")) fail(`${profile} profile must not invoke the local release evidence verifier`);
  }
}

const releaseWorkflow = fs.readFileSync(path.join(root, ".github/workflows/release.yml"), "utf8");
const releaseLines = significantWorkflowLines(releaseWorkflow);
const releaseCommands = workflowRunCommands(releaseWorkflow);
if (!releaseLines.some((line) => line.includes("static readiness only")) || releaseCommands.some(isReleaseCheckCommand)) {
  fail("release.yml must identify itself as static readiness only and must not claim a full evidence pass");
}

if (!failed) {
  console.log("[check-manual-workflow] ok");
} else {
  process.exitCode = 1;
}
