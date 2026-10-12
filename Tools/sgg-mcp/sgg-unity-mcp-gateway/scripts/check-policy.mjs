#!/usr/bin/env node

import fs from "node:fs";
import path from "node:path";

const root = process.cwd();
const policyPath = path.join(root, "config", "policy.example.json");
const risks = ["read", "write", "execute", "destructive", "network"];
const actions = ["allow", "ask", "deny"];
let failed = false;

function fail(message) {
  console.error(`[check-policy] ${message}`);
  failed = true;
}

function readJson(relativePath) {
  const absolutePath = path.join(root, relativePath);
  try {
    return JSON.parse(fs.readFileSync(absolutePath, "utf8"));
  } catch (error) {
    fail(`${relativePath}: ${error.message}`);
    return undefined;
  }
}

function validatePatterns(projectId, action, patterns) {
  if (!Array.isArray(patterns)) {
    fail(`config/policy.example.json: projects.${projectId}.${action} must be an array`);
    return [];
  }
  for (const pattern of patterns) {
    if (typeof pattern !== "string" || pattern.length === 0) {
      fail(`config/policy.example.json: projects.${projectId}.${action} contains non-empty string patterns only`);
      continue;
    }
    if (pattern.includes("*") && !pattern.endsWith("*")) {
      fail(`config/policy.example.json: projects.${projectId}.${action} pattern ${pattern} may only use trailing * wildcard`);
    }
  }
  return patterns;
}

const policy = readJson("config/policy.example.json");
if (policy) {
  if (!policy.defaults || typeof policy.defaults !== "object" || Array.isArray(policy.defaults)) {
    fail("config/policy.example.json: defaults must be an object");
  } else {
    for (const risk of risks) {
      if (!actions.includes(policy.defaults[risk])) {
        fail(`config/policy.example.json: defaults.${risk} must be one of ${actions.join(", ")}`);
      }
    }
    for (const risk of Object.keys(policy.defaults)) {
      if (!risks.includes(risk)) {
        fail(`config/policy.example.json: defaults.${risk} is not a known risk`);
      }
    }
  }

  if (![undefined, "ask", "deny"].includes(policy.untrusted_package_default)) {
    fail("config/policy.example.json: untrusted_package_default must be ask or deny");
  }

  for (const [projectId, projectRules] of Object.entries(policy.projects ?? {})) {
    const byAction = new Map();
    for (const action of actions) {
      for (const pattern of validatePatterns(projectId, action, projectRules[action] ?? [])) {
        const previous = byAction.get(pattern);
        if (previous && previous !== action) {
          fail(`config/policy.example.json: projects.${projectId} pattern ${pattern} appears in both ${previous} and ${action}`);
        }
        byAction.set(pattern, action);
      }
    }

    const allowAskWildcards = [
      ...(projectRules.allow ?? []).map((pattern) => ["allow", pattern]),
      ...(projectRules.ask ?? []).map((pattern) => ["ask", pattern]),
    ].filter(([, pattern]) => typeof pattern === "string" && pattern.endsWith("*"));

    for (let leftIndex = 0; leftIndex < allowAskWildcards.length; leftIndex++) {
      for (let rightIndex = leftIndex + 1; rightIndex < allowAskWildcards.length; rightIndex++) {
        const [leftAction, left] = allowAskWildcards[leftIndex];
        const [rightAction, right] = allowAskWildcards[rightIndex];
        if (leftAction === rightAction || left.length !== right.length) {
          continue;
        }
        const leftPrefix = left.slice(0, -1);
        const rightPrefix = right.slice(0, -1);
        if (leftPrefix.startsWith(rightPrefix) || rightPrefix.startsWith(leftPrefix)) {
          fail(`config/policy.example.json: projects.${projectId} wildcard specificity tie between ${leftAction}:${left} and ${rightAction}:${right}`);
        }
      }
    }
  }
}

if (!fs.existsSync(policyPath)) {
  fail("missing config/policy.example.json");
}

if (!failed) {
  console.log("[check-policy] ok");
} else {
  process.exitCode = 1;
}
