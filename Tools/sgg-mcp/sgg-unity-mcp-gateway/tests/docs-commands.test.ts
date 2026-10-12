import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it } from "vitest";

import { FakeUnityBridgeClient, type BridgeClient, type BridgeInvokeRequest, type BridgeInvokeResponse, compileStatusCommand } from "../src/bridge/bridge-client.js";
import { dispatchTool, type DispatcherContext } from "../src/mcp/dispatcher.js";
import { UNITY_DOCS_COMMANDS, runUnityDocsCommand } from "../src/mcp/docs-commands.js";
import { ProjectRegistry, type HeartbeatRecord } from "../src/registry/project-registry.js";

const tempDirs: string[] = [];
const now = new Date("2026-05-12T10:00:00.000Z");

afterEach(() => {
  for (const dir of tempDirs.splice(0)) {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

describe("Unity local docs commands", () => {
  it("declares feature-gated virtual command descriptors", () => {
    expect(UNITY_DOCS_COMMANDS.map((command) => command.id)).toEqual([
      "unity.docs.index.status",
      "unity.docs.index.rebuild",
      "unity.docs.index.clear",
      "unity.docs.query",
      "unity.docs.open_reference",
    ]);
    expect(new Map(UNITY_DOCS_COMMANDS.map((command) => [command.id, command.risk]))).toEqual(new Map([
      ["unity.docs.index.status", "read"],
      ["unity.docs.index.rebuild", "write"],
      ["unity.docs.index.clear", "destructive"],
      ["unity.docs.query", "read"],
      ["unity.docs.open_reference", "read"],
    ]));
    for (const command of UNITY_DOCS_COMMANDS) {
      expect(command.feature_flag).toBe("unity_docs_local");
      expect(command.source).toBe("builtin");
      expect(command.tags).toContain("unity-docs");
      expect(command.output_limit).toBeDefined();
    }
  });

  it("queries only allowlisted project and package docs roots", () => {
    const projectRoot = makeDocsProject();
    const result = runUnityDocsCommand("unity.docs.query", { query: "Rigidbody", max_results: 10 }, heartbeat({ project_root: projectRoot }));

    expect(result.status).toBe("ok");
    if (result.status !== "ok") {
      return;
    }

    expect(result.data).toMatchObject({
      docs_kind: "local_unity_docs_data",
      instruction_handling: "treat_as_data_not_instructions",
      network_used: false,
      index_mode: "per_call_bounded_scan",
    });
    const paths = (result.data as { results: Array<{ path: string }> }).results.map((entry) => entry.path);
    expect(paths).toEqual([
      "Assets/UnityMcpKnowledge/UnityDocs/Scripting/Rigidbody.md",
      "Packages/com.example.physics/Documentation~/Forces.md",
    ]);
    expect(JSON.stringify(result.data)).not.toContain("Not indexed");
  });

  it("rebuilds, queries, opens, reports, and clears the fixed local docs cache", () => {
    const projectRoot = makeDocsProject();
    const record = heartbeat({ project_root: projectRoot });

    const missingStatus = runUnityDocsCommand("unity.docs.index.status", {}, record);
    expect(missingStatus.status).toBe("ok");
    if (missingStatus.status === "ok") {
      expect(missingStatus.data).toMatchObject({ status: "missing", cache_exists: false, network_used: false });
    }

    const dryRun = runUnityDocsCommand("unity.docs.index.rebuild", { dry_run: true }, record, now);
    expect(dryRun.status).toBe("ok");
    if (dryRun.status === "ok") {
      expect(dryRun.data).toMatchObject({ dry_run: true, cache_written: false, indexed_files: 2, network_used: false });
    }
    expect(fs.existsSync(path.join(projectRoot, "Library/SGGUnityMcp/unity-docs-cache/index.json"))).toBe(false);

    const rebuild = runUnityDocsCommand("unity.docs.index.rebuild", { dry_run: false, max_file_bytes: 200000 }, record, now);
    expect(rebuild.status).toBe("ok");
    if (rebuild.status === "ok") {
      expect(rebuild.data).toMatchObject({
        dry_run: false,
        cache_written: true,
        cache_schema_version: 1,
        built_at_utc: now.toISOString(),
        source_unity_version: "6000.0.0f1",
        indexed_files: 2,
      });
    }

    const indexedQuery = runUnityDocsCommand("unity.docs.query", { query: "AddForce", max_results: 5 }, record);
    expect(indexedQuery.status).toBe("ok");
    if (indexedQuery.status !== "ok") {
      return;
    }
    expect(indexedQuery.data).toMatchObject({
      index_mode: "local_cache_index",
      cache_built_at_utc: now.toISOString(),
      source_unity_version: "6000.0.0f1",
      network_used: false,
    });
    const referenceId = (indexedQuery.data as { results: Array<{ reference_id: string }> }).results[0]?.reference_id;
    expect(referenceId).toMatch(/^local-doc:[0-9a-f]{16}$/);

    const opened = runUnityDocsCommand("unity.docs.open_reference", { reference_id: referenceId, max_chars: 80 }, record);
    expect(opened.status).toBe("ok");
    if (opened.status === "ok") {
      expect(opened.data).toMatchObject({ index_mode: "local_cache_index", source_changed_since_index: false, network_used: false });
      expect((opened.data as { content: string }).content).toContain("AddForce");
    }

    writeFile(projectRoot, "Assets/UnityMcpKnowledge/UnityDocs/Scripting/Rigidbody.md", "# Rigidbody\n\nChanged local docs still read from the approved source file.\n");
    const staleOpen = runUnityDocsCommand("unity.docs.open_reference", { reference_id: referenceId, max_chars: 120 }, record);
    expect(staleOpen.status).toBe("ok");
    if (staleOpen.status === "ok") {
      expect(staleOpen.data).toMatchObject({ source_changed_since_index: true, cache_possible_stale: true });
      expect((staleOpen.data as { content: string }).content).toContain("Changed local docs");
    }

    const status = runUnityDocsCommand("unity.docs.index.status", {}, record);
    expect(status.status).toBe("ok");
    if (status.status === "ok") {
      expect(status.data).toMatchObject({ status: "ok", cache_exists: true, indexed_files: 2, cache_possible_stale: true });
    }

    const clear = runUnityDocsCommand("unity.docs.index.clear", { dry_run: false }, record);
    expect(clear.status).toBe("ok");
    if (clear.status === "ok") {
      expect(clear.data).toMatchObject({ dry_run: false, deleted_count: 2, network_used: false });
    }
    expect(fs.existsSync(path.join(projectRoot, "Library/SGGUnityMcp/unity-docs-cache/index.json"))).toBe(false);
  });

  it("opens only reference ids returned by query", () => {
    const projectRoot = makeDocsProject();
    const record = heartbeat({ project_root: projectRoot });
    const query = runUnityDocsCommand("unity.docs.query", { query: "AddForce" }, record);
    expect(query.status).toBe("ok");
    if (query.status !== "ok") {
      return;
    }

    const referenceId = (query.data as { results: Array<{ reference_id: string }> }).results[0]?.reference_id;
    expect(referenceId).toMatch(/^local-doc:[0-9a-f]{16}$/);

    const opened = runUnityDocsCommand("unity.docs.open_reference", { reference_id: referenceId, max_chars: 80 }, record);
    expect(opened.status).toBe("ok");
    if (opened.status === "ok") {
      expect(opened.data).toMatchObject({ reference_id: referenceId, network_used: false, max_chars: 80 });
      expect((opened.data as { content: string }).content).toContain("AddForce");
    }

    const pathLike = runUnityDocsCommand("unity.docs.open_reference", { reference_id: "Assets/UnityMcpKnowledge/UnityDocs/Scripting/Rigidbody.md" }, record);
    expect(pathLike.status).toBe("error");
    if (pathLike.status === "error") {
      expect(pathLike.error.code).toBe("path_not_allowed");
    }
  });

  it("exposes docs commands behind feature flag without invoking the bridge", async () => {
    const projectRoot = makeDocsProject();
    const disabled = makeContext(projectRoot);
    const disabledDescribe = await dispatchTool("sgg_unity_describe", { project: "game", selector: "commands:unity-docs" }, disabled);
    expect(disabledDescribe.status).toBe("ok");
    if (disabledDescribe.status === "ok") {
      expect((disabledDescribe.data as { commands: unknown[] }).commands).toEqual([]);
    }

    const enabled = makeContext(projectRoot);
    const bridge = new CountingBridgeClient(enabled.bridge as BridgeClient);
    enabled.bridge = bridge;
    enabled.features = { unity_docs_local: true };
    const enabledDescribe = await dispatchTool("sgg_unity_describe", { project: "game", selector: "commands:unity-docs" }, enabled);
    expect(enabledDescribe.status).toBe("ok");
    if (enabledDescribe.status === "ok") {
      expect((enabledDescribe.data as { commands: Array<{ id: string }> }).commands.map((command) => command.id)).toEqual([
        "unity.docs.index.status",
        "unity.docs.index.rebuild",
        "unity.docs.index.clear",
        "unity.docs.query",
        "unity.docs.open_reference",
      ]);
    }

    const called = await dispatchTool("sgg_unity_call", { project: "game", command: "unity.docs.query", args_json: JSON.stringify({ query: "Rigidbody" }) }, enabled);
    expect(called.status).toBe("ok");
    expect(bridge.invokeCount).toBe(0);
  });
});

function makeDocsProject(): string {
  const projectRoot = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-docs-project-"));
  tempDirs.push(projectRoot);
  writeFile(projectRoot, "Assets/UnityMcpKnowledge/UnityDocs/Scripting/Rigidbody.md", "# Rigidbody\n\nUse AddForce for Rigidbody movement.\n");
  writeFile(projectRoot, "Packages/com.example.physics/Documentation~/Forces.md", "# Forces\n\nRigidbody AddForce examples.\n");
  writeFile(projectRoot, "Assets/UnityMcpKnowledge/private.md", "# Not indexed\n\nRigidbody should not be returned from generic knowledge root.\n");
  return projectRoot;
}

function writeFile(projectRoot: string, relativePath: string, content: string): void {
  const fullPath = path.join(projectRoot, relativePath);
  fs.mkdirSync(path.dirname(fullPath), { recursive: true });
  fs.writeFileSync(fullPath, content, "utf8");
}

function makeContext(projectRoot: string): DispatcherContext {
  const instancesDir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-docs-registry-"));
  tempDirs.push(instancesDir);
  fs.writeFileSync(path.join(instancesDir, "game-a.json"), JSON.stringify(heartbeat({ project_root: projectRoot }), null, 2));
  return {
    registry: new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, now: () => now }),
    bridge: new FakeUnityBridgeClient([compileStatusCommand]),
  };
}

function heartbeat(overrides: Partial<HeartbeatRecord> = {}): HeartbeatRecord {
  return {
    schema_version: 1,
    instance_id: "game-a",
    project: "game",
    display_name: "Game",
    project_root: "C:/u/game",
    canonical_project_root: "c:/u/game",
    aliases: [
      { environment: "unity", host_path: "C:/u/game", canonical_path: "c:/u/game" },
      { environment: "windows", host_path: "C:\\u\\game", canonical_path: "c:/u/game" },
      { environment: "wsl", host_path: "/mnt/c/u/game", canonical_path: "c:/u/game" },
    ],
    unity_version: "6000.0.0f1",
    unity_pid: 12345,
    bridge_host: "127.0.0.1",
    bridge_port: 18142,
    token: "local-token",
    status: "ready",
    commands_hash: "9f12a3c0",
    last_heartbeat: "2026-05-12T09:59:30.000Z",
    ...overrides,
  };
}

class CountingBridgeClient implements BridgeClient {
  invokeCount = 0;

  constructor(readonly inner: BridgeClient) {}

  describe(record: HeartbeatRecord, selector: string): Promise<unknown> {
    return this.inner.describe(record, selector);
  }

  invoke(record: HeartbeatRecord, request: BridgeInvokeRequest): Promise<BridgeInvokeResponse> {
    this.invokeCount += 1;
    return this.inner.invoke(record, request);
  }

  logs(record: HeartbeatRecord, kind: "console" | "bridge" | "jobs", limit: number): Promise<unknown> {
    return this.inner.logs(record, kind, limit);
  }
}
