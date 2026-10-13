import fs from "node:fs";

import { visit } from "jsonc-parser/lib/esm/main.js";
import { z } from "zod";

import { DEFAULT_APPROVAL_HMAC_KEY_FILE } from "../approvals/approval-key-manager.js";
import { BRIDGE_PROTOCOL_BOUNDS } from "../bridge/bridge-protocol.js";
import { canonicalizeIpAddress } from "../mcp/ip-address.js";
import { TEST_RUN_STORAGE_DEFAULT_MAX_BINDINGS, TEST_RUN_STORAGE_DEFAULT_MAX_BYTES, TEST_RUN_STORAGE_DEFAULT_RETENTION_DAYS, TEST_RUN_STORAGE_MAX_BINDINGS, TEST_RUN_STORAGE_MAX_BYTES, TEST_RUN_STORAGE_MIN_BYTES } from "../operations/test-run-storage-settings.js";

const riskActionSchema = z.enum(["allow", "ask", "deny"]);
const riskLevelSchema = z.enum(["read", "write", "execute", "destructive", "network"]);
const featureFlagsSchema = z.record(
  z.string().min(1).max(BRIDGE_PROTOCOL_BOUNDS.identifierChars).regex(/^[A-Za-z0-9._-]+$/),
  z.boolean(),
).refine((flags) => Object.keys(flags).length <= BRIDGE_PROTOCOL_BOUNDS.featureFlags, "Too many feature flags");
const officialRelaySchema = z
  .object({
    enabled: z.boolean().default(false),
    command: z.string().min(1).optional(),
    args: z.array(z.string()).default([]),
    env: z.record(z.string(), z.string()).default({}),
    allow_tools: z.array(z.string().min(1)).default([]),
    tool_risks: z.record(z.string(), riskLevelSchema).default({}),
    unity_relative_path_fields: z.record(z.string(), z.array(z.string().min(1))).default({}),
    connect_timeout_ms: z.number().int().positive().default(3000),
    call_timeout_ms: z.number().int().positive().default(30000),
  })
  .strict();

const skillsSchema = z
  .object({
    unity_user_skills_root: z.string().min(1).optional(),
  })
  .strict();

const monoBehaviourAllowlistEntrySchema = z
  .object({
    assembly_name: z.string().min(1),
    full_type_name: z.string().min(1),
    note: z.string().optional(),
  })
  .strict();

const sceneAuthoringSchema = z
  .object({
    mono_behaviour_allowlist: z.array(monoBehaviourAllowlistEntrySchema).default([]),
  })
  .strict();

export const gatewayConfigSchema = z
  .object({
    mcp: z
      .object({
        host: z.string().min(1),
        port: z.number().int().min(1).max(65535),
        token_env: z.string().min(1).optional(),
        allowed_origins: z.array(z.string().url()).default([]),
        allow_non_loopback: z.boolean().default(false),
        max_body_bytes: z.number().int().positive().default(1024 * 1024),
        request_timeout_ms: z.number().int().positive().default(30_000),
        shutdown_timeout_ms: z.number().int().positive().max(60_000).default(5_000),
        sessions: z
          .object({
            max_active: z.number().int().positive().max(10_000),
            idle_ttl_ms: z.number().int().positive().max(604_800_000),
            cleanup_interval_ms: z.number().int().positive().max(86_400_000),
          })
          .strict()
          .default({ max_active: 128, idle_ttl_ms: 1_800_000, cleanup_interval_ms: 60_000 }),
        rate_limit: z
          .object({
            window_ms: z.number().int().positive().max(86_400_000),
            max_requests: z.number().int().positive().max(1_000_000),
            max_buckets: z.number().int().positive().max(1_000_000).default(4096),
            cleanup_interval_ms: z.number().int().positive().max(86_400_000).default(60_000),
            trusted_proxy: z.boolean().default(false),
            trusted_proxy_peers: z
              .array(z.string().refine((value) => canonicalizeIpAddress(value) === value, "Expected a canonical IP address"))
              .max(64)
              .default([]),
          })
          .strict()
          .superRefine((value, context) => {
            if (value.trusted_proxy && value.trusted_proxy_peers.length === 0) {
              context.addIssue({ code: "custom", path: ["trusted_proxy_peers"], message: "At least one trusted proxy peer is required" });
            }
            if (new Set(value.trusted_proxy_peers).size !== value.trusted_proxy_peers.length) {
              context.addIssue({ code: "custom", path: ["trusted_proxy_peers"], message: "Trusted proxy peers must be unique" });
            }
          })
          .default({ window_ms: 60_000, max_requests: 120, max_buckets: 4096, cleanup_interval_ms: 60_000, trusted_proxy: false, trusted_proxy_peers: [] }),
      })
      .strict(),
    registry: z
      .object({
        instances_dir: z.string().min(1),
        stale_after_ms: z.number().int().positive(),
        cleanup_after_ms: z.number().int().positive().optional(),
        cache_max_entries: z.number().int().positive().max(10_000).default(1024),
        cache_ttl_ms: z.number().int().positive().max(86_400_000).default(600_000),
        invalid_diagnostics_max: z.number().int().positive().max(128).default(32),
        scan_max_files: z.number().int().positive().max(10_000).default(1024),
      })
      .strict()
      .superRefine((value, context) => {
        if (value.cache_max_entries < value.scan_max_files) context.addIssue({ code: "custom", path: ["cache_max_entries"], message: "cache_max_entries must be at least scan_max_files" });
      }),
    logging: z
      .object({
        level: z.enum(["debug", "info", "warn", "error"]).default("info"),
        audit_enabled: z.boolean().default(true),
        audit_dir: z.string().min(1).optional(),
        audit_retention: z.object({
          max_file_bytes: z.number().int().min(128 * 1024).max(1024 * 1024 * 1024).default(10 * 1024 * 1024),
          max_files: z.number().int().positive().max(10_000).default(31),
          max_age_ms: z.number().int().positive().max(366 * 86_400_000).default(30 * 86_400_000),
          max_total_bytes: z.number().int().positive().max(10 * 1024 * 1024 * 1024).default(256 * 1024 * 1024),
        }).strict().optional(),
      })
      .strict(),
    cache: z
      .object({
        asset_index_root: z.string().min(1).optional(),
      })
      .strict()
      .optional(),
    approvals: z
      .object({
        dir: z.string().min(1),
        hmac_key_file: z.string().min(1).default(DEFAULT_APPROVAL_HMAC_KEY_FILE),
        default_ttl_ms: z.number().int().positive().default(300_000),
        auto_allow_ask: z.boolean().default(false),
        auto_allow_known_unity_popups: z.boolean().default(false),
        auto_allow_stale_registry_cleanup_without_elicitation: z.boolean().default(true),
        retention: z.object({
          max_records: z.number().int().positive().max(100_000).default(1024),
          max_bytes: z.number().int().positive().max(1024 * 1024 * 1024).default(8 * 1024 * 1024),
          quarantine_max_files: z.number().int().positive().max(10_000).default(128),
          quarantine_max_bytes: z.number().int().positive().max(1024 * 1024 * 1024).default(2 * 1024 * 1024),
          quarantine_max_age_ms: z.number().int().positive().max(366 * 86_400_000).default(7 * 86_400_000),
          claim_ttl_ms: z.number().int().positive().max(86_400_000).default(15 * 60_000),
        }).strict().optional(),
      })
      .strict()
      .optional(),
    skills: skillsSchema.optional(),
    test_runs: z.object({
      storage: z.object({
        max_bytes: z.number().int().min(TEST_RUN_STORAGE_MIN_BYTES).max(TEST_RUN_STORAGE_MAX_BYTES).default(TEST_RUN_STORAGE_DEFAULT_MAX_BYTES),
        max_bindings: z.number().int().positive().max(TEST_RUN_STORAGE_MAX_BINDINGS).default(TEST_RUN_STORAGE_DEFAULT_MAX_BINDINGS),
        retention_days: z.number().int().min(1).max(366).default(TEST_RUN_STORAGE_DEFAULT_RETENTION_DAYS),
      }).strict(),
    }).strict().optional(),
    scene_authoring: sceneAuthoringSchema.optional(),
    official_relay: officialRelaySchema.optional(),
    features: featureFlagsSchema.optional(),
  })
  .strict();

export const policyConfigSchema = z
  .object({
    schema_version: z.literal(1),
    defaults: z.record(riskLevelSchema, riskActionSchema),
    trusted_packages: z.array(z.string().min(1)).default([]),
    untrusted_package_default: z.enum(["ask", "deny"]).default("ask"),
    projects: z.record(
      z.string().min(1),
      z
        .object({
          allow: z.array(z.string().min(1)).default([]),
          ask: z.array(z.string().min(1)).default([]),
          deny: z.array(z.string().min(1)).default([]),
        })
        .strict(),
    ),
  })
  .strict();

export type GatewayConfig = z.infer<typeof gatewayConfigSchema>;
type ParsedPolicyConfig = z.infer<typeof policyConfigSchema>;
export type PolicyConfig = Omit<ParsedPolicyConfig, "trusted_packages" | "untrusted_package_default"> & Partial<Pick<ParsedPolicyConfig, "trusted_packages" | "untrusted_package_default">>;

export function loadGatewayConfig(filePath: string, env: NodeJS.ProcessEnv = process.env): GatewayConfig {
  return parseExpandedGatewayConfig(fs.readFileSync(filePath, "utf8"), env);
}

export function parseExpandedGatewayConfig(source: string, env: NodeJS.ProcessEnv = process.env): GatewayConfig {
  return expandGatewayConfig(parseGatewayConfig(source), env);
}

// Shared by startup and the narrow live popup-consent reader. Never accept a
// different duplicate-key/schema interpretation for the same owner config.
export function parseGatewayConfig(source: string): GatewayConfig {
  if (Buffer.byteLength(source, "utf8") > 1024 * 1024) throw new Error("Gateway config exceeds the popup approval settings size limit");
  const parsed = JSON.parse(source) as unknown;
  // JSON.parse silently accepts duplicate keys. Consent must never differ from
  // the strict MCP Window parser, including escaped names and nested objects.
  const members: Set<string>[] = [];
  visit(source, {
    onObjectBegin: () => { members.push(new Set()); },
    onObjectProperty: (name) => {
      const owner = members.at(-1)!;
      if (owner.has(name)) throw new Error("Gateway config contains duplicate JSON member names");
      owner.add(name);
    },
    onObjectEnd: () => { members.pop(); },
  });
  return gatewayConfigSchema.parse(parsed);
}

function expandGatewayConfig(config: GatewayConfig, env: NodeJS.ProcessEnv): GatewayConfig {
  const expanded: GatewayConfig = {
    ...config,
    registry: {
      ...config.registry,
      instances_dir: expandEnvPlaceholders(config.registry.instances_dir, env),
    },
    logging: {
      ...config.logging,
      ...(config.logging.audit_dir ? { audit_dir: expandEnvPlaceholders(config.logging.audit_dir, env) } : {}),
    },
  };

  if (config.cache?.asset_index_root) {
    expanded.cache = {
      ...config.cache,
      asset_index_root: expandEnvPlaceholders(config.cache.asset_index_root, env),
    };
  }

  if (config.approvals) {
    expanded.approvals = {
      ...config.approvals,
      dir: expandEnvPlaceholders(config.approvals.dir, env),
      hmac_key_file: expandEnvPlaceholders(config.approvals.hmac_key_file, env),
    };
  }

  if (config.official_relay) {
    expanded.official_relay = {
      ...config.official_relay,
      ...(config.official_relay.command ? { command: expandEnvPlaceholders(config.official_relay.command, env) } : {}),
      env: Object.fromEntries(Object.entries(config.official_relay.env).map(([key, value]) => [key, expandEnvPlaceholders(value, env)])),
    };
  }

  if (config.skills?.unity_user_skills_root) {
    expanded.skills = {
      ...config.skills,
      unity_user_skills_root: expandEnvPlaceholders(config.skills.unity_user_skills_root, env),
    };
  }

  return expanded;
}

export function loadPolicyConfig(filePath: string): PolicyConfig {
  return policyConfigSchema.parse(readJson(filePath));
}

export function expandEnvPlaceholders(value: string, env: NodeJS.ProcessEnv = process.env): string {
  return value.replaceAll(/%([A-Za-z_][A-Za-z0-9_]*)%/g, (_match, name: string) => env[name] ?? `%${name}%`);
}

function readJson(filePath: string): unknown {
  return JSON.parse(fs.readFileSync(filePath, "utf8"));
}
