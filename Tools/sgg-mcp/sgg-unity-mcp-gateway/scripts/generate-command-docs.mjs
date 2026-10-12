#!/usr/bin/env node

import fs from "node:fs";
import path from "node:path";

const root = process.cwd();
const checkOnly = process.argv.includes("--check");
const packageId = "com.sungeargames.unity-mcp";
const commandsDir = path.join(root, "unity", "Packages", packageId, "Editor", "Commands");
const virtualCommandsDir = path.join(root, "src", "mcp");

const outputFiles = [
  { relativePath: "docs/commands.generated.md", language: "en" },
  { relativePath: "docs/ru/commands.generated.ru.md", language: "ru" },
];

const mcpTools = [
  { id: "sgg_unity_projects", description: "List live Unity projects plus aggregate live/stale registry counts." },
  { id: "sgg_unity_stale_projects", description: "Query bounded, filterable stale heartbeat history with stable cursor pagination." },
  { id: "sgg_unity_stale_registry_cleanup", description: "Preview or explicitly clean bounded expired stale heartbeat records owned by confirmed-dead processes." },
  { id: "sgg_unity_describe", description: "Read project status, command catalog, grouped command lists, or one command schema." },
  { id: "sgg_unity_call", description: "Invoke one Unity command for an explicit project." },
  { id: "sgg_unity_batch", description: "Run bounded batches of Unity command calls with explicit project per item." },
  { id: "sgg_unity_job", description: "Poll gateway-local jobs and request cancellation only for commands that advertise `supports_cancel`." },
  { id: "sgg_unity_logs", description: "Read bounded gateway, bridge, or Unity log tails." },
];

let failed = false;

function main() {
  const bridgeCommands = readBridgeCommands();
  const virtualCommands = readVirtualCommands();
  assertNoDuplicateCommands([...bridgeCommands, ...virtualCommands]);

  for (const output of outputFiles) {
    const content = renderDocument(output.language, bridgeCommands, virtualCommands);
    const absolutePath = path.join(root, output.relativePath);
    if (checkOnly) {
      checkFresh(absolutePath, output.relativePath, content);
    } else {
      fs.mkdirSync(path.dirname(absolutePath), { recursive: true });
      fs.writeFileSync(absolutePath, content, "utf8");
    }
  }

  if (failed) {
    process.exitCode = 1;
    return;
  }

  console.log(checkOnly ? "[generate-command-docs] ok" : "[generate-command-docs] wrote generated command docs");
}

function readBridgeCommands() {
  if (!fs.existsSync(commandsDir)) {
    throw new Error(`missing ${path.relative(root, commandsDir)}`);
  }

  const commands = [];
  for (const fileName of fs.readdirSync(commandsDir).filter(isCommandSourceFile).sort((left, right) => left.localeCompare(right))) {
    const absolutePath = path.join(commandsDir, fileName);
    const relativePath = path.relative(root, absolutePath);
    const text = fs.readFileSync(absolutePath, "utf8");
    const constants = csharpConstantsByName(text);
    let searchFrom = 0;

    while (true) {
      const start = text.indexOf("[McpCommand(", searchFrom);
      if (start < 0) {
        break;
      }

      const open = text.indexOf("(", start);
      const end = findMatching(text, open, "(", ")");
      if (end < 0) {
        throw new Error(`${relativePath}: malformed McpCommand attribute`);
      }

      const descriptor = parseCsharpAttribute(text.slice(start + 1, end + 1), constants);
      commands.push(normalizeCommand({
        id: descriptor.id,
        description: descriptor.Description || descriptor.Title,
        risk: descriptor.Risk,
        featureFlag: descriptor.FeatureFlag,
        sourceFile: relativePath,
      }));
      searchFrom = end + 1;
    }
  }

  return sortCommands(commands);
}

function readVirtualCommands() {
  if (!fs.existsSync(virtualCommandsDir)) {
    throw new Error(`missing ${path.relative(root, virtualCommandsDir)}`);
  }

  const commands = [];
  const files = fs.readdirSync(virtualCommandsDir)
    .filter((fileName) => fileName.endsWith("-commands.ts"))
    .sort((left, right) => left.localeCompare(right));

  for (const fileName of files) {
    const absolutePath = path.join(virtualCommandsDir, fileName);
    const relativePath = path.relative(root, absolutePath);
    const text = fs.readFileSync(absolutePath, "utf8");
    const constants = tsConstantsByName(text);
    const arrayRegex = /export\s+const\s+[A-Z0-9_]*COMMANDS\s*:\s*CommandDescriptor\[\]\s*=\s*\[/g;

    for (const match of text.matchAll(arrayRegex)) {
      const arrayStart = match.index + match[0].lastIndexOf("[");
      const arrayEnd = findMatching(text, arrayStart, "[", "]");
      if (arrayEnd < 0) {
        throw new Error(`${relativePath}: malformed CommandDescriptor array`);
      }

      const body = text.slice(arrayStart + 1, arrayEnd);
      for (const objectText of topLevelObjects(body)) {
        const id = resolveTsValue(extractPropertyValue(objectText, "id"), constants);
        if (!id) {
          continue;
        }

        commands.push(normalizeCommand({
          id,
          description: resolveTsValue(extractPropertyValue(objectText, "description"), constants),
          risk: resolveTsValue(extractPropertyValue(objectText, "risk"), constants),
          featureFlag: resolveTsValue(extractPropertyValue(objectText, "feature_flag"), constants),
          sourceFile: relativePath,
        }));
      }
    }
  }

  return sortCommands(commands);
}

function isCommandSourceFile(fileName) {
  return fileName.endsWith("Commands.cs") || fileName === "BuiltInCommands.cs" || fileName === "JobArtifactCommands.cs";
}

function normalizeCommand(command) {
  if (!command.id) {
    throw new Error(`missing command id in ${command.sourceFile}`);
  }

  return {
    id: command.id,
    description: command.description || "No descriptor description.",
    risk: command.risk || "read",
    featureFlag: command.featureFlag || "",
    sourceFile: command.sourceFile,
  };
}

function sortCommands(commands) {
  return [...commands].sort((left, right) => left.id.localeCompare(right.id));
}

function assertNoDuplicateCommands(commands) {
  const seen = new Map();
  for (const command of commands) {
    const previous = seen.get(command.id);
    if (previous) {
      throw new Error(`duplicate command ${command.id} in ${previous} and ${command.sourceFile}`);
    }
    seen.set(command.id, command.sourceFile);
  }
}

function csharpConstantsByName(text) {
  const constants = new Map();
  const regex = /const\s+string\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*("(?:\\.|[^"])*")\s*;/gs;
  for (const match of text.matchAll(regex)) {
    constants.set(match[1], stringLiteralValue(match[2]));
  }
  return constants;
}

function tsConstantsByName(text) {
  const constants = new Map();
  const regex = /const\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*("(?:\\.|[^"])*")\s*;/gs;
  for (const match of text.matchAll(regex)) {
    constants.set(match[1], stringLiteralValue(match[2]));
  }
  return constants;
}

function parseCsharpAttribute(raw, constants) {
  const body = raw.slice(raw.indexOf("(") + 1, raw.lastIndexOf(")"));
  const parts = splitTopLevel(body);
  const descriptor = { id: resolveCsharpValue(parts[0] ?? "", constants) };

  for (const part of parts.slice(1)) {
    const separator = part.indexOf("=");
    if (separator <= 0) {
      continue;
    }
    const name = part.slice(0, separator).trim();
    const rawValue = part.slice(separator + 1).trim();
    descriptor[name] = resolveCsharpValue(rawValue, constants);
  }

  return descriptor;
}

function resolveCsharpValue(rawValue, constants) {
  const trimmed = String(rawValue ?? "").trim();
  return constants.get(trimmed) ?? stringLiteralValue(trimmed) ?? "";
}

function resolveTsValue(rawValue, constants) {
  const trimmed = String(rawValue ?? "").trim();
  return constants.get(trimmed) ?? stringLiteralValue(trimmed) ?? "";
}

function extractPropertyValue(objectText, propertyName) {
  const regex = new RegExp(`\\b${propertyName}\\s*:\\s*`, "g");
  const match = regex.exec(objectText);
  if (!match) {
    return undefined;
  }

  let index = regex.lastIndex;
  while (/\s/.test(objectText[index] ?? "")) {
    index++;
  }

  if (objectText[index] === '"') {
    return readStringLiteral(objectText, index);
  }

  const identifierMatch = /^[A-Za-z_][A-Za-z0-9_]*/.exec(objectText.slice(index));
  return identifierMatch?.[0];
}

function readStringLiteral(text, start) {
  let escaped = false;
  for (let index = start + 1; index < text.length; index++) {
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
      return text.slice(start, index + 1);
    }
  }
  return undefined;
}

function stringLiteralValue(value) {
  const match = String(value ?? "").trim().match(/^"((?:\\.|[^"])*)"$/s);
  if (!match) {
    return undefined;
  }

  return match[1]
    .replaceAll('\\"', '"')
    .replaceAll("\\\\", "\\")
    .replaceAll("\\n", "\n")
    .replaceAll("\\r", "\r")
    .replaceAll("\\t", "\t");
}

function splitTopLevel(value) {
  const parts = [];
  let start = 0;
  let braceDepth = 0;
  let bracketDepth = 0;
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
    } else if (char === "[") {
      bracketDepth++;
    } else if (char === "]") {
      bracketDepth--;
    } else if (char === "(") {
      parenDepth++;
    } else if (char === ")") {
      parenDepth--;
    } else if (char === "," && braceDepth === 0 && bracketDepth === 0 && parenDepth === 0) {
      parts.push(value.slice(start, index).trim());
      start = index + 1;
    }
  }

  parts.push(value.slice(start).trim());
  return parts.filter(Boolean);
}

function findMatching(text, start, openChar, closeChar) {
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
    if (char === openChar) {
      depth++;
    } else if (char === closeChar) {
      depth--;
      if (depth === 0) {
        return index;
      }
    }
  }

  return -1;
}

function topLevelObjects(value) {
  const objects = [];
  let start = -1;
  let depth = 0;
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
      if (depth === 0) {
        start = index;
      }
      depth++;
    } else if (char === "}") {
      depth--;
      if (depth === 0 && start >= 0) {
        objects.push(value.slice(start, index + 1));
        start = -1;
      }
    }
  }

  return objects;
}

function renderDocument(language, bridgeCommands, virtualCommands) {
  const isRu = language === "ru";
  const title = isRu ? "# Сгенерированный реестр команд" : "# Generated Command Inventory";
  const intro = isRu
    ? [
        "> Generated by `npm run generate:command-docs`. Do not edit directly.",
        "",
        "Этот файл собирается из stable MCP tool definitions, Unity Bridge `[McpCommand]` descriptors и Gateway virtual command descriptors. Feature-gated команды реализованы, но скрыты, пока matching config flag не включен.",
      ]
    : [
        "> Generated by `npm run generate:command-docs`. Do not edit directly.",
        "",
        "This file is built from stable MCP tool definitions, Unity Bridge `[McpCommand]` descriptors, and Gateway virtual command descriptors. Feature-gated commands are implemented but hidden until the matching config flag is enabled.",
      ];

  return [
    title,
    "",
    ...intro,
    "",
    isRu ? "## Stable MCP Tools" : "## Stable MCP Tools",
    "",
    renderToolsTable(isRu),
    "",
    isRu ? `## Unity Bridge Commands (${bridgeCommands.length})` : `## Unity Bridge Commands (${bridgeCommands.length})`,
    "",
    renderCommandTable(bridgeCommands, isRu),
    "",
    isRu ? `## Gateway Virtual Commands (${virtualCommands.length})` : `## Gateway Virtual Commands (${virtualCommands.length})`,
    "",
    renderCommandTable(virtualCommands, isRu),
    "",
  ].join("\n");
}

function renderToolsTable(isRu) {
  const header = isRu
    ? "| Tool | Status | Назначение |\n| --- | --- | --- |"
    : "| Tool | Status | Purpose |\n| --- | --- | --- |";
  const rows = mcpTools.map((tool) => `| \`${tool.id}\` | Implemented | ${markdownCell(tool.description)} |`);
  return [header, ...rows].join("\n");
}

function renderCommandTable(commands, isRu) {
  const header = isRu
    ? "| Command | Status | Feature flag | Risk | Назначение |\n| --- | --- | --- | --- | --- |"
    : "| Command | Status | Feature flag | Risk | Purpose |\n| --- | --- | --- | --- | --- |";
  const rows = commands.map((command) => {
    const status = command.featureFlag ? "Feature-gated" : "Implemented";
    const featureFlag = command.featureFlag ? `\`${command.featureFlag}\`` : "none";
    return `| \`${command.id}\` | ${status} | ${featureFlag} | ${markdownCell(command.risk)} | ${markdownCell(command.description)} |`;
  });
  return [header, ...rows].join("\n");
}

function markdownCell(value) {
  return String(value ?? "")
    .replaceAll("\r", " ")
    .replaceAll("\n", " ")
    .replaceAll("|", "\\|")
    .trim();
}

function checkFresh(absolutePath, relativePath, expected) {
  if (!fs.existsSync(absolutePath)) {
    console.error(`[generate-command-docs] missing ${relativePath}; run npm run generate:command-docs`);
    failed = true;
    return;
  }

  const actual = fs.readFileSync(absolutePath, "utf8");
  if (actual !== expected) {
    console.error(`[generate-command-docs] stale ${relativePath}; run npm run generate:command-docs`);
    failed = true;
  }
}

main();
