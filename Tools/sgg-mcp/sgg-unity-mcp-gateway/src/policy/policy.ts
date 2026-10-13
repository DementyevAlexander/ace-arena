import type { CommandDescriptor } from "../bridge/bridge-client.js";
import type { PolicyConfig } from "../config/config.js";

export type PolicyAction = "allow" | "ask" | "deny";

export type PolicyDecision = {
  action: PolicyAction;
  reason: "no_policy" | "project_deny" | "project_rule" | "risk_default" | "extension_trust";
  pattern?: string;
};

export type VisibleCommandDescriptor = CommandDescriptor & {
  requires_confirmation?: true;
};

const BUILTIN_PACKAGE_ID = "com.sungeargames.unity-mcp";

export function evaluatePolicy(policy: PolicyConfig | undefined, project: string, command: Pick<CommandDescriptor, "id" | "risk" | "source" | "package" | "package_id" | "trust">): PolicyDecision {
  if (!policy) {
    if (isUntrustedPackageCommand(command)) {
      return { action: "ask", reason: "extension_trust" };
    }

    return { action: "allow", reason: "no_policy" };
  }

  const projectRules = policy.projects[project];
  if (projectRules) {
    const denyExact = findExact(projectRules.deny, command.id);
    if (denyExact) {
      return { action: "deny", reason: "project_deny", pattern: denyExact };
    }

    const denyWildcard = findWildcard(projectRules.deny, command.id);
    if (denyWildcard) {
      return { action: "deny", reason: "project_deny", pattern: denyWildcard };
    }

    const extensionTrustDecision = evaluateExtensionTrust(policy, command);

    const exactProjectRule = findActionByExact(projectRules, command.id);
    if (exactProjectRule) {
      if (exactProjectRule.action === "allow" && extensionTrustDecision) {
        return extensionTrustDecision;
      }

      return { action: exactProjectRule.action, reason: "project_rule", pattern: exactProjectRule.pattern };
    }

    const wildcardProjectRule = findActionByWildcard(projectRules, command.id);
    if (wildcardProjectRule) {
      if (wildcardProjectRule.action === "allow" && extensionTrustDecision) {
        return extensionTrustDecision;
      }

      return { action: wildcardProjectRule.action, reason: "project_rule", pattern: wildcardProjectRule.pattern };
    }
  }

  const defaultAction = policy.defaults[command.risk];
  const extensionTrustDecision = evaluateExtensionTrust(policy, command);
  if (defaultAction === "allow" && extensionTrustDecision) {
    return extensionTrustDecision;
  }

  return { action: defaultAction, reason: "risk_default" };
}

export function filterVisibleCommands(policy: PolicyConfig | undefined, project: string, commands: CommandDescriptor[]): VisibleCommandDescriptor[] {
  const visible: VisibleCommandDescriptor[] = [];

  for (const command of commands) {
    const decision = evaluatePolicy(policy, project, command);
    if (decision.action === "deny") {
      continue;
    }

    visible.push(decision.action === "ask" ? { ...command, requires_confirmation: true } : command);
  }

  return visible;
}

function findActionByExact(projectRules: { allow: string[]; ask: string[] }, commandId: string): { action: "allow" | "ask"; pattern: string } | undefined {
  const allowPattern = findExact(projectRules.allow, commandId);
  const askPattern = findExact(projectRules.ask, commandId);
  return chooseMostSpecific(allowPattern, askPattern);
}

function evaluateExtensionTrust(policy: PolicyConfig, command: Pick<CommandDescriptor, "source" | "package" | "package_id" | "trust">): PolicyDecision | undefined {
  if (!isUntrustedPackageCommand(command) || isPackageTrusted(policy, command)) {
    return undefined;
  }

  return { action: policy.untrusted_package_default ?? "ask", reason: "extension_trust" };
}

function isUntrustedPackageCommand(command: Pick<CommandDescriptor, "source" | "package" | "package_id" | "trust">): boolean {
  if (!command.source && !command.trust) {
    return false;
  }

  return command.trust === "untrusted_package" || command.source === "manifest" || command.source === "attribute" || command.source === "official_relay";
}

function isPackageTrusted(policy: PolicyConfig, command: Pick<CommandDescriptor, "package" | "package_id" | "trust">): boolean {
  const packageId = command.package_id || command.package;
  return command.trust === "builtin" || packageId === BUILTIN_PACKAGE_ID || (policy.trusted_packages ?? []).includes(packageId);
}

function findActionByWildcard(projectRules: { allow: string[]; ask: string[] }, commandId: string): { action: "allow" | "ask"; pattern: string } | undefined {
  const allowPattern = findWildcard(projectRules.allow, commandId);
  const askPattern = findWildcard(projectRules.ask, commandId);
  return chooseMostSpecific(allowPattern, askPattern);
}

function chooseMostSpecific(allowPattern: string | undefined, askPattern: string | undefined): { action: "allow" | "ask"; pattern: string } | undefined {
  if (!allowPattern && !askPattern) {
    return undefined;
  }

  if (allowPattern && (!askPattern || allowPattern.length >= askPattern.length)) {
    return { action: "allow", pattern: allowPattern };
  }

  if (askPattern) {
    return { action: "ask", pattern: askPattern };
  }

  return undefined;
}

function findExact(patterns: string[], commandId: string): string | undefined {
  return patterns.find((pattern) => !pattern.endsWith("*") && pattern === commandId);
}

function findWildcard(patterns: string[], commandId: string): string | undefined {
  const matches = patterns.filter((pattern) => pattern.endsWith("*") && commandId.startsWith(pattern.slice(0, -1)));
  matches.sort((left, right) => right.length - left.length);
  return matches[0];
}
