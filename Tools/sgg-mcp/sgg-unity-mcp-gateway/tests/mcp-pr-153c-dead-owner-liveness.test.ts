import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it } from "vitest";

import { FakeUnityBridgeClient, type BridgeClient, type BridgeInvokeRequest, type BridgeInvokeResponse, type BridgeTransportOptions } from "../src/bridge/bridge-client.js";
import { dispatchTool, type DispatcherContext } from "../src/mcp/dispatcher.js";
import {
  ProjectRegistry,
  type HeartbeatRecord,
  type RegistryProcessGenerationInspector,
} from "../src/registry/project-registry.js";

const roots: string[] = [];
const now = new Date("2026-08-26T14:00:00.000Z");

afterEach(() => {
  for (const root of roots.splice(0)) fs.rmSync(root, { recursive: true, force: true });
});

describe("MCP-PR-153C dead-owner liveness", () => {
  it("demotes an unchanged cached heartbeat only after exact process-generation death proof", () => {
    const instancesDir = makeTempDir();
    const file = writeHeartbeat(instancesDir, heartbeat());
    const original = fs.readFileSync(file);
    let processState: "live" | "dead" = "live";
    const observed: unknown[] = [];
    const inspector: RegistryProcessGenerationInspector = (identities) => {
      observed.push(identities);
      return identities.map((identity) => ({ ...identity, state: processState }));
    };
    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, now: () => now, processGenerationInspector: inspector });

    const before = registry.captureSnapshot();
    expect(before.records).toMatchObject([{ instance_id: "game-owner", stale: false }]);
    expect(before.resolveProject("game-owner")).toMatchObject({ status: "ok" });

    processState = "dead";
    const after = registry.captureSnapshot();
    expect(after.records).toMatchObject([{ instance_id: "game-owner", stale: true }]);
    expect(after.resolveProject("game-owner")).toMatchObject({ status: "error", code: "project_stale" });
    expect(after.getStats()).toMatchObject({ projectRecordsCount: 1, liveProjectRecordsCount: 0, staleProjectRecordsCount: 1 });
    expect(before.records).toMatchObject([{ instance_id: "game-owner", stale: false }]);
    expect(registry.queryStaleProjects()).toMatchObject({ total_available: 1, projects: [{ instance_id: "game-owner", stale: true }] });
    expect(fs.readFileSync(file)).toEqual(original);
    expect(observed[0]).toEqual([{
      instance_id: "game-owner",
      unity_pid: 12345,
      unity_process_start_utc: "2026-08-26T13:00:00.1234000Z",
      heartbeat_generation: 7,
      last_heartbeat: "2026-08-26T13:59:50.000Z",
    }]);
    expect(after.getStats().heartbeatCacheHitCount).toBe(1);
  });

  it("keeps incomplete, failed, uncertain, and duplicate process evidence fail-closed", () => {
    const cases: Array<{ name: string; record: HeartbeatRecord; inspector: RegistryProcessGenerationInspector; expectedCalls: number }> = [
      {
        name: "missing generation",
        record: heartbeat({ heartbeat_generation: undefined }),
        inspector: () => { throw new Error("must not inspect incomplete identity"); },
        expectedCalls: 0,
      },
      {
        name: "scan failure",
        record: heartbeat(),
        inspector: () => { throw new Error("scan failed"); },
        expectedCalls: 1,
      },
      {
        name: "uncertain scan",
        record: heartbeat(),
        inspector: (identities) => identities.map((identity) => ({ ...identity, state: "uncertain" })),
        expectedCalls: 1,
      },
      {
        name: "duplicate observations",
        record: heartbeat(),
        inspector: (identities) => identities.flatMap((identity) => [{ ...identity, state: "dead" as const }, { ...identity, state: "dead" as const }]),
        expectedCalls: 1,
      },
    ];

    for (const entry of cases) {
      const instancesDir = makeTempDir();
      writeHeartbeat(instancesDir, entry.record);
      let calls = 0;
      const registry = new ProjectRegistry({
        instancesDir,
        staleAfterMs: 90_000,
        now: () => now,
        processGenerationInspector: (identities) => { calls += 1; return entry.inspector(identities); },
      });

      expect(registry.resolveProject("game-owner"), entry.name).toMatchObject({ status: "ok" });
      expect(registry.listProjectRecords(), entry.name).toMatchObject([{ stale: false }]);
      expect(calls, entry.name).toBe(entry.expectedCalls * 2);
    }
  });

  it("excludes a proven-dead owner from discovery, resolves one live replacement, and never dispatches an exact dead selector", async () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat());
    writeHeartbeat(instancesDir, heartbeat({
      instance_id: "game-replacement",
      unity_pid: 54321,
      unity_process_start_utc: "2026-08-26T13:30:00.0000000Z",
      heartbeat_generation: 3,
      bridge_port: 18143,
    }));
    const registry = new ProjectRegistry({
      instancesDir,
      staleAfterMs: 90_000,
      now: () => now,
      processGenerationInspector: (identities) => identities.map((identity) => ({
        ...identity,
        state: identity.instance_id === "game-owner" ? "dead" : "live",
      })),
    });
    const bridge = new TrackingBridge();
    const context: DispatcherContext = { registry, bridge, now: () => now };

    const projects = await dispatchTool("sgg_unity_projects", {}, context);
    expect(projects).toMatchObject({
      status: "ok",
      data: {
        projects: [{ instance_id: "game-replacement", stale: false }],
        counts: { total: 2, live: 1, stale: 1, unique_projects: 1 },
      },
    });

    const exactDead = await dispatchTool("sgg_unity_call", {
      project: "game-owner",
      command: "editor.quit",
      args_json: "{}",
      request_id: "pr-153c-exact-dead",
    }, context);
    expect(exactDead).toMatchObject({ status: "error", error: { code: "project_stale" } });
    expect(bridge.describeCount).toBe(0);
    expect(bridge.invokeCount).toBe(0);

    const explicitStableRead = await dispatchTool("sgg_unity_call", {
      project: "game",
      command: "compile.status",
      args_json: "{}",
      request_id: "pr-153c-stable-live",
    }, context);
    expect(explicitStableRead).toMatchObject({ status: "ok", project: "game" });
    expect(bridge.describeCount).toBe(1);
    expect(bridge.invokeCount).toBe(1);
  });
});

class TrackingBridge implements BridgeClient {
  readonly inner = new FakeUnityBridgeClient();
  describeCount = 0;
  invokeCount = 0;

  describe(record: HeartbeatRecord, selector: string, transport?: BridgeTransportOptions): Promise<unknown> {
    this.describeCount += 1;
    return this.inner.describe(record, selector);
  }

  invoke(record: HeartbeatRecord, request: BridgeInvokeRequest, transport?: BridgeTransportOptions): Promise<BridgeInvokeResponse> {
    this.invokeCount += 1;
    return this.inner.invoke(record, request, transport);
  }

  logs(record: HeartbeatRecord, kind: "console" | "bridge" | "jobs", limit: number, transport?: BridgeTransportOptions): Promise<unknown> {
    return this.inner.logs(record, kind, limit);
  }
}

function makeTempDir(): string {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-pr-153c-"));
  roots.push(root);
  return root;
}

function writeHeartbeat(instancesDir: string, record: HeartbeatRecord): string {
  const file = path.join(instancesDir, `${record.instance_id}.json`);
  fs.writeFileSync(file, JSON.stringify(record, null, 2));
  return file;
}

function heartbeat(overrides: Partial<HeartbeatRecord> = {}): HeartbeatRecord {
  return {
    schema_version: 1,
    instance_id: "game-owner",
    project: "game",
    display_name: "Game",
    package_version: "2026.8.26-4",
    project_root: "C:/u/game",
    canonical_project_root: "c:/u/game",
    aliases: [{ environment: "windows", host_path: "C:\\u\\game", canonical_path: "c:/u/game" }],
    unity_version: "6000.5.9f1",
    unity_pid: 12345,
    unity_process_start_utc: "2026-08-26T13:00:00.1234000Z",
    bridge_host: "127.0.0.1",
    bridge_port: 18142,
    token: "local-token",
    status: "ready",
    commands_hash: "catalog-v4",
    catalog_status: "ready",
    catalog_generation: 1,
    heartbeat_generation: 7,
    last_heartbeat: "2026-08-26T13:59:50.000Z",
    ...overrides,
  };
}
