import fs from "node:fs";
import path from "node:path";

import { policyConfigSchema, type PolicyConfig } from "../config/config.js";
import { applyPolicyProfile, createDefaultPolicy, isPolicyProfileName, POLICY_PROFILES, POLICY_PROFILE_NAMES, setProjectPolicyRule, summarizeProjectPolicy, type PolicyAction } from "./policy-profiles.js";

export type PolicyCliIo = {
  stdout?: (text: string) => void;
  stderr?: (text: string) => void;
};

type ParsedOptions = {
  values: Record<string, string>;
  dryRun: boolean;
  confirmRisk: boolean;
};

export function runPolicyCli(argv: string[], io: PolicyCliIo = {}): number {
  const stdout = io.stdout ?? ((text) => process.stdout.write(text));
  const stderr = io.stderr ?? ((text) => process.stderr.write(text));
  const command = argv[0];

  try {
    if (!command || command === "--help" || command === "-h") {
      stderr(policyHelpText());
      return command ? 0 : 1;
    }

    if (command === "profiles") {
      stdout(jsonLine({ profiles: POLICY_PROFILE_NAMES.map((name) => ({ name, ...POLICY_PROFILES[name] })) }));
      return 0;
    }

    const options = parseOptions(argv.slice(1));
    if (command === "inspect") {
      const policyPath = requireOption(options, "policy");
      const policy = loadPolicyForEdit(policyPath, options.values["from-policy"]);
      const project = options.values.project;
      stdout(jsonLine(project ? summarizeProjectPolicy(policy, project) : policy));
      return 0;
    }

    if (command === "apply-profile") {
      const policyPath = requireOption(options, "policy");
      const project = requireOption(options, "project");
      const profile = requireOption(options, "profile");
      if (!isPolicyProfileName(profile)) {
        throw new Error(`Unknown policy profile: ${profile}`);
      }

      if (profile === "allow-all" && !options.confirmRisk) {
        throw new Error("Refusing to apply allow-all without --confirm-risk");
      }

      const policy = loadPolicyForEdit(policyPath, options.values["from-policy"]);
      const nextPolicy = applyPolicyProfile(policy, project, profile);
      writeOrPreview(policyPath, nextPolicy, options, stdout, {
        project,
        profile,
        features: POLICY_PROFILES[profile].features ?? [],
        rules: summarizeProjectPolicy(nextPolicy, project),
      });
      return 0;
    }

    if (command === "set-rule") {
      const policyPath = requireOption(options, "policy");
      const project = requireOption(options, "project");
      const action = parseAction(requireOption(options, "action"));
      const pattern = requireOption(options, "command");
      if (requiresRiskConfirmation(action, pattern) && !options.confirmRisk) {
        throw new Error(`Refusing to allow high-risk command pattern '${pattern}' without --confirm-risk`);
      }

      const policy = loadPolicyForEdit(policyPath, options.values["from-policy"]);
      const nextPolicy = setProjectPolicyRule(policy, project, action, pattern);
      writeOrPreview(policyPath, nextPolicy, options, stdout, {
        project,
        action,
        command: pattern,
        rules: summarizeProjectPolicy(nextPolicy, project),
      });
      return 0;
    }

    throw new Error(`Unknown policy command: ${command}`);
  } catch (error) {
    stderr(`${error instanceof Error ? error.message : String(error)}\n`);
    return 1;
  }
}

export function loadPolicyForEdit(policyPath: string, fromPolicyPath?: string): PolicyConfig {
  if (fs.existsSync(policyPath)) {
    return readPolicyFile(policyPath);
  }

  if (fromPolicyPath && fs.existsSync(fromPolicyPath)) {
    return readPolicyFile(fromPolicyPath);
  }

  return createDefaultPolicy();
}

export function writePolicyFile(policyPath: string, policy: PolicyConfig): void {
  const directory = path.dirname(policyPath);
  fs.mkdirSync(directory, { recursive: true });
  const tempPath = path.join(directory, `.${path.basename(policyPath)}.${process.pid}.tmp`);
  fs.writeFileSync(tempPath, policyJson(policy), "utf8");
  fs.renameSync(tempPath, policyPath);
}

function writeOrPreview(policyPath: string, policy: PolicyConfig, options: ParsedOptions, stdout: (text: string) => void, summary: Record<string, unknown>): void {
  if (!options.dryRun) {
    writePolicyFile(policyPath, policy);
  }

  stdout(jsonLine({
    dry_run: options.dryRun,
    policy_path: policyPath,
    ...summary,
  }));
}

function readPolicyFile(policyPath: string): PolicyConfig {
  return policyConfigSchema.parse(JSON.parse(fs.readFileSync(policyPath, "utf8")));
}

function parseOptions(argv: string[]): ParsedOptions {
  const values: Record<string, string> = {};
  let dryRun = false;
  let confirmRisk = false;
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (!arg) {
      continue;
    }
    if (arg === "--dry-run") {
      dryRun = true;
      continue;
    }
    if (arg === "--confirm-risk") {
      confirmRisk = true;
      continue;
    }
    if (!arg.startsWith("--")) {
      throw new Error(`Unexpected argument: ${arg}`);
    }
    const key = arg.slice(2);
    const value = argv[index + 1];
    if (!value || value.startsWith("--")) {
      throw new Error(`${arg} requires a value`);
    }
    values[key] = value;
    index += 1;
  }
  return { values, dryRun, confirmRisk };
}

function requireOption(options: ParsedOptions, key: string): string {
  const value = options.values[key];
  if (!value) {
    throw new Error(`--${key} is required`);
  }
  return value;
}

function parseAction(value: string): PolicyAction {
  if (value === "allow" || value === "ask" || value === "deny") {
    return value;
  }
  throw new Error("--action must be allow, ask, or deny");
}

function requiresRiskConfirmation(action: PolicyAction, pattern: string): boolean {
  if (action !== "allow") {
    return false;
  }

  return pattern === "*"
    || /^shell\./.test(pattern)
    || /^process\./.test(pattern)
    || /^network\./.test(pattern)
    || /^editor\.execute_csharp/.test(pattern)
    || /^editor\.roslyn\.install$/.test(pattern)
    || /(^|\.)(delete|remove)$/.test(pattern)
    || pattern.endsWith("delete")
    || pattern.endsWith("remove");
}

function policyJson(policy: PolicyConfig): string {
  return `${JSON.stringify(policy, null, 2)}\n`;
}

function jsonLine(value: unknown): string {
  return `${JSON.stringify(value, null, 2)}\n`;
}

function policyHelpText(): string {
  return `Usage:
  sgg-unity-mcp-gateway policy profiles
  sgg-unity-mcp-gateway policy inspect --policy path [--project id] [--from-policy template]
  sgg-unity-mcp-gateway policy apply-profile --policy path --project id --profile name [--from-policy template] [--dry-run] [--confirm-risk]
  sgg-unity-mcp-gateway policy set-rule --policy path --project id --action allow|ask|deny --command pattern [--from-policy template] [--dry-run] [--confirm-risk]
`;
}
