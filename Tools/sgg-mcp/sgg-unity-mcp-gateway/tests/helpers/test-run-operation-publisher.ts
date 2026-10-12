import type { TestRunOperationPublisher } from "../../src/operations/test-run-operation-publisher.js";
import { TestRunOperationStoreError, testRunCanonicalProjectRootHash, type TestRunOperationStoreLike, type TestRunOperationBinding } from "../../src/operations/test-run-operation-store.js";
import { CanonicalPathService } from "../../src/paths/canonical-path.js";

// Explicit fixture seam only. Production never falls back to synchronous storage.
// Preserve injected clocks/faults and the existing store's exact pre-commit hook.
export function inlineTestRunOperationPublisher(getStore: () => TestRunOperationStoreLike): TestRunOperationPublisher {
  return {
    get dir() { return getStore().dir; },
    async prepare(binding, options) {
      const store = getStore();
      store.publish(binding, { beforeCommit: options.beforeCommit });
      let issued = false;
      let terminal = false;
      let released = false;
      return {
        async publishRequestIssuance(issuedAt) {
          if (released || issued) throw new TestRunOperationStoreError("issuance_invalid");
          issued = true;
          return store.publishRequestIssuance(binding, issuedAt);
        },
        async publishTerminalNoExecution(marker, evidence) {
          if (released || !issued || terminal) throw new TestRunOperationStoreError("terminal_outcome_invalid");
          terminal = true;
          return store.publishTerminalNoExecution(binding, marker, evidence);
        },
        release() { released = true; },
      };
    },
    snapshotStatus() { return getStore().snapshotStatus(); },
    async close() {},
  };
}

export function ownedTestRunOperationBinding(root: string): TestRunOperationBinding {
  const created = new Date();
  const stamp = created.toISOString().replace(/[-:]/g, "");
  const operationId = `test-${stamp.slice(0, 8)}-${stamp.slice(9, 15)}-1234abcd`;
  const canonicalRoot = new CanonicalPathService().normalizeProjectRoot(root);
  return {
    schema_version: 1, contract: "sgg.unity_mcp.test_run_operation_owner.v1", command: "test.run", operation_id: operationId,
    project: "game", original_project_selector: "game", origin_project_root: root, canonical_project_root: canonicalRoot,
    canonical_project_root_hash: testRunCanonicalProjectRootHash(canonicalRoot), origin_instance_id: "game-a", origin_unity_pid: process.pid,
    origin_unity_process_start_utc: new Date(created.getTime() - 1000).toISOString(), caller_args_hash: `sha256:${"a".repeat(64)}`,
    artifact_path: `Library/SGGUnityMcp/job-artifacts/test-runs/${operationId}.json`, artifact_schema_version: 3,
    package_id: "com.sungeargames.unity-mcp", package_version: "2026.10.10-5", commands_hash: "hash", catalog_generation: 1,
    artifact_schema_capability: "test_run_artifact_v3", created_at: created.toISOString(),
  };
}
