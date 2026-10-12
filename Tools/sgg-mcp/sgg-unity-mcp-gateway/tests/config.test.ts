import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { describe, expect, it } from "vitest";

import { expandEnvPlaceholders, gatewayConfigSchema, loadGatewayConfig, loadPolicyConfig, parseGatewayConfig } from "../src/config/config.js";
import { testRunStorageOptionsFromConfig } from "../src/operations/test-run-storage-settings.js";

const root = process.cwd();

describe("config loading", () => {
  it("uses exact independent 200 MiB / 192000 owner-store defaults without changing privacy or approvals", () => {
    const input = JSON.parse(fs.readFileSync(path.join(root, "config/gateway.example.json"), "utf8"));
    const config = gatewayConfigSchema.parse(input);
    expect(testRunStorageOptionsFromConfig(config)).toEqual({ maxBytes: 209715200, maxBindings: 192000, retentionMs: 30 * 86400000 });
    const parsed = gatewayConfigSchema.parse({ ...input, test_runs: { storage: {} } });
    expect(parsed.test_runs?.storage).toEqual({ max_bytes: 209715200, max_bindings: 192000, retention_days: 30 });
    expect(parsed.approvals).toEqual(config.approvals);
    expect(parsed.features).toEqual(config.features);
  });

  it("preserves explicit old quotas and keeps byte and record settings independent", () => {
    const input = JSON.parse(fs.readFileSync(path.join(root, "config/gateway.example.json"), "utf8"));
    expect(testRunStorageOptionsFromConfig(gatewayConfigSchema.parse({ ...input, test_runs: { storage: { max_bytes: 8388608, max_bindings: 4096 } } })))
      .toEqual({ maxBytes: 8388608, maxBindings: 4096, retentionMs: 30 * 86400000 });
    expect(gatewayConfigSchema.parse({ ...input, test_runs: { storage: { max_bytes: 1048576 } } }).test_runs?.storage.max_bindings).toBe(192000);
    expect(gatewayConfigSchema.parse({ ...input, test_runs: { storage: { max_bytes: 1073741824, max_bindings: 1000000, retention_days: 366 } } }).test_runs?.storage)
      .toEqual({ max_bytes: 1073741824, max_bindings: 1000000, retention_days: 366 });
  });

  it.each([
    { max_bytes: 1048575 }, { max_bytes: 1073741825 }, { max_bytes: "209715200" }, { max_bytes: 1048576.5 },
    { max_bindings: 0 }, { max_bindings: 1000001 }, { max_bindings: 192000.5 }, { max_bindings: "192000" },
    { retention_days: 0 }, { retention_days: 367 }, { retention_days: 1.5 }, { retention_days: "30" },
    { arbitrary_path: "private" },
  ])("rejects invalid test owner-store settings: %j", storage => {
    const input = JSON.parse(fs.readFileSync(path.join(root, "config/gateway.example.json"), "utf8"));
    expect(() => gatewayConfigSchema.parse({ ...input, test_runs: { storage } })).toThrow();
  });

  it("retains nested/top-level strict unknown-key and duplicate-key checks for storage settings", () => {
    const input = JSON.parse(fs.readFileSync(path.join(root, "config/gateway.example.json"), "utf8"));
    expect(() => gatewayConfigSchema.parse({ ...input, test_runs: { storage: {}, other: true } })).toThrow();
    expect(() => gatewayConfigSchema.parse({ ...input, test_run_store: {} })).toThrow();
    const source = JSON.stringify(input).replace(/}$/, ',"test_runs":{"storage":{"max_bindings":192000,"max_bindings":4096}}}');
    expect(() => parseGatewayConfig(source)).toThrow(/duplicate JSON member/i);
  });

  it("loads the gateway example config and expands Windows-style env placeholders", () => {
    const config = loadGatewayConfig(path.join(root, "config/gateway.example.json"), {
      LOCALAPPDATA: "C:/Users/Alice/AppData/Local",
      APPDATA: "C:/Users/Alice/AppData/Roaming",
      UNITY_OFFICIAL_MCP_RELAY: "C:/Unity/Assistant/UnityMcpRelay.exe",
    });

    expect(config.mcp).toEqual({
      host: "127.0.0.1",
      port: 27182,
      token_env: "SGG_UNITY_MCP_TOKEN",
      allowed_origins: [],
      allow_non_loopback: false,
      max_body_bytes: 1048576,
      request_timeout_ms: 30000,
      shutdown_timeout_ms: 5000,
      sessions: { max_active: 128, idle_ttl_ms: 1800000, cleanup_interval_ms: 60000 },
      rate_limit: { window_ms: 60000, max_requests: 120, max_buckets: 4096, cleanup_interval_ms: 60000, trusted_proxy: false, trusted_proxy_peers: [] },
    });
    expect(config.registry.instances_dir).toBe("C:/Users/Alice/AppData/Local/SGG/UnityMcpGateway/instances");
    expect(config.logging.audit_dir).toBe("C:/Users/Alice/AppData/Local/SGG/UnityMcpGateway/audit");
    expect(config.cache?.asset_index_root).toBe("C:/Users/Alice/AppData/Local/SGG/UnityMcpGateway/index");
    expect(config.approvals).toEqual({
      dir: "C:/Users/Alice/AppData/Local/SGG/UnityMcpGateway/approvals",
      hmac_key_file: "C:/Users/Alice/AppData/Local/SGG/UnityMcpGateway/secrets/approval-hmac-v1.key",
      default_ttl_ms: 300000,
      auto_allow_ask: false,
      auto_allow_known_unity_popups: false,
      auto_allow_stale_registry_cleanup_without_elicitation: true,
    });
    expect(config.skills?.unity_user_skills_root).toBe("C:/Users/Alice/AppData/Roaming/Unity/AIAssistantSkills");
    expect(config.scene_authoring?.mono_behaviour_allowlist).toEqual([]);
    expect(config.official_relay).toMatchObject({
      enabled: false,
      command: "C:/Unity/Assistant/UnityMcpRelay.exe",
      args: ["--mcp"],
      allow_tools: [],
      unity_relative_path_fields: {},
    });
    expect(config.registry.stale_after_ms).toBe(90000);
    expect(config.registry.cleanup_after_ms).toBe(600000);
    expect(config.features?.semantic_scene_inspection).toBe(false);
    expect(config.features?.asset_readonly).toBe(false);
    expect(config.features?.asset_safe_writes).toBe(false);
    expect(config.features?.editor_safe_writes).toBe(false);
    expect(config.features?.package_registry_network).toBe(false);
    expect(config.features?.gameplay_data_readonly).toBe(false);
    expect(config.features?.unity_docs_local).toBe(false);
    expect(config.features?.profiler_recording).toBe(false);
    expect(config.features?.ui_readonly).toBe(false);
    expect(config.features?.ugui_visual_inspection).toBe(false);
    expect(config.features?.ugui_interaction_tests).toBe(false);
    expect(config.features?.uitk_interaction_tests).toBe(false);
    expect(config.features?.official_unity_mcp_relay).toBe(false);
    expect(config.features?.sgg_mcp_diagnostics).toBe(false);
    expect(config.features?.sgg_mcp_plans).toBe(false);
    expect(config.features?.sgg_mcp_pipelines).toBe(false);
    expect(config.features?.sgg_mcp_skills).toBe(false);
    expect(config.features?.sgg_mcp_checkpoints).toBe(false);
    expect(config.features?.sgg_mcp_artifacts).toBe(false);
    expect(config.features?.sgg_mcp_policy).toBe(false);
  });

  it("leaves missing env placeholders explicit instead of silently deleting them", () => {
    expect(expandEnvPlaceholders("%LOCALAPPDATA%/SGG", {})).toBe("%LOCALAPPDATA%/SGG");
  });

  it("defaults the approval HMAC key to the fixed local application data path", () => {
    const input = JSON.parse(fs.readFileSync(path.join(root, "config/gateway.example.json"), "utf8")) as { approvals: Record<string, unknown> };
    delete input.approvals.hmac_key_file;
    const config = gatewayConfigSchema.parse(input);
    expect(config.approvals?.hmac_key_file).toBe("%LOCALAPPDATA%/SGG/UnityMcpGateway/secrets/approval-hmac-v1.key");
  });

  it("defaults the narrow stale cleanup compatibility mode on and preserves an explicit disable", () => {
    const input = JSON.parse(fs.readFileSync(path.join(root, "config/gateway.example.json"), "utf8")) as { approvals: Record<string, unknown> };
    delete input.approvals.auto_allow_stale_registry_cleanup_without_elicitation;
    expect(gatewayConfigSchema.parse(input).approvals?.auto_allow_stale_registry_cleanup_without_elicitation).toBe(true);

    input.approvals.auto_allow_stale_registry_cleanup_without_elicitation = false;
    expect(gatewayConfigSchema.parse(input).approvals?.auto_allow_stale_registry_cleanup_without_elicitation).toBe(false);
  });

  it("requires explicit boolean opt-in to known Unity popup responses", () => {
    const input = JSON.parse(fs.readFileSync(path.join(root, "config/gateway.example.json"), "utf8")) as { approvals: Record<string, unknown> };
    delete input.approvals.auto_allow_known_unity_popups;
    expect(gatewayConfigSchema.parse(input).approvals?.auto_allow_known_unity_popups).toBe(false);
    input.approvals.auto_allow_known_unity_popups = true;
    expect(gatewayConfigSchema.parse(input).approvals?.auto_allow_known_unity_popups).toBe(true);
    input.approvals.auto_allow_known_unity_popups = false;
    expect(gatewayConfigSchema.parse(input).approvals?.auto_allow_known_unity_popups).toBe(false);
    input.approvals.auto_allow_known_unity_popups = "true";
    expect(() => gatewayConfigSchema.parse(input)).toThrow();
  });

  it.each([
    '"auto_allow_known_unity_popups":false,"auto_allow_known_unity_popups":true',
    '"auto_allow_known_unity_popups":true,"auto_allow_known_unity_popups":false',
    '"auto_allow_known_unity_popups":false,"auto_allow_known_unity_pop\\u0075ps":true',
  ])("rejects ambiguous popup consent keys: %s", (members) => {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-config-consent-"));
    try {
      const source = fs.readFileSync(path.join(root, "config/gateway.example.json"), "utf8")
        .replace('"auto_allow_known_unity_popups": false', members);
      const configPath = path.join(directory, "gateway.json");
      fs.writeFileSync(configPath, source);
      expect(() => loadGatewayConfig(configPath)).toThrow(/duplicate JSON member/i);
    } finally { fs.rmSync(directory, { recursive: true, force: true }); }
  });

  it("rejects duplicate approvals objects instead of allowing last-key-wins consent", () => {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-config-consent-"));
    try {
      const source = fs.readFileSync(path.join(root, "config/gateway.example.json"), "utf8");
      const configPath = path.join(directory, "gateway.json");
      fs.writeFileSync(configPath, source.replace(/}\s*$/, ',"approvals":{"dir":"isolated","auto_allow_known_unity_popups":true}}'));
      expect(() => loadGatewayConfig(configPath)).toThrow(/duplicate JSON member/i);
    } finally { fs.rmSync(directory, { recursive: true, force: true }); }
  });

  it("rejects audit files too small to hold strict start and terminal records", () => {
    const input = JSON.parse(fs.readFileSync(path.join(root, "config/gateway.example.json"), "utf8")) as { logging: Record<string, unknown> };
    input.logging.audit_retention = { max_file_bytes: 128 * 1024 - 1 };
    expect(() => gatewayConfigSchema.parse(input)).toThrow();
  });

  it("requires unique canonical peer IPs in trusted proxy mode", () => {
    const input = JSON.parse(fs.readFileSync(path.join(root, "config/gateway.example.json"), "utf8")) as { mcp: { rate_limit: Record<string, unknown> } };
    input.mcp.rate_limit.trusted_proxy = true;
    expect(() => gatewayConfigSchema.parse(input)).toThrow(/trusted proxy peer/i);

    input.mcp.rate_limit.trusted_proxy_peers = ["::ffff:127.0.0.1"];
    expect(() => gatewayConfigSchema.parse(input)).toThrow(/canonical IP/i);

    input.mcp.rate_limit.trusted_proxy_peers = ["127.0.0.1", "127.0.0.1"];
    expect(() => gatewayConfigSchema.parse(input)).toThrow(/unique/i);

    input.mcp.rate_limit.trusted_proxy_peers = ["127.0.0.1", "::1"];
    expect(gatewayConfigSchema.parse(input).mcp.rate_limit.trusted_proxy_peers).toEqual(["127.0.0.1", "::1"]);
  });

  it("rejects feature settings that the Bridge invoke schema cannot serialize", () => {
    const input = JSON.parse(fs.readFileSync(path.join(root, "config/gateway.example.json"), "utf8")) as { features: Record<string, boolean> };
    input.features = Object.fromEntries(Array.from({ length: 257 }, (_, index) => [`feature_${index}`, true]));
    expect(() => gatewayConfigSchema.parse(input)).toThrow(/too many feature flags/i);

    input.features = { "invalid feature": true };
    expect(() => gatewayConfigSchema.parse(input)).toThrow();
  });

  it("loads the policy example config", () => {
    const policy = loadPolicyConfig(path.join(root, "config/policy.example.json"));

    expect(policy.schema_version).toBe(1);
    expect(policy.defaults.execute).toBe("deny");
    expect(policy.projects.game?.allow).toContain("runtime.capture_state");
    expect(policy.projects.game?.allow).toContain("build.profile.list");
    expect(policy.projects.game?.allow).toContain("package.operation.status");
    expect(policy.projects.game?.allow).toContain("package.operation.list");
    expect(policy.projects.game?.allow).toContain("unity.docs.index.status");
    expect(policy.projects.game?.allow).toContain("unity.docs.query");
    expect(policy.projects.game?.allow).toContain("unity.docs.open_reference");
    expect(policy.projects.game?.allow).toContain("plan.template");
    expect(policy.projects.game?.allow).toContain("plan.list");
    expect(policy.projects.game?.allow).toContain("plan.read");
    expect(policy.projects.game?.allow).toContain("pipeline.template");
    expect(policy.projects.game?.allow).toContain("pipeline.list");
    expect(policy.projects.game?.allow).toContain("pipeline.read");
    expect(policy.projects.game?.allow).toContain("pipeline.validate");
    expect(policy.projects.game?.allow).toContain("pipeline.status");
    expect(policy.projects.game?.allow).toContain("checkpoint.status");
    expect(policy.projects.game?.allow).toContain("checkpoint.list");
    expect(policy.projects.game?.allow).toContain("checkpoint.diff");
    expect(policy.projects.game?.allow).toContain("artifact.list");
    expect(policy.projects.game?.allow).toContain("artifact.metadata");
    expect(policy.projects.game?.allow).toContain("profiler.frame.summary");
    expect(policy.projects.game?.allow).toContain("profiler.report.read");
    expect(policy.projects.game?.allow).toContain("profiler.memory.snapshot.compare");
    expect(policy.projects.game?.ask).toContain("profiler.recording.start");
    expect(policy.projects.game?.ask).toContain("profiler.recording.stop");
    expect(policy.projects.game?.ask).toContain("profiler.capture.save");
    expect(policy.projects.game?.allow).toContain("editor.window.screenshot");
    expect(policy.projects.game?.allow).toContain("desktop.region.screenshot");
    expect(policy.projects.game?.allow).toContain("ui.screen.inventory");
    expect(policy.projects.game?.allow).toContain("ui.screen.inspect");
    expect(policy.projects.game?.allow).toContain("ui.interaction.map");
    expect(policy.projects.game?.allow).toContain("skill.list");
    expect(policy.projects.game?.allow).toContain("skill.read");
    expect(policy.projects.game?.allow).toContain("skill.validate");
    expect(policy.projects.game?.allow).toContain("policy.explain");
    expect(policy.projects.game?.allow).toContain("material.property_schema");
    expect(policy.projects.game?.ask).toContain("asset.folder.create");
    expect(policy.projects.game?.ask).toContain("material.*");
    expect(policy.projects.game?.ask).toContain("package.install");
    expect(policy.projects.game?.ask).toContain("package.install.git");
    expect(policy.projects.game?.ask).toContain("package.remove");
    expect(policy.projects.game?.ask).toContain("package.search");
    expect(policy.projects.game?.ask).toContain("runtime.state_machine.run");
    expect(policy.projects.game?.ask).toContain("ugui.interaction.smoke");
    expect(policy.projects.game?.ask).toContain("uitk.interaction.smoke");
    expect(policy.projects.game?.ask).toContain("package.operation.prune");
    expect(policy.projects.game?.ask).toContain("plan.create");
    expect(policy.projects.game?.ask).toContain("pipeline.create");
    expect(policy.projects.game?.ask).toContain("pipeline.start");
    expect(policy.projects.game?.ask).toContain("pipeline.record_step");
    expect(policy.projects.game?.ask).toContain("pipeline.cancel");
    expect(policy.projects.game?.ask).toContain("skill.import");
    expect(policy.projects.game?.ask).toContain("checkpoint.create");
    expect(policy.projects.game?.ask).toContain("checkpoint.delete");
    expect(policy.projects.game?.ask).toContain("checkpoint.restore");
    expect(policy.projects.game?.ask).toContain("checkpoint.prune");
    expect(policy.projects.game?.ask).toContain("artifact.prune");
    expect(policy.projects.game?.ask).toContain("unity.docs.index.rebuild");
    expect(policy.projects.game?.ask).toContain("unity.docs.index.clear");
    expect(policy.projects.game?.deny).toContain("network.*");
  });
});
