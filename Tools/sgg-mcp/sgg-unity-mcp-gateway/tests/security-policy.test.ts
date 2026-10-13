import { describe, expect, it } from "vitest";

import type { CommandDescriptor } from "../src/bridge/bridge-client.js";
import type { PolicyConfig } from "../src/config/config.js";
import { evaluatePolicy, filterVisibleCommands } from "../src/policy/policy.js";

const policy: PolicyConfig = {
  schema_version: 1,
  defaults: {
    read: "allow",
    write: "ask",
    execute: "deny",
    destructive: "ask",
    network: "deny",
  },
  trusted_packages: [],
  untrusted_package_default: "ask",
  projects: {
    game: {
      allow: ["compile.status", "asset.search", "scene.*"],
      ask: ["asset.delete", "asset.index.*"],
      deny: ["shell.*", "network.fetch"],
    },
  },
};

describe("security policy", () => {
  it("evaluates deny before project allow/ask rules", () => {
    expect(evaluatePolicy(policy, "game", command("network.fetch", "read"))).toMatchObject({ action: "deny", reason: "project_deny" });
    expect(evaluatePolicy(policy, "game", command("shell.run", "read"))).toMatchObject({ action: "deny", reason: "project_deny" });
  });

  it("supports exact and wildcard allow/ask rules", () => {
    expect(evaluatePolicy(policy, "game", command("compile.status", "read"))).toMatchObject({ action: "allow", pattern: "compile.status" });
    expect(evaluatePolicy(policy, "game", command("scene.open", "write"))).toMatchObject({ action: "allow", pattern: "scene.*" });
    expect(evaluatePolicy(policy, "game", command("asset.index.rebuild", "read"))).toMatchObject({ action: "ask", pattern: "asset.index.*" });
  });

  it("supports allow-all wildcard project rules", () => {
    const allowAllPolicy: PolicyConfig = {
      ...policy,
      projects: { game: { allow: ["*"], ask: [], deny: [] } },
    };

    expect(evaluatePolicy(allowAllPolicy, "game", command("editor.menu.execute", "execute"))).toMatchObject({ action: "allow", pattern: "*" });
  });

  it("keeps untrusted extension commands trust-gated under allow-all", () => {
    const allowAllPolicy: PolicyConfig = {
      ...policy,
      projects: { game: { allow: ["*"], ask: [], deny: [] } },
    };
    const untrustedCommand = {
      ...command("extension.do_anything", "execute"),
      source: "manifest",
      package_id: "com.example.untrusted-tools",
      trust: "untrusted_package",
    } as const;

    expect(evaluatePolicy(allowAllPolicy, "game", untrustedCommand)).toMatchObject({ action: "ask", reason: "extension_trust" });
  });

  it("falls back to risk defaults", () => {
    expect(evaluatePolicy(policy, "game", command("asset.import", "write"))).toMatchObject({ action: "ask", reason: "risk_default" });
    expect(evaluatePolicy(policy, "tools", command("editor.execute_csharp", "execute"))).toMatchObject({ action: "deny", reason: "risk_default" });
  });

  it("does not auto-allow untrusted manifest commands by risk or project allow rules", () => {
    const untrustedReadCommand = {
      ...command("asset.search", "read"),
      source: "manifest",
      package_id: "com.example.untrusted-tools",
      trust: "untrusted_package",
    } as const;

    expect(evaluatePolicy(policy, "game", untrustedReadCommand)).toMatchObject({ action: "ask", reason: "extension_trust" });
    expect(evaluatePolicy(policy, "other", untrustedReadCommand)).toMatchObject({ action: "ask", reason: "extension_trust" });
  });

  it("allows trusted package commands to follow normal policy and risk rules", () => {
    const trustedPolicy = { ...policy, trusted_packages: ["com.example.trusted-tools"] };
    const trustedReadCommand = {
      ...command("asset.search", "read"),
      source: "manifest",
      package_id: "com.example.trusted-tools",
      trust: "untrusted_package",
    } as const;

    expect(evaluatePolicy(trustedPolicy, "game", trustedReadCommand)).toMatchObject({ action: "allow", pattern: "asset.search" });
    expect(evaluatePolicy(trustedPolicy, "other", trustedReadCommand)).toMatchObject({ action: "allow", reason: "risk_default" });
  });

  it("filters denied commands and marks ask commands as requiring confirmation", () => {
    const visible = filterVisibleCommands(policy, "game", [
      command("compile.status", "read"),
      command("asset.import", "write"),
      command("shell.run", "execute"),
      { ...command("asset.search", "read"), source: "manifest", package_id: "com.example.untrusted-tools", trust: "untrusted_package" },
    ]);

    expect(visible.map((item) => item.id)).toEqual(["compile.status", "asset.import", "asset.search"]);
    expect(visible[1]).toMatchObject({ id: "asset.import", requires_confirmation: true });
    expect(visible[2]).toMatchObject({ id: "asset.search", requires_confirmation: true });
  });
});

function command(id: string, risk: CommandDescriptor["risk"]): CommandDescriptor {
  return {
    id,
    title: id,
    description: id,
    package: "com.example.test",
    risk,
    idempotency: "safe",
    timeout_ms: 1000,
    schema: { type: "object" },
  };
}
