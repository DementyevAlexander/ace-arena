import { randomUUID } from "node:crypto";
import path from "node:path";

import { ApprovalStore } from "../approvals/approval-store.js";
import { ApprovalBindingService } from "../approvals/approval-binding.js";
import { FileApprovalKeyManager } from "../approvals/approval-key-manager.js";
import { KnownUnityPopupSettingsManager } from "../approvals/known-unity-popup-settings.js";
import { HttpUnityBridgeClient, type BridgeClient } from "../bridge/bridge-client.js";
import type { GatewayConfig, PolicyConfig } from "../config/config.js";
import { createUnityWindowDiagnosticsRuntime } from "../diagnostics/unity-window-diagnostics.js";
import { createUnityProcessGenerationInspector, createUnityProcessRuntime } from "../diagnostics/unity-processes.js";
import { InMemoryJobManager } from "../jobs/job-manager.js";
import type { DispatcherContext } from "../mcp/dispatcher.js";
import { OfficialRelayAdapter } from "../mcp/official-relay-adapter.js";
import { FileAuditSink, InMemoryAuditSink, type AuditSink } from "../observability/audit.js";
import type { OperationalLogger } from "../observability/operational-logger.js";
import { TestRunOperationStore, resolveTestRunOperationStoreLimits, testRunOperationStoreDir, type TestRunOperationStoreLike } from "../operations/test-run-operation-store.js";
import { TestRunOperationStorePublisher, type TestRunOperationPublisher } from "../operations/test-run-operation-publisher.js";
import { testRunStorageOptionsFromConfig } from "../operations/test-run-storage-settings.js";
import type { PolicyManager } from "../policy/policy-manager.js";
import { ProjectRegistry, type RegistryProcessGenerationInspector } from "../registry/project-registry.js";
import { ProjectQueueManager } from "../routing/project-queue.js";

export type GatewayRuntimeOptions = {
  bridge?: BridgeClient;
  audit?: AuditSink;
  approvalBindingService?: ApprovalBindingService;
  testRunOperationStore?: TestRunOperationStoreLike;
  testRunOperationPublisher?: TestRunOperationPublisher;
  testRunOperationWorkerEntry?: URL;
  configPath?: string;
  policyPath?: string;
  policyManager?: PolicyManager;
  mcpTokenPresent?: boolean;
  now?: () => Date;
  runId?: string;
  gatewayRuntimeVersion?: string;
  operationalLogger?: OperationalLogger;
  registryProcessGenerationInspector?: RegistryProcessGenerationInspector;
};

export function createGatewayContext(config: GatewayConfig, policy?: PolicyConfig, options: GatewayRuntimeOptions = {}): DispatcherContext {
  const unityWindowDiagnostics = createUnityWindowDiagnosticsRuntime(options.now ? { now: options.now } : {});
  const registryProcessGenerationInspector = options.registryProcessGenerationInspector ?? createUnityProcessGenerationInspector(options.now ? { now: options.now } : {});
  const operationStoreLimits = resolveTestRunOperationStoreLimits(testRunStorageOptionsFromConfig(config));
  const operationStore = options.testRunOperationStore ?? new TestRunOperationStore(testRunOperationStoreDir(config.registry.instances_dir), {
    ...operationStoreLimits,
    ...(options.now ? { now: options.now } : {}),
  });
  // Custom read stores require an explicit matching publisher. Never fall back
  // to synchronous mutations or silently substitute different configured limits.
  const operationPublisher = options.testRunOperationPublisher ?? (options.testRunOperationStore === undefined
    ? new TestRunOperationStorePublisher({ storeDir: operationStore.dir, limits: operationStoreLimits, ...(options.testRunOperationWorkerEntry ? { workerEntry: options.testRunOperationWorkerEntry } : {}) })
    : undefined);
  const context: DispatcherContext = {
    registry: new ProjectRegistry({
      instancesDir: config.registry.instances_dir,
      staleAfterMs: config.registry.stale_after_ms,
      ...(config.registry.cleanup_after_ms ? { cleanupAfterMs: config.registry.cleanup_after_ms } : {}),
      cacheMaxEntries: config.registry.cache_max_entries,
      cacheTtlMs: config.registry.cache_ttl_ms,
      invalidDiagnosticsMax: config.registry.invalid_diagnostics_max,
      scanMaxFiles: config.registry.scan_max_files,
      processGenerationInspector: registryProcessGenerationInspector,
      ...(options.now ? { now: options.now } : {}),
      ...(options.operationalLogger ? { logger: options.operationalLogger } : {}),
    }),
    bridge: options.bridge ?? new HttpUnityBridgeClient(options.operationalLogger),
    queues: new ProjectQueueManager(),
    jobs: new InMemoryJobManager(options.now),
    testRunOperationStore: operationStore,
    ...(operationPublisher ? { testRunOperationPublisher: operationPublisher } : {}),
    unityWindowDiagnostics,
    unityProcessDiagnostics: createUnityProcessRuntime(),
    features: config.features ?? {},
    instancesDir: config.registry.instances_dir,
    auditEnabled: config.logging.audit_enabled,
    ...(options.operationalLogger ? { operationalLogger: options.operationalLogger } : {}),
    ...(config.logging.audit_dir ? { auditDir: config.logging.audit_dir, auditFilePath: path.join(config.logging.audit_dir, "audit.ndjson") } : {}),
    ...(config.cache?.asset_index_root ? { assetIndexCacheRoot: config.cache.asset_index_root } : {}),
    ...(config.scene_authoring?.mono_behaviour_allowlist?.length ? { monoBehaviourAllowlist: config.scene_authoring.mono_behaviour_allowlist } : {}),
    ...(config.official_relay?.enabled ? { officialRelay: new OfficialRelayAdapter(config.official_relay) } : {}),
    ...(config.skills?.unity_user_skills_root ? { skillUnityUserSkillsRoot: config.skills.unity_user_skills_root } : {}),
    ...(options.configPath ? { configPath: options.configPath, knownUnityPopupSettings: new KnownUnityPopupSettingsManager(options.configPath) } : {}),
    ...(options.policyPath ? { policyPath: options.policyPath } : {}),
    ...(options.policyManager ? { policyManager: options.policyManager } : {}),
    ...(typeof options.mcpTokenPresent === "boolean" ? { mcpTokenPresent: options.mcpTokenPresent } : {}),
    ...(options.gatewayRuntimeVersion ? { gatewayRuntimeVersion: options.gatewayRuntimeVersion } : {}),
    ...(config.approvals ? {
      approvalStore: new ApprovalStore(config.approvals.dir, {
        ...(config.approvals.retention ? {
          maxRecords: config.approvals.retention.max_records,
          maxBytes: config.approvals.retention.max_bytes,
          maxQuarantineFiles: config.approvals.retention.quarantine_max_files,
          maxQuarantineBytes: config.approvals.retention.quarantine_max_bytes,
          quarantineMaxAgeMs: config.approvals.retention.quarantine_max_age_ms,
          claimTtlMs: config.approvals.retention.claim_ttl_ms,
        } : {}),
        ...(options.now ? { now: options.now } : {}),
        ...(options.operationalLogger ? { logger: options.operationalLogger } : {}),
      }),
      approvalBindingService: options.approvalBindingService ?? new ApprovalBindingService(new FileApprovalKeyManager(config.approvals.hmac_key_file)),
      approvalDefaultTtlMs: config.approvals.default_ttl_ms,
      approvalAutoAllowAsk: config.approvals.auto_allow_ask,
      approvalAutoAllowKnownUnityPopups: config.approvals.auto_allow_known_unity_popups,
      approvalAutoAllowStaleCleanupWithoutElicitation: config.approvals.auto_allow_stale_registry_cleanup_without_elicitation,
    } : {}),
  };

  if (policy) {
    context.policy = policy;
  }

  if (config.logging.audit_enabled) {
    context.audit = options.audit ?? (config.logging.audit_dir ? new FileAuditSink(config.logging.audit_dir, {
      ...(config.logging.audit_retention ? {
        maxFileBytes: config.logging.audit_retention.max_file_bytes,
        maxFiles: config.logging.audit_retention.max_files,
        maxAgeMs: config.logging.audit_retention.max_age_ms,
        maxTotalBytes: config.logging.audit_retention.max_total_bytes,
      } : {}),
      ...(options.now ? { now: options.now } : {}),
      ...(options.operationalLogger ? { logger: options.operationalLogger } : {}),
    }) : new InMemoryAuditSink());
  }

  context.runId = options.runId ?? randomUUID();

  if (options.now) {
    context.now = options.now;
  }

  return context;
}
