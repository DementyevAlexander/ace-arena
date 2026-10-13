function stripComment(value) {
  let single = false;
  let double = false;
  for (let index = 0; index < value.length; index++) {
    const character = value[index];
    if (character === "'" && !double) single = !single;
    else if (character === '"' && !single && value[index - 1] !== "\\") double = !double;
    else if (character === "#" && !single && !double && (index === 0 || /\s/.test(value[index - 1]))) return value.slice(0, index).trimEnd();
  }
  return value;
}

export function significantWorkflowLines(workflow) {
  return workflow.split(/\r?\n/).map((line) => stripComment(line).trim()).filter(Boolean);
}

export function workflowRunCommands(workflow) {
  const lines = workflow.split(/\r?\n/);
  const commands = [];
  for (let index = 0; index < lines.length; index++) {
    const line = lines[index];
    const match = /^(\s*)-\s+run:\s*(.*)$/.exec(line);
    if (!match) continue;
    const scalar = stripComment(match[2]).trim();
    const blockScalar = /^[|>](?:(?:[1-9][+-]?)|(?:[+-][1-9]?))?$/.test(scalar);
    if (!blockScalar) {
      if (scalar) commands.push(scalar.replace(/^(['"])(.*)\1$/, "$2"));
      continue;
    }
    const baseIndent = match[1].length;
    const body = [];
    let contentIndent;
    while (++index < lines.length) {
      const bodyLine = lines[index];
      if (!bodyLine.trim()) {
        body.push("");
        continue;
      }
      const indent = /^\s*/.exec(bodyLine)[0].length;
      if (indent <= baseIndent || contentIndent !== undefined && indent < contentIndent) {
        index--;
        break;
      }
      contentIndent ??= indent;
      const executable = stripComment(bodyLine.trim()).trim();
      if (executable) body.push(executable);
    }
    const command = body.join(scalar.startsWith(">") ? " " : "\n").trim();
    if (command) commands.push(command);
  }
  return commands;
}

export function isReleaseCheckCommand(command) {
  return /check\s*:\s*release|check-release\.mjs/i.test(command);
}
