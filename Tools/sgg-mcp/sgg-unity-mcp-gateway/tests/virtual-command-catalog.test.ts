import { describe, expect, it } from "vitest";

import type { CommandDescriptor } from "../src/bridge/bridge-client.js";
import { ARTIFACT_COMMANDS } from "../src/mcp/artifact-commands.js";
import { CHECKPOINT_COMMANDS } from "../src/mcp/checkpoints-commands.js";
import { DIAGNOSTICS_COMMANDS } from "../src/mcp/diagnostics-commands.js";
import { UNITY_DOCS_COMMANDS } from "../src/mcp/docs-commands.js";
import { PIPELINE_COMMANDS } from "../src/mcp/pipeline-commands.js";
import { PLAN_COMMANDS } from "../src/mcp/plans-commands.js";
import { POLICY_COMMANDS } from "../src/mcp/policy-commands.js";
import { SKILL_COMMANDS } from "../src/mcp/skills-commands.js";

const virtualCommands = [...PLAN_COMMANDS, ...PIPELINE_COMMANDS, ...SKILL_COMMANDS, ...CHECKPOINT_COMMANDS, ...ARTIFACT_COMMANDS, ...UNITY_DOCS_COMMANDS, ...DIAGNOSTICS_COMMANDS, ...POLICY_COMMANDS];

describe("Gateway virtual command catalog", () => {
  it("keeps workflow families behind their feature flags", () => {
    expect(flagsFor(PLAN_COMMANDS)).toEqual(new Set(["sgg_mcp_plans"]));
    expect(flagsFor(PIPELINE_COMMANDS)).toEqual(new Set(["sgg_mcp_pipelines"]));
    expect(flagsFor(SKILL_COMMANDS)).toEqual(new Set(["sgg_mcp_skills"]));
    expect(flagsFor(CHECKPOINT_COMMANDS)).toEqual(new Set(["sgg_mcp_checkpoints"]));
    expect(flagsFor(ARTIFACT_COMMANDS)).toEqual(new Set(["sgg_mcp_artifacts"]));
    expect(flagsFor(UNITY_DOCS_COMMANDS)).toEqual(new Set(["unity_docs_local"]));
    expect(flagsFor(DIAGNOSTICS_COMMANDS)).toEqual(new Set(["sgg_mcp_diagnostics"]));
    expect(flagsFor(POLICY_COMMANDS)).toEqual(new Set(["sgg_mcp_policy"]));
  });

  it("uses expected risk values for workflow command families", () => {
    expect(risksFor(PLAN_COMMANDS)).toEqual(new Map([
      ["plan.template", "read"],
      ["plan.list", "read"],
      ["plan.read", "read"],
      ["plan.create", "write"],
    ]));
    expect(risksFor(PIPELINE_COMMANDS)).toEqual(new Map([
      ["pipeline.template", "read"],
      ["pipeline.list", "read"],
      ["pipeline.read", "read"],
      ["pipeline.create", "write"],
      ["pipeline.validate", "read"],
      ["pipeline.start", "write"],
      ["pipeline.status", "read"],
      ["pipeline.record_step", "write"],
      ["pipeline.cancel", "write"],
    ]));
    expect(risksFor(CHECKPOINT_COMMANDS)).toEqual(new Map([
      ["checkpoint.status", "read"],
      ["checkpoint.list", "read"],
      ["checkpoint.create", "write"],
      ["checkpoint.diff", "read"],
      ["checkpoint.delete", "destructive"],
      ["checkpoint.restore", "destructive"],
      ["checkpoint.prune", "destructive"],
    ]));
    expect(risksFor(ARTIFACT_COMMANDS)).toEqual(new Map([
      ["artifact.list", "read"],
      ["artifact.metadata", "read"],
      ["artifact.prune", "destructive"],
    ]));
    expect(risksFor(SKILL_COMMANDS)).toEqual(new Map([
      ["skill.list", "read"],
      ["skill.read", "read"],
      ["skill.validate", "read"],
      ["skill.import", "write"],
    ]));
    expect(risksFor(UNITY_DOCS_COMMANDS)).toEqual(new Map([
      ["unity.docs.index.status", "read"],
      ["unity.docs.index.rebuild", "write"],
      ["unity.docs.index.clear", "destructive"],
      ["unity.docs.query", "read"],
      ["unity.docs.open_reference", "read"],
    ]));
    expect(uniqueRisks(DIAGNOSTICS_COMMANDS)).toEqual(new Set(["read", "write", "destructive"]));
    expect(uniqueRisks(POLICY_COMMANDS)).toEqual(new Set(["read"]));
  });

  it("uses lowercase dot notation and SGGMcp naming only", () => {
    for (const command of virtualCommands) {
      expect(command.id).toMatch(/^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$/);
      expect(command.id).not.toContain("Canonical" + "Mcp");
      expect(`${command.title} ${command.description} ${command.capability ?? ""}`).not.toContain("Canonical" + "Mcp");
    }
  });
});

function flagsFor(commands: CommandDescriptor[]): Set<string | undefined> {
  return new Set(commands.map((command) => command.feature_flag));
}

function risksFor(commands: CommandDescriptor[]): Map<string, CommandDescriptor["risk"]> {
  return new Map(commands.map((command) => [command.id, command.risk]));
}

function uniqueRisks(commands: CommandDescriptor[]): Set<CommandDescriptor["risk"]> {
  return new Set(commands.map((command) => command.risk));
}
