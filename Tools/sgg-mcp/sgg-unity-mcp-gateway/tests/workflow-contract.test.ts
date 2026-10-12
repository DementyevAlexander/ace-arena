import { describe, expect, it } from "vitest";

import {
  isReleaseCheckCommand,
  significantWorkflowLines,
  workflowRunCommands,
// @ts-expect-error The directly executable Node helper has no separate declaration file.
} from "../scripts/workflow-contract.mjs";

describe("manual workflow parsing", () => {
  it("parses inline, literal, and folded run scalars while ignoring comments", () => {
    const workflow = `
      # - run: npm run check:release
      name: manual static readiness only # bounded marker
      steps:
        - run: npm ci
        - name: npm run check:static
        - run: npm run check:static # actual command
        - run: |
            echo safe
            # node scripts/check-release.mjs
            npm run check:docs # check:release in a comment
        - run: >
            sh -c "node scripts/check-release.mjs"
    `;
    expect(workflowRunCommands(workflow)).toEqual([
      "npm ci",
      "npm run check:static",
      "echo safe\nnpm run check:docs",
      'sh -c "node scripts/check-release.mjs"',
    ]);
    expect(significantWorkflowLines(workflow)).toContain("name: manual static readiness only");
  });

  it.each([
    "npm run check:release",
    "cmd /c npm.cmd run check : release",
    "sh -c 'node scripts/check-release.mjs'",
    "powershell -Command \"node.exe .\\scripts\\check-release.mjs\"",
    "tool --argument=check:release",
  ])("conservatively recognizes executable release verification body %s", (command) => {
    expect(isReleaseCheckCommand(command)).toBe(true);
  });

  it("does not treat commented commands as executable", () => {
    const commands = workflowRunCommands(`steps:\n  - run: |\n      # npm run check:release\n      npm run check:static # check-release.mjs`);
    expect(commands).toEqual(["npm run check:static"]);
    expect(commands.some(isReleaseCheckCommand)).toBe(false);
  });

  it.each(["|", ">", "|-", "|+", "|2", "|2-", "|+2", ">2", ">-2", ">2+"])(
    "parses YAML block scalar header %s and inspects its full body", (header) => {
      const commands = workflowRunCommands(`steps:\n  - run: ${header}\n      echo safe\n      sh -c 'npm run check:release'\n      # check-release.mjs`);
      expect(commands).toHaveLength(1);
      expect(isReleaseCheckCommand(commands[0])).toBe(true);
    },
  );

  it.each(["npm run check:static", "release check is documented here", "node scripts/check-release-docs.mjs"])(
    "does not reject non-executable text %s", (command) => expect(isReleaseCheckCommand(command)).toBe(false),
  );
});
