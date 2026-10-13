#!/usr/bin/env node

import fs from "node:fs";
import path from "node:path";

const root = process.cwd();
const commandsDir = path.join(root, "unity", "Packages", "com.sungeargames.unity-mcp", "Editor", "Commands");
const allowedRisks = new Set(["read", "write", "execute", "destructive", "network"]);
const allowedIdempotency = new Set(["safe", "idempotent", "unsafe"]);
const shapesRequiringOutputLimit = new Set(["bounded_list", "bounded_tree", "object_details", "asset_details", "text_artifact", "build_result", "screenshot_artifact"]);

let failed = false;

function fail(message) {
  console.error(`[check-command-descriptors] ${message}`);
  failed = true;
}

function readCommandFiles(dir) {
  if (!fs.existsSync(dir)) {
    fail(`missing ${path.relative(root, dir)}`);
    return [];
  }

  return fs.readdirSync(dir)
    .filter((name) => name.endsWith("Commands.cs") || name === "BuiltInCommands.cs" || name === "JobArtifactCommands.cs")
    .map((name) => path.join(dir, name))
    .sort((left, right) => left.localeCompare(right));
}

function lineNumber(text, index) {
  return text.slice(0, index).split("\n").length;
}

function findAttributeEnd(text, start) {
  let depth = 0;
  let inString = false;
  let escaped = false;
  for (let index = start; index < text.length; index++) {
    const char = text[index];
    if (escaped) {
      escaped = false;
      continue;
    }
    if (char === "\\") {
      escaped = true;
      continue;
    }
    if (char === '"') {
      inString = !inString;
      continue;
    }
    if (inString) {
      continue;
    }
    if (char === "(") {
      depth++;
    } else if (char === ")") {
      depth--;
      if (depth === 0) {
        return index;
      }
    }
  }

  return -1;
}

function csharpStringLiteralValue(value) {
  const trimmed = value.trim();
  const match = trimmed.match(/^"((?:\\.|[^"])*)"$/s);
  if (!match) {
    return undefined;
  }

  return match[1]
    .replaceAll('\\"', '"')
    .replaceAll('\\\\', "\\")
    .replaceAll('\\n', "\n")
    .replaceAll('\\r', "\r")
    .replaceAll('\\t', "\t");
}

function constantsByName(text) {
  const constants = new Map();
  const regex = /const\s+string\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*("(?:\\.|[^"])*")\s*;/gs;
  for (const match of text.matchAll(regex)) {
    constants.set(match[1], csharpStringLiteralValue(match[2]));
  }
  return constants;
}

function splitTopLevel(value) {
  const parts = [];
  let start = 0;
  let braceDepth = 0;
  let parenDepth = 0;
  let inString = false;
  let escaped = false;
  for (let index = 0; index < value.length; index++) {
    const char = value[index];
    if (escaped) {
      escaped = false;
      continue;
    }
    if (char === "\\") {
      escaped = true;
      continue;
    }
    if (char === '"') {
      inString = !inString;
      continue;
    }
    if (inString) {
      continue;
    }
    if (char === "{") {
      braceDepth++;
    } else if (char === "}") {
      braceDepth--;
    } else if (char === "(") {
      parenDepth++;
    } else if (char === ")") {
      parenDepth--;
    } else if (char === "," && braceDepth === 0 && parenDepth === 0) {
      parts.push(value.slice(start, index).trim());
      start = index + 1;
    }
  }
  parts.push(value.slice(start).trim());
  return parts.filter(Boolean);
}

function parseAttribute(raw, constants) {
  const body = raw.slice(raw.indexOf("(") + 1, raw.lastIndexOf(")"));
  const parts = splitTopLevel(body);
  const descriptor = { id: csharpStringLiteralValue(parts[0] ?? "") };
  for (const part of parts.slice(1)) {
    const separator = part.indexOf("=");
    if (separator <= 0) {
      continue;
    }
    const name = part.slice(0, separator).trim();
    const rawValue = part.slice(separator + 1).trim();
    descriptor[name] = constants.get(rawValue) ?? csharpStringLiteralValue(rawValue) ?? rawValue;
  }
  return descriptor;
}

function validateJson(file, commandId, property, value) {
  try {
    const parsed = JSON.parse(value);
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
      fail(`${file}: command ${commandId} ${property} must be a JSON object`);
    }
  } catch (error) {
    fail(`${file}: command ${commandId} ${property} is invalid JSON: ${error.message}`);
  }
}

function validateWrapperWaitJson(file, commandId, value) {
  let contract;
  try {
    contract = JSON.parse(value);
  } catch (error) {
    fail(`${file}: command ${commandId} WrapperWaitJson is invalid JSON: ${error.message}`);
    return;
  }
  if (!contract || typeof contract !== "object" || Array.isArray(contract)) {
    fail(`${file}: command ${commandId} WrapperWaitJson must be a JSON object`);
    return;
  }
  const keys = Object.keys(contract);
  if (contract.mode !== "wait" || keys.some((key) => !["mode", "wait_for_replacement_ms", "wait_for_test_run_terminal_ms"].includes(key))) {
    fail(`${file}: command ${commandId} WrapperWaitJson must advertise only mode=wait and supported wait fields`);
  }
  const replacement = contract.wait_for_replacement_ms;
  const terminal = contract.wait_for_test_run_terminal_ms;
  if (!replacement && !terminal) {
    fail(`${file}: command ${commandId} WrapperWaitJson must advertise at least one wait field`);
  }
  validateRange(replacement, "wait_for_replacement_ms", "pre_dispatch_only");
  validateRange(terminal, "wait_for_test_run_terminal_ms", "start_and_terminal_poll", true);

  function validateRange(range, name, deadlineScope, terminalContract = false) {
    if (range === undefined) return;
    const allowedKeys = terminalContract
      ? ["min_ms", "max_ms", "deadline_scope", "response_mode", "dry_run", "compile_readiness_contract"]
      : ["min_ms", "max_ms", "deadline_scope"];
    const valid = range && typeof range === "object" && !Array.isArray(range)
      && Object.keys(range).every((key) => allowedKeys.includes(key))
      && Number.isInteger(range.min_ms) && range.min_ms >= 1 && range.min_ms <= 3_600_000
      && Number.isInteger(range.max_ms) && range.max_ms >= 1 && range.max_ms <= 3_600_000
      && range.min_ms <= range.max_ms
      && range.deadline_scope === deadlineScope
      && (!terminalContract || (range.response_mode === "compact" && range.dry_run === false))
      && (!Object.hasOwn(range, "compile_readiness_contract") || (terminalContract
        && typeof range.compile_readiness_contract === "string" && range.compile_readiness_contract.length >= 1 && range.compile_readiness_contract.length <= 128));
    if (!valid) fail(`${file}: command ${commandId} WrapperWaitJson ${name} contract is invalid`);
  }
}

for (const absolutePath of readCommandFiles(commandsDir)) {
  const relativePath = path.relative(root, absolutePath);
  const text = fs.readFileSync(absolutePath, "utf8");
  const constants = constantsByName(text);
  let searchFrom = 0;
  while (true) {
    const start = text.indexOf("[McpCommand(", searchFrom);
    if (start < 0) {
      break;
    }
    const end = findAttributeEnd(text, start);
    if (end < 0) {
      fail(`${relativePath}:${lineNumber(text, start)} malformed McpCommand attribute`);
      break;
    }

    const raw = text.slice(start + 1, end + 1);
    const descriptor = parseAttribute(raw, constants);
    const commandLabel = descriptor.id ? `command ${descriptor.id}` : `attribute at line ${lineNumber(text, start)}`;

    if (!descriptor.id) {
      fail(`${relativePath}:${lineNumber(text, start)} missing command id`);
    }
    if (!allowedRisks.has(descriptor.Risk)) {
      fail(`${relativePath}: ${commandLabel} has missing/unknown Risk (${descriptor.Risk ?? "missing"})`);
    }
    if (!allowedIdempotency.has(descriptor.Idempotency)) {
      fail(`${relativePath}: ${commandLabel} has missing/unknown Idempotency (${descriptor.Idempotency ?? "missing"})`);
    }
    if (!/^\d+$/.test(String(descriptor.TimeoutMs ?? "")) || Number(descriptor.TimeoutMs) <= 0 || Number(descriptor.TimeoutMs) > 7200000) {
      fail(`${relativePath}: ${commandLabel} must set TimeoutMs between 1 and 7200000`);
    }
    if (!descriptor.SchemaJson) {
      fail(`${relativePath}: ${commandLabel} must set SchemaJson`);
    } else if (!String(descriptor.SchemaJson).startsWith("{")) {
      fail(`${relativePath}: ${commandLabel} SchemaJson must resolve to a JSON object literal or const string`);
    } else {
      validateJson(relativePath, descriptor.id, "SchemaJson", descriptor.SchemaJson);
    }
    if (!descriptor.ResultShape) {
      fail(`${relativePath}: ${commandLabel} must set ResultShape`);
    }
    if (shapesRequiringOutputLimit.has(descriptor.ResultShape)) {
      if (!descriptor.OutputLimitJson) {
        fail(`${relativePath}: ${commandLabel} with ResultShape=${descriptor.ResultShape} must set OutputLimitJson`);
      } else if (!String(descriptor.OutputLimitJson).startsWith("{")) {
        fail(`${relativePath}: ${commandLabel} OutputLimitJson must resolve to a JSON object literal or const string`);
      } else {
        validateJson(relativePath, descriptor.id, "OutputLimitJson", descriptor.OutputLimitJson);
      }
    }
    if (descriptor.WrapperWaitJson) {
      if (!String(descriptor.WrapperWaitJson).startsWith("{")) {
        fail(`${relativePath}: ${commandLabel} WrapperWaitJson must resolve to a JSON object literal or const string`);
      } else {
        validateWrapperWaitJson(relativePath, descriptor.id, descriptor.WrapperWaitJson);
      }
    }

    searchFrom = end + 1;
  }
}

if (!failed) {
  console.log("[check-command-descriptors] ok");
} else {
  process.exitCode = 1;
}
