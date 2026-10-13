import fs, { type BigIntStats } from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it } from "vitest";

import { BRIDGE_PROTOCOL_BOUNDS } from "../src/bridge/bridge-protocol.js";
import { ProjectRegistry, projectRegistrySnapshotForResolution, publicRegistryOperationalStatus, type HeartbeatRecord, type ProjectRegistryOptions } from "../src/registry/project-registry.js";

const tempDirs: string[] = [];
const now = new Date("2026-05-12T10:00:00.000Z");

afterEach(() => {
  for (const dir of tempDirs.splice(0)) {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

describe("ProjectRegistry", () => {
  it("returns an empty list when the heartbeat directory does not exist", () => {
    const registry = new ProjectRegistry({ instancesDir: path.join(os.tmpdir(), "missing-sgg-registry"), staleAfterMs: 90000, now: () => now });
    expect(registry.listProjects()).toEqual([]);
    expect(registry.resolveProject("missing")).toEqual({
      status: "error",
      code: "unsafe_binding_refused",
      matches: [],
      registryOperationalState: "unavailable",
    });
  });

  it("returns project_not_found only after an operational empty scan", () => {
    const registry = new ProjectRegistry({ instancesDir: makeTempDir(), staleAfterMs: 90_000, now: () => now });

    expect(registry.resolveProject("missing")).toEqual({ status: "error", code: "project_not_found", matches: [] });
  });

  it("reads heartbeat files and redacts bridge tokens from public records", () => {
    const instancesDir = makeTempDir();
    const { canonical_project_root: _canonicalProjectRoot, ...record } = heartbeat({
      project_root: "/mnt/c/u/game",
      canonical_project_root: "wrong-forward-value",
      token: "secret-token",
    });
    writeHeartbeat(instancesDir, record);

    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90000, now: () => now });
    const projects = registry.listProjects();

    expect(projects).toHaveLength(1);
    expect(projects[0]).toMatchObject({
      project: "game",
      canonical_project_root: "c:/u/game",
      aliases: [
        { environment: "wsl", host_path: "/mnt/c/u/game", canonical_path: "c:/u/game" },
        { environment: "unity", host_path: "C:/u/game", canonical_path: "c:/u/game" },
        { environment: "windows", host_path: "C:\\u\\game", canonical_path: "c:/u/game" },
      ],
      stale: false,
      bridge_port: 18142,
    });
    expect(projects[0]).not.toHaveProperty("token");
  });

  it("overrides forwarded canonical project roots with locally canonicalized roots", () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat({ project_root: "/mnt/c/u/game", canonical_project_root: "wrong-forward-value" }));

    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90000, now: () => now });
    const projects = registry.listProjects();

    expect(projects).toHaveLength(1);
    expect(projects[0]).toMatchObject({ project_root: "/mnt/c/u/game", canonical_project_root: "c:/u/game" });
  });

  it("returns project_stale when only stale heartbeats match", () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat({ last_heartbeat: "2026-05-12T09:00:00.000Z" }));

    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90000, now: () => now });
    expect(registry.resolveProject("game")).toMatchObject({ status: "error", code: "project_stale" });
  });

  it("can resolve a single stale heartbeat when explicitly requested for rescue commands", () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat({ last_heartbeat: "2026-05-12T09:00:00.000Z" }));

    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90000, now: () => now });
    expect(registry.resolveProject("game", { allowStale: true })).toMatchObject({ status: "ok", record: { project: "game" } });
  });

  it("returns duplicate_project_id instead of guessing between live matches", () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "game-a" }));
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "game-b", unity_pid: 22222, bridge_port: 18143 }));

    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90000, now: () => now });
    const result = registry.resolveProject("game");

    expect(result).toMatchObject({ status: "error", code: "duplicate_project_id" });
    if (result.status === "error") {
      expect(result.matches).toHaveLength(2);
      expect(result.matches[0]).not.toHaveProperty("token");
    }
  });

  it("preserves conflicting per-file records with identical routing identity and redacts their secrets", () => {
    const instancesDir = makeTempDir();
    fs.writeFileSync(path.join(instancesDir, "a.json"), JSON.stringify(heartbeat({ token: "private-token-a", bridge_port: 18142, unity_pid: 11111, status: "ready" })));
    fs.writeFileSync(path.join(instancesDir, "b.json"), JSON.stringify(heartbeat({ token: "private-token-b", bridge_port: 18143, unity_pid: 22222, status: "compiling" })));
    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, now: () => now });

    const records = registry.listProjectRecords();
    expect(records).toHaveLength(2);
    expect(records.map(({ bridge_port, unity_pid, status }) => ({ bridge_port, unity_pid, status }))).toEqual([
      { bridge_port: 18142, unity_pid: 11111, status: "ready" },
      { bridge_port: 18143, unity_pid: 22222, status: "compiling" },
    ]);
    const resolution = registry.resolveProject("game");
    expect(resolution).toMatchObject({ status: "error", code: "duplicate_project_id", matches: [{ instance_id: "game-a" }, { instance_id: "game-a" }] });
    expect(registry.getStats()).toMatchObject({ projectRecordsCount: 2, heartbeatCacheEntriesCount: 2, heartbeatCacheHitCount: 2 });
    expect(JSON.stringify({ records, resolution })).not.toMatch(/private-token-a|private-token-b|"token"/);
  });

  it("resolves a single live project", () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat({ project: "tools", instance_id: "tools-a" }));

    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90000, now: () => now });
    expect(registry.resolveProject("tools")).toMatchObject({ status: "ok", record: { project: "tools", instance_id: "tools-a" } });
    expect(registry.resolveProject("missing")).toEqual({ status: "error", code: "project_not_found", matches: [] });
  });

  it("lists and resolves updating heartbeats as live projects", () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat({ status: "updating" }));

    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90000, now: () => now });

    expect(registry.listProjects()).toMatchObject([{ project: "game", status: "updating", stale: false }]);
    expect(registry.resolveProject("game")).toMatchObject({ status: "ok", record: { project: "game", status: "updating" } });
  });

  it("accepts waiting_for_idle heartbeats and preserves package version", () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat({ status: "waiting_for_idle", package_version: "2026.6.5-5", bridge_port: 0 }));

    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90000, now: () => now });

    expect(registry.listProjects()).toMatchObject([{ project: "game", status: "waiting_for_idle", package_version: "2026.6.5-5", bridge_port: 0, stale: false }]);
    expect(registry.resolveProject("game")).toMatchObject({ status: "ok", record: { project: "game", status: "waiting_for_idle", package_version: "2026.6.5-5", bridge_port: 0 } });
  });

  it("preserves optional dirty scene heartbeat status", () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat({ has_unsaved_scene_changes: true, dirty_scene_count: 1, dirty_scenes: [{ scene_path: "Assets/Main.unity", scene_name: "Main", is_active: true }] }));

    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90000, now: () => now });

    expect(registry.listProjects()).toMatchObject([{ has_unsaved_scene_changes: true, dirty_scene_count: 1, dirty_scenes: [{ scene_path: "Assets/Main.unity", scene_name: "Main", is_active: true }] }]);
  });

  it("resolves projects by canonical root, aliases, display name and instance id", () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "game-a", project_root: "/mnt/c/u/game" }));

    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90000, now: () => now });

    expect(registry.resolveProject("c:/u/game")).toMatchObject({ status: "ok", record: { instance_id: "game-a" } });
    expect(registry.resolveProject("C:\\u\\game")).toMatchObject({ status: "ok", record: { instance_id: "game-a" } });
    expect(registry.resolveProject("/mnt/c/u/game")).toMatchObject({ status: "ok", record: { instance_id: "game-a" } });
    expect(registry.resolveProject("Game")).toMatchObject({ status: "ok", record: { instance_id: "game-a" } });
    expect(registry.resolveProject("game-a")).toMatchObject({ status: "ok", record: { instance_id: "game-a" } });
  });

  it("retains bounded exact-instance identity only to expose a unique replacement", () => {
    const instancesDir = makeTempDir();
    let current = now;
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "game-a" }));
    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, cacheTtlMs: 1_000, now: () => current });

    expect(registry.resolveProject("game-a")).toMatchObject({ status: "ok" });
    fs.rmSync(path.join(instancesDir, "game-a.json"));
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "game-b", bridge_port: 18143 }));
    current = new Date(now.getTime() + 500);

    expect(registry.resolveProject("game-a")).toMatchObject({ status: "error", code: "project_not_found" });
    const replacement = registry.resolveRecentReplacementProject("game-a");
    expect(replacement).toMatchObject({
      status: "ok",
      failedRecord: { instance_id: "game-a" },
      replacementRecord: { instance_id: "game-b" },
    });
    expect(JSON.stringify(replacement)).not.toMatch(/token|canonical_project_root|project_root|aliases|local-token/i);

    current = new Date(now.getTime() + 1_501);
    expect(registry.resolveRecentReplacementProject("game-a")).toEqual({ status: "error", code: "project_not_found" });
  });

  it("keeps persisted dot-segment selectors resolving to the cleaned heartbeat identity", () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "game-a", project_root: "C:/u/other/../game" }));

    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90000, now: () => now });

    expect(registry.listProjects()).toMatchObject([{
      canonical_project_root: "c:/u/game",
      aliases: expect.arrayContaining([
        { environment: "unity", host_path: "C:/u/other/../game", canonical_path: "c:/u/game" },
      ]),
    }]);
    expect(registry.resolveProject("C:/u/other/../game")).toMatchObject({ status: "ok", record: { instance_id: "game-a" } });
    expect(registry.resolveProject("c:/u/game")).toMatchObject({ status: "ok", record: { instance_id: "game-a" } });
  });

  it("fails ambiguous when distinct heartbeat roots clean to one canonical identity", () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "game-a", project_root: "C:/u/other/../game" }));
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "game-b", project_root: "C:/u/game", unity_pid: 22222, bridge_port: 18143 }));

    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90000, now: () => now });

    expect(registry.resolveProject("C:/u/other/../game")).toMatchObject({ status: "error", code: "duplicate_project_id" });
    expect(registry.resolveProject("c:/u/game")).toMatchObject({ status: "error", code: "duplicate_project_id" });
  });

  it("uses canonical root selectors to disambiguate duplicate project ids", () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "game-a", project_root: "C:/u/game" }));
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "game-b", project_root: "D:/u/game", unity_pid: 22222, bridge_port: 18143 }));

    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90000, now: () => now });

    expect(registry.resolveProject("game")).toMatchObject({ status: "error", code: "duplicate_project_id" });
    expect(registry.resolveProject("D:\\u\\game")).toMatchObject({ status: "ok", record: { instance_id: "game-b", canonical_project_root: "d:/u/game" } });
  });

  it("ignores invalid heartbeat files during discovery", () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "game-a" }));
    fs.writeFileSync(path.join(instancesDir, "broken.json"), JSON.stringify({ schema_version: 1, bridge_port: 0 }));

    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90000, now: () => now });
    expect(registry.listProjects()).toHaveLength(1);
    expect(registry.resolveProject("game")).toMatchObject({ status: "ok", record: { instance_id: "game-a" } });
  });

  it("classifies protocol version mismatches separately without exposing heartbeat content", () => {
    const instancesDir = makeTempDir();
    fs.writeFileSync(path.join(instancesDir, "future.json"), JSON.stringify({ schema_version: 2, token: "never-diagnose-this", payload: "private-content" }));
    fs.writeFileSync(path.join(instancesDir, "missing.json"), JSON.stringify({ token: "also-private" }));
    fs.writeFileSync(path.join(instancesDir, "invalid.json"), JSON.stringify({ schema_version: 1, token: "invalid-private" }));

    const stats = new ProjectRegistry({ instancesDir, staleAfterMs: 90000, now: () => now }).getStats();

    expect(stats).toMatchObject({ invalidProjectRecordsCount: 1, protocolVersionMismatchProjectRecordsCount: 2 });
    expect(JSON.stringify(stats)).not.toContain("private");
    expect(JSON.stringify(stats)).not.toContain("schema_version");
  });

  it("rejects actual oversized heartbeat bytes before decoding without exposing content", () => {
    const instancesDir = makeTempDir();
    const privateToken = "oversized-private-token";
    const privateContent = "oversized-private-content";
    const oversized = Buffer.from(JSON.stringify({
      schema_version: 1,
      token: privateToken,
      payload: privateContent,
      padding: "x".repeat(BRIDGE_PROTOCOL_BOUNDS.heartbeatBytes),
    }), "utf8");
    expect(oversized.byteLength).toBeGreaterThan(BRIDGE_PROTOCOL_BOUNDS.heartbeatBytes);
    fs.writeFileSync(path.join(instancesDir, "oversized.json"), oversized);

    const stats = new ProjectRegistry({ instancesDir, staleAfterMs: 90000, now: () => now }).getStats();
    expect(stats).toMatchObject({
      invalidProjectRecordsCount: 1,
      protocolVersionMismatchProjectRecordsCount: 0,
    });
    expect(JSON.stringify(stats)).not.toContain(privateToken);
    expect(JSON.stringify(stats)).not.toContain(privateContent);
  });

  it("authorizes heartbeat size from the single Buffer read before UTF-8 decoding", () => {
    const source = fs.readFileSync(path.join(process.cwd(), "src", "registry", "project-registry.ts"), "utf8");

    expect(source).toContain("const readBuffer = Buffer.alloc(BRIDGE_PROTOCOL_BOUNDS.heartbeatBytes + 1);");
    expect(source).toContain("this.#fileSystem.readSync(handle, readBuffer");
    expect(source).toContain("heartbeatBytes.byteLength > BRIDGE_PROTOCOL_BOUNDS.heartbeatBytes");
    expect(source.indexOf("heartbeatBytes.byteLength")).toBeLessThan(source.indexOf('heartbeatBytes.toString("utf8")'));
    expect(source).toContain("sameIdentity(before, pathAfter)");
    expect(source).toContain("sameIdentity(opened, openedAfter)");
    expect(source).toContain("samePathAndDescriptorIdentity(pathAfter, openedAfter, this.#platform)");
    expect(source.indexOf("before.size > BigInt(BRIDGE_PROTOCOL_BOUNDS.heartbeatBytes)")).toBeLessThan(source.indexOf("Buffer.alloc(BRIDGE_PROTOCOL_BOUNDS.heartbeatBytes + 1)"));
    expect(source).not.toContain('readFileSync(filePath, "utf8")');
  });

  it("reports live project counts separately from stale heartbeat records", () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "game-live" }));
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "game-stale", unity_pid: 22222, bridge_port: 18143, last_heartbeat: "2026-05-12T09:00:00.000Z" }));
    writeHeartbeat(instancesDir, heartbeat({ project: "tools", instance_id: "tools-stale", unity_pid: 33333, bridge_port: 18144, last_heartbeat: "2026-05-12T09:00:00.000Z" }));
    fs.writeFileSync(path.join(instancesDir, "broken.json"), JSON.stringify({ schema_version: 1, bridge_port: 0 }));

    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90000, now: () => now });

    expect(registry.getStats()).toEqual({
      projectRecordsCount: 3,
      liveProjectRecordsCount: 1,
      staleProjectRecordsCount: 2,
      invalidProjectRecordsCount: 1,
      protocolVersionMismatchProjectRecordsCount: 0,
      removedProjectRecordsCount: 0,
      uniqueProjectsCount: 2,
      liveProjectsCount: 1,
      staleProjectsCount: 1,
      heartbeatCacheHitCount: 0,
      heartbeatCacheMissCount: 4,
      heartbeatCacheEntriesCount: 3,
      invalidProjectRecordDiagnostics: [{ code: "heartbeat_invalid_schema" }],
      invalidProjectRecordDiagnosticsTruncated: false,
      heartbeatScanFilesCount: 4,
      heartbeatScanOverflowCount: 0,
      heartbeatScanTruncated: false,
      registryOperationalState: "ok",
    });
  });

  it("hides stale duplicate records when a live heartbeat covers the same project root", () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "game-live" }));
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "game-stopping-stale", unity_pid: 22222, bridge_port: 18143, status: "stopping", last_heartbeat: "2026-05-12T09:00:00.000Z" }));
    writeHeartbeat(instancesDir, heartbeat({ project: "tools", instance_id: "tools-stale", project_root: "C:/u/tools", unity_pid: 33333, bridge_port: 18144, last_heartbeat: "2026-05-12T09:00:00.000Z" }));

    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90000, now: () => now });

    expect(registry.listProjects().map((project) => project.instance_id)).toEqual(["game-live", "tools-stale"]);
    expect(registry.listProjectRecords().map((project) => project.instance_id)).toEqual(["game-live", "game-stopping-stale", "tools-stale"]);
    expect(registry.getStats()).toMatchObject({ projectRecordsCount: 3, staleProjectRecordsCount: 2, liveProjectsCount: 1, staleProjectsCount: 1 });
    expect(registry.resolveProject("game")).toMatchObject({ status: "ok", record: { instance_id: "game-live" } });
  });

  it("paginates and filters stale project records with snapshot-bound cursors", () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "game-live" }));
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "game-stale-newer", status: "stopping", unity_pid: 22222, last_heartbeat: "2026-05-12T09:30:00.0000Z" }));
    writeHeartbeat(instancesDir, heartbeat({ project: "tools", instance_id: "tools-stale-older", project_root: "C:/u/tools", status: "error", unity_pid: 33333, last_heartbeat: "2026-05-12T09:00:00.000Z" }));
    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, now: () => now });

    const first = registry.queryStaleProjects({ limit: 1 });
    expect(first).toMatchObject({ returned_count: 1, total_available: 2, truncated: true, projects: [{ instance_id: "game-stale-newer", stale: true }] });
    expect(first.projects[0]?.stale_record_id).toMatch(/^sha256:[a-f0-9]{64}$/);
    expect(first.next_cursor).toBeTypeOf("string");
    const second = registry.queryStaleProjects({ limit: 1, cursor: first.next_cursor! });
    expect(second).toMatchObject({ returned_count: 1, total_available: 2, truncated: false, next_cursor: null, projects: [{ instance_id: "tools-stale-older" }] });
    expect(registry.queryStaleProjects({ project: "tools" })).toMatchObject({ total_available: 1, projects: [{ instance_id: "tools-stale-older" }] });
    expect(registry.queryStaleProjects({ status: "stopping" })).toMatchObject({ total_available: 1, projects: [{ instance_id: "game-stale-newer" }] });
    expect(registry.queryStaleProjects({ last_heartbeat_from: "2026-05-12T11:15:00+02:00" })).toMatchObject({ total_available: 1, projects: [{ instance_id: "game-stale-newer" }] });
    expect(registry.queryStaleProjects({ last_heartbeat_from: "2026-05-12T09:30:00Z", last_heartbeat_to: "2026-05-12T09:30:00Z" })).toMatchObject({ total_available: 1, projects: [{ instance_id: "game-stale-newer" }] });

    writeHeartbeat(instancesDir, heartbeat({ project: "extra", instance_id: "extra-stale", project_root: "C:/u/extra", unity_pid: 44444, last_heartbeat: "2026-05-12T08:00:00.000Z" }));
    expect(() => registry.queryStaleProjects({ limit: 1, cursor: first.next_cursor! })).toThrow("current query snapshot");
  });

  it("cleans only confirmed-dead expired stale heartbeat records after exact dry-run binding", () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "game-live" }));
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "dead-stale", unity_pid: 22222, last_heartbeat: "2026-05-12T09:00:00.000Z" }));
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "live-stale", unity_pid: 33333, last_heartbeat: "2026-05-12T09:00:00.000Z" }));
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "young-stale", unity_pid: 44444, last_heartbeat: "2026-05-12T09:58:00.000Z" }));
    const malformed = path.join(instancesDir, "malformed.json");
    fs.writeFileSync(malformed, "{not-json");
    const registry = new ProjectRegistry({
      instancesDir,
      staleAfterMs: 90_000,
      cleanupAfterMs: 600_000,
      processState: (pid) => pid === 22222 ? "dead" : "live",
      now: () => now,
    });

    const preview = registry.cleanupStaleRecords({ limit: 10 });
    expect(preview).toMatchObject({
      dry_run: true,
      mutation_attempted: false,
      candidate_count: 2,
      selected_count: 2,
      would_delete_count: 1,
      deleted_count: 0,
      skipped_count: 1,
      records: expect.arrayContaining([
        expect.objectContaining({ instance_id: "dead-stale", process_state: "dead", action: "would_delete" }),
        expect.objectContaining({ instance_id: "live-stale", process_state: "live", action: "skipped_process_live" }),
      ]),
    });
    expect(preview.precondition_hash).toMatch(/^sha256:[a-f0-9]{64}$/);
    const deadRecord = preview.records.find((record) => record.instance_id === "dead-stale")!;
    expect(() => registry.cleanupStaleRecords({ dry_run: false, record_ids: [deadRecord.stale_record_id], precondition_hash: preview.precondition_hash! })).toThrow("confirm_cleanup=true");
    expect(() => registry.cleanupStaleRecords({ dry_run: false, confirm_cleanup: true, record_ids: [deadRecord.stale_record_id], precondition_hash: `sha256:${"0".repeat(64)}` })).toThrow("precondition changed");
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "dead-stale", status: "error", unity_pid: 22222, last_heartbeat: "2026-05-12T09:00:00.000Z" }));
    expect(() => registry.cleanupStaleRecords({ dry_run: false, confirm_cleanup: true, record_ids: [deadRecord.stale_record_id], precondition_hash: preview.precondition_hash! })).toThrow("missing, changed, or no longer eligible");
    expect(fs.existsSync(path.join(instancesDir, "dead-stale.json"))).toBe(true);
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "dead-stale", unity_pid: 22222, last_heartbeat: "2026-05-12T09:00:00.000Z" }));

    const applied = registry.cleanupStaleRecords({
      dry_run: false,
      confirm_cleanup: true,
      record_ids: [deadRecord.stale_record_id],
      precondition_hash: preview.precondition_hash!,
    });
    expect(applied).toMatchObject({ dry_run: false, mutation_attempted: true, deleted_count: 1, records: [{ instance_id: "dead-stale", action: "deleted" }] });
    expect(fs.existsSync(path.join(instancesDir, "dead-stale.json"))).toBe(false);
    expect(fs.existsSync(path.join(instancesDir, "live-stale.json"))).toBe(true);
    expect(fs.existsSync(path.join(instancesDir, "young-stale.json"))).toBe(true);
    expect(fs.existsSync(path.join(instancesDir, "game-live.json"))).toBe(true);
    expect(fs.existsSync(malformed)).toBe(true);
  });

  it("never treats a heartbeat stored under a mismatched filename as a cleanup target", () => {
    const instancesDir = makeTempDir();
    const mismatchedPath = path.join(instancesDir, "different-name.json");
    fs.writeFileSync(mismatchedPath, JSON.stringify(heartbeat({ instance_id: "dead-stale", unity_pid: 22222, last_heartbeat: "2026-05-12T09:00:00.000Z" })));
    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, cleanupAfterMs: 600_000, processState: () => "dead", now: () => now });

    expect(registry.cleanupStaleRecords()).toMatchObject({ candidate_count: 0, would_delete_count: 0, deleted_count: 0 });
    expect(registry.getStats()).toMatchObject({ projectRecordsCount: 1, invalidProjectRecordsCount: 0 });
    expect(fs.existsSync(mismatchedPath)).toBe(true);
  });

  it("ignores expired heartbeat files without mutating registry storage", () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "game-live" }));
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "old-stale", last_heartbeat: "2026-05-12T09:40:00.000Z" }));
    const invalidPath = path.join(instancesDir, "old-invalid.json");
    fs.writeFileSync(invalidPath, JSON.stringify({ schema_version: 1, bridge_port: 0 }));
    fs.utimesSync(invalidPath, new Date("2026-05-12T09:40:00.000Z"), new Date("2026-05-12T09:40:00.000Z"));

    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90000, cleanupAfterMs: 600000, now: () => now });

    expect(registry.getStats({ cleanup: true })).toMatchObject({
      projectRecordsCount: 1,
      liveProjectRecordsCount: 1,
      invalidProjectRecordsCount: 2,
      protocolVersionMismatchProjectRecordsCount: 0,
      removedProjectRecordsCount: 0,
      liveProjectsCount: 1,
      staleProjectsCount: 0,
      invalidProjectRecordDiagnostics: [{ code: "heartbeat_expired" }, { code: "heartbeat_expired" }],
    });
    expect(fs.existsSync(path.join(instancesDir, "old-stale.json"))).toBe(true);
    expect(fs.existsSync(invalidPath)).toBe(true);
  });

  it("does not reopen or reparse unchanged valid heartbeats", () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat());
    let opens = 0;
    const fileSystem = wrappingFileSystem({ openSync(file, flags) { opens += 1; return fs.openSync(file, flags); } });
    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, fileSystem, now: () => now });

    expect(registry.listProjects()).toHaveLength(1);
    expect(registry.getStats()).toMatchObject({ heartbeatCacheHitCount: 1, heartbeatCacheMissCount: 0, heartbeatCacheEntriesCount: 1 });
    expect(opens).toBe(1);
  });

  it("accepts native Windows zero-device identities for directory scans, health, and cache", () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat());
    const fileSystem = wrappingFileSystem({
      platform: "win32",
      lstatSync(file, options) { return identityStats(fs.lstatSync(file, options), { dev: 0n }); },
      fstatSync(handle, options) { return identityStats(fs.fstatSync(handle, options), { dev: 7n }); },
    });
    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, fileSystem, now: () => now });

    expect(registry.listProjects()).toMatchObject([{ project: "game", stale: false }]);
    const stats = registry.getStats();
    expect(stats).toMatchObject({
      projectRecordsCount: 1,
      heartbeatCacheHitCount: 1,
      heartbeatCacheMissCount: 0,
      heartbeatCacheEntriesCount: 1,
      invalidProjectRecordDiagnostics: [],
      registryOperationalState: "ok",
    });
    expect(publicRegistryOperationalStatus(stats)).toMatchObject({ status: "ok", invalid_diagnostic_codes: [] });
  });

  it("fails unsafe for a zero Windows directory inode and recovers health when identity becomes certain", () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat());
    let uncertain = true;
    const fileSystem = wrappingFileSystem({
      platform: "win32",
      lstatSync(file, options) {
        const stat = fs.lstatSync(file, options);
        return identityStats(stat, { dev: 0n, ...(uncertain && file === instancesDir ? { ino: 0n } : {}) });
      },
      fstatSync(handle, options) { return identityStats(fs.fstatSync(handle, options), { dev: 0n }); },
    });
    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, fileSystem, now: () => now });

    expect(publicRegistryOperationalStatus(registry.getStats())).toMatchObject({ status: "unsafe", invalid_diagnostic_codes: ["unsafe_registry_directory"] });
    uncertain = false;
    const recovered = registry.getStats();
    expect(recovered).toMatchObject({ projectRecordsCount: 1, heartbeatCacheEntriesCount: 1, invalidProjectRecordDiagnostics: [], registryOperationalState: "ok" });
    expect(publicRegistryOperationalStatus(recovered)).toMatchObject({ status: "ok", invalid_diagnostic_codes: [] });
  });

  it("keeps cached heartbeat identity isolated from returned record mutation", () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat());
    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, now: () => now });
    const first = registry.resolveProject("game");
    if (first.status !== "ok") throw new Error("expected project");
    first.record.project = "mutated";
    first.record.commands_hash = "mutated";
    first.record.aliases[0]!.host_path = "private-mutated-root";

    const second = registry.resolveProject("game");
    expect(second).toMatchObject({ status: "ok", record: { project: "game", commands_hash: "9f12a3c0" } });
    if (second.status !== "ok") throw new Error("expected cached project");
    expect(second.record.aliases[0]?.host_path).toBe("C:/u/game");
  });

  it("detects atomic replacement with the same size and timestamp", () => {
    const instancesDir = makeTempDir();
    const file = path.join(instancesDir, "game-a.json");
    const first = JSON.stringify(heartbeat({ project: "aaaa" }));
    const second = JSON.stringify(heartbeat({ project: "bbbb" }));
    expect(Buffer.byteLength(first)).toBe(Buffer.byteLength(second));
    fs.writeFileSync(file, first);
    const timestamp = new Date("2026-05-12T09:59:00.000Z");
    fs.utimesSync(file, timestamp, timestamp);
    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, now: () => now });
    expect(registry.listProjects()).toMatchObject([{ project: "aaaa" }]);

    const replacement = `${file}.replacement`;
    fs.writeFileSync(replacement, second);
    fs.utimesSync(replacement, timestamp, timestamp);
    fs.renameSync(replacement, file);
    expect(registry.listProjects()).toMatchObject([{ project: "bbbb" }]);
    expect(registry.getStats()).toMatchObject({ heartbeatCacheHitCount: 1, heartbeatCacheMissCount: 0 });
  });

  it("keeps a high-cardinality scan operational during an atomic heartbeat replacement", () => {
    const instancesDir = makeTempDir();
    for (let index = 0; index < 1024; index += 1) {
      writeHeartbeat(instancesDir, heartbeat({ instance_id: `instance-${index}`, project: `project-${index}`, unity_pid: 20_000 + index, heartbeat_generation: 1 }));
    }
    const target = path.join(instancesDir, "instance-777.json");
    let replaced = false;
    const fileSystem = wrappingFileSystem({
      opendirSync(directory) {
        const handle = fs.opendirSync(directory);
        return {
          readSync() {
            const entry = handle.readSync();
            if (!replaced && entry !== null) {
              const temporary = `${target}.replacement`;
              fs.writeFileSync(temporary, JSON.stringify(heartbeat({ instance_id: "instance-777", project: "project-777", unity_pid: 20_777, heartbeat_generation: 2 })));
              fs.renameSync(temporary, target);
              replaced = true;
            }
            return entry;
          },
          closeSync() { handle.closeSync(); },
        };
      },
    });
    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, cacheMaxEntries: 1024, scanMaxFiles: 1024, fileSystem, now: () => now });

    const snapshot = registry.captureSnapshot();

    expect(replaced).toBe(true);
    expect(snapshot.getStats()).toMatchObject({ projectRecordsCount: 1024, registryOperationalState: "ok" });
    expect(snapshot.resolveProject("project-777")).toMatchObject({ status: "ok", record: { instance_id: "instance-777", heartbeat_generation: 2 } });
  });

  it.each(["ino", "ctimeNs", "size", "mtimeNs"] as const)("invalidates the Windows dev-zero cache when %s changes", (changedField) => {
    const instancesDir = makeTempDir();
    const file = path.join(instancesDir, "game-a.json");
    const first = JSON.stringify(heartbeat({ project: changedField === "size" ? "a" : "aaaa" }));
    const second = JSON.stringify(heartbeat({ project: changedField === "size" ? "longer" : "bbbb" }));
    if (changedField !== "size") expect(Buffer.byteLength(first)).toBe(Buffer.byteLength(second));
    fs.writeFileSync(file, first);
    let generation = 0n;
    const stableIdentity = (stat: BigIntStats, dev: bigint): BigIntStats => identityStats(stat, {
      dev,
      ino: changedField === "ino" ? 101n + generation : 101n,
      ctimeNs: changedField === "ctimeNs" ? 201n + generation : 201n,
      mtimeNs: changedField === "mtimeNs" ? 301n + generation : 301n,
    });
    const fileSystem = wrappingFileSystem({
      platform: "win32",
      lstatSync(target, options) { const stat = fs.lstatSync(target, options); return stat.isFile() ? stableIdentity(stat, 0n) : identityStats(stat, { dev: 0n }); },
      fstatSync(handle, options) { return stableIdentity(fs.fstatSync(handle, options), 7n); },
    });
    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, fileSystem, now: () => now });
    expect(registry.listProjects()).toHaveLength(1);

    generation = 1n;
    fs.writeFileSync(file, second);
    expect(registry.getStats()).toMatchObject({ projectRecordsCount: 1, heartbeatCacheHitCount: 0, heartbeatCacheMissCount: 1, heartbeatCacheEntriesCount: 1 });
    expect(registry.resolveProject(changedField === "size" ? "longer" : "bbbb")).toMatchObject({ status: "ok" });
  });

  it.each(["retry", "reject"] as const)("handles a deterministic path replacement race with safe %s", (outcome) => {
    const instancesDir = makeTempDir();
    const file = path.join(instancesDir, "game-a.json");
    fs.writeFileSync(file, JSON.stringify(heartbeat({ project: "first" })));
    let generation = 0n;
    let opens = 0;
    const handleGenerations = new Map<number, bigint>();
    const pathIdentity = (stat: BigIntStats): BigIntStats => identityStats(stat, { dev: 0n, ino: 101n + generation, ctimeNs: 201n + generation, mtimeNs: 301n + generation });
    const descriptorIdentity = (stat: BigIntStats, handle: number): BigIntStats => {
      const openedGeneration = handleGenerations.get(handle);
      if (openedGeneration === undefined) throw new Error("unknown test handle");
      return identityStats(stat, { dev: 7n, ino: 101n + openedGeneration, ctimeNs: 201n + openedGeneration, mtimeNs: 301n + openedGeneration });
    };
    const fileSystem = wrappingFileSystem({
      platform: "win32",
      lstatSync(target, options) { const stat = fs.lstatSync(target, options); return stat.isFile() ? pathIdentity(stat) : identityStats(stat, { dev: 0n }); },
      openSync(target, flags) { const handle = fs.openSync(target, flags); opens += 1; handleGenerations.set(handle, generation); return handle; },
      fstatSync(handle, options) { return descriptorIdentity(fs.fstatSync(handle, options), handle); },
      readSync(handle, buffer, offset, length, position) {
        const count = fs.readSync(handle, buffer, offset, length, position);
        if (count > 0 && (outcome === "reject" || generation === 0n)) generation += 1n;
        return count;
      },
      closeSync(handle) { handleGenerations.delete(handle); fs.closeSync(handle); },
    });
    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, fileSystem, now: () => now });

    const stats = registry.getStats();
    expect(opens).toBe(2);
    if (outcome === "retry") {
      expect(stats).toMatchObject({ projectRecordsCount: 1, invalidProjectRecordDiagnostics: [], heartbeatCacheEntriesCount: 1, registryOperationalState: "ok" });
      expect(registry.listProjects()).toMatchObject([{ project: "first" }]);
    } else {
      expect(stats).toMatchObject({ projectRecordsCount: 0, invalidProjectRecordsCount: 1, invalidProjectRecordDiagnostics: [{ code: "heartbeat_unstable" }], heartbeatCacheEntriesCount: 0, registryOperationalState: "ok" });
    }
  });

  it("does not cache partial writes and recovers on the next scan", () => {
    const instancesDir = makeTempDir();
    const file = path.join(instancesDir, "game-a.json");
    fs.writeFileSync(file, "{\"token\":\"partial-secret");
    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, now: () => now });
    expect(registry.getStats()).toMatchObject({ invalidProjectRecordsCount: 1, heartbeatCacheEntriesCount: 0, invalidProjectRecordDiagnostics: [{ code: "heartbeat_invalid_json" }] });

    fs.writeFileSync(file, JSON.stringify(heartbeat()));
    expect(registry.listProjects()).toHaveLength(1);
    expect(registry.getStats()).toMatchObject({ heartbeatCacheHitCount: 1, invalidProjectRecordsCount: 0 });
  });

  it("expires deleted and TTL-stale cache entries", () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat());
    let clock = now.getTime();
    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, cacheTtlMs: 1000, now: () => new Date(clock) });
    registry.listProjects();
    clock += 1001;
    expect(registry.getStats()).toMatchObject({ heartbeatCacheHitCount: 0, heartbeatCacheMissCount: 1, heartbeatCacheEntriesCount: 1 });
    fs.rmSync(path.join(instancesDir, "game-a.json"));
    clock += 1001;
    expect(registry.getStats()).toMatchObject({ projectRecordsCount: 0, heartbeatCacheEntriesCount: 0 });
  });

  it("caches the complete bounded valid snapshot and enforces cache capacity", () => {
    const instancesDir = makeTempDir();
    for (let index = 0; index < 5; index += 1) writeHeartbeat(instancesDir, heartbeat({ project: `game-${index}`, instance_id: `game-${index}` }));
    expect(() => new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, cacheMaxEntries: 2, scanMaxFiles: 3, now: () => now })).toThrow("cache_max_entries");
    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, cacheMaxEntries: 5, scanMaxFiles: 5, now: () => now });
    expect(registry.getStats()).toMatchObject({ projectRecordsCount: 5, heartbeatCacheMissCount: 5, heartbeatCacheEntriesCount: 5, registryOperationalState: "ok" });
    expect(registry.getStats()).toMatchObject({ projectRecordsCount: 5, heartbeatCacheHitCount: 5, heartbeatCacheEntriesCount: 5 });
  });

  it.each([
    ["inode", { ino: 0n }],
    ["mtime", { mtimeNs: 0n }],
    ["ctime", { ctimeNs: 0n }],
    ["size", { size: -1n }],
  ] as const)("rejects a Windows heartbeat with invalid %s identity and reports bounded health", (_field, invalidIdentity) => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat());
    let opens = 0;
    const fileSystem = wrappingFileSystem({
      platform: "win32",
      lstatSync(file, options) { const stat = fs.lstatSync(file, options); return identityStats(stat, stat.isFile() ? { dev: 0n, ...invalidIdentity } : { dev: 0n }); },
      fstatSync(handle, options) { return identityStats(fs.fstatSync(handle, options), { dev: 7n, ...invalidIdentity }); },
      openSync(file, flags) { opens += 1; return fs.openSync(file, flags); },
    });
    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, fileSystem, now: () => now });
    const stats = registry.getStats();

    expect(stats).toMatchObject({ projectRecordsCount: 0, invalidProjectRecordsCount: 1, heartbeatCacheEntriesCount: 0, invalidProjectRecordDiagnostics: [{ code: "unsafe_heartbeat_file" }], registryOperationalState: "ok" });
    expect(publicRegistryOperationalStatus(stats)).toMatchObject({ status: "ok", invalid_records: 1, invalid_diagnostic_codes: ["unsafe_heartbeat_file"] });
    expect(opens).toBe(0);
  });

  it.each(["path_before", "descriptor_before", "descriptor_after", "path_after"] as const)("requires certain Windows file identity at %s", (stage) => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat());
    let fileLstats = 0;
    let fileFstats = 0;
    const fileSystem = wrappingFileSystem({
      platform: "win32",
      lstatSync(file, options) {
        const stat = fs.lstatSync(file, options);
        if (!stat.isFile()) return identityStats(stat, { dev: 0n });
        fileLstats += 1;
        return identityStats(stat, { dev: 0n, ...(stage === "path_before" && fileLstats === 2 || stage === "path_after" && fileLstats === 3 ? { ino: 0n } : {}) });
      },
      fstatSync(handle, options) {
        fileFstats += 1;
        return identityStats(fs.fstatSync(handle, options), { dev: 7n, ...(stage === "descriptor_before" && fileFstats === 1 || stage === "descriptor_after" && fileFstats === 2 ? { ino: 0n } : {}) });
      },
    });
    const stats = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, fileSystem, now: () => now }).getStats();

    expect(stats).toMatchObject({ projectRecordsCount: 0, invalidProjectRecordsCount: 1, heartbeatCacheEntriesCount: 0, invalidProjectRecordDiagnostics: [{ code: "unsafe_heartbeat_file" }] });
  });

  it("rejects a POSIX heartbeat with an unknown device", () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat());
    let opens = 0;
    const fileSystem = wrappingFileSystem({
      platform: "linux",
      lstatSync(file, options) { const stat = fs.lstatSync(file, options); return identityStats(stat, { dev: stat.isFile() ? 0n : 7n }); },
      fstatSync(handle, options) { return identityStats(fs.fstatSync(handle, options), { dev: 0n }); },
      openSync(file, flags) { opens += 1; return fs.openSync(file, flags); },
    });
    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, fileSystem, now: () => now });
    expect(registry.getStats()).toMatchObject({ projectRecordsCount: 0, invalidProjectRecordsCount: 1, heartbeatCacheEntriesCount: 0, invalidProjectRecordDiagnostics: [{ code: "unsafe_heartbeat_file" }] });
    expect(opens).toBe(0);
  });

  it.skipIf(process.platform === "win32")("rejects symlink heartbeat files and symlink registry components", () => {
    const root = makeTempDir();
    const instancesDir = path.join(root, "instances");
    fs.mkdirSync(instancesDir, { mode: 0o700 });
    const privateFile = path.join(root, "private.json");
    fs.writeFileSync(privateFile, JSON.stringify(heartbeat({ token: "symlink-private-token" })));
    fs.symlinkSync(privateFile, path.join(instancesDir, "linked.json"));
    const fileStats = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, now: () => now }).getStats();
    expect(fileStats).toMatchObject({ projectRecordsCount: 0, invalidProjectRecordDiagnostics: [{ code: "unsafe_heartbeat_file" }] });

    const linkedDir = path.join(root, "linked-instances");
    fs.symlinkSync(instancesDir, linkedDir, "dir");
    const directoryStats = new ProjectRegistry({ instancesDir: linkedDir, staleAfterMs: 90_000, now: () => now }).getStats();
    expect(directoryStats).toMatchObject({ projectRecordsCount: 0, invalidProjectRecordDiagnostics: [{ code: "unsafe_registry_directory" }] });
    expect(JSON.stringify({ fileStats, directoryStats })).not.toContain("private-token");
  });

  it.skipIf(process.platform === "win32")("rejects a POSIX registry directory that is not owner-only", () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat());
    fs.chmodSync(instancesDir, 0o755);
    expect(new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, now: () => now }).getStats()).toMatchObject({
      projectRecordsCount: 0,
      invalidProjectRecordDiagnostics: [{ code: "unsafe_registry_directory" }],
    });
  });

  it("fails closed when a POSIX registry directory device identity is uncertain", () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat());
    const fileSystem = wrappingFileSystem({
      platform: "linux",
      lstatSync(file, options) { const stat = fs.lstatSync(file, options); return identityStats(stat, { dev: stat.isDirectory() && file === instancesDir ? 0n : 7n }); },
    });
    expect(new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, fileSystem, now: () => now }).getStats()).toMatchObject({
      projectRecordsCount: 0,
      heartbeatCacheEntriesCount: 0,
      invalidProjectRecordDiagnostics: [{ code: "unsafe_registry_directory" }],
    });
  });

  it("bounds diagnostics for thousands of invalid files without leaking names or contents", () => {
    const instancesDir = makeTempDir();
    const secret = "registry-super-secret";
    for (let index = 0; index < 2000; index += 1) fs.writeFileSync(path.join(instancesDir, `private-${secret}-${index}.json`), `{\"token\":\"${secret}\"`);
    const stats = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, invalidDiagnosticsMax: 7, now: () => now }).getStats();
    const serialized = JSON.stringify(stats);
    expect(stats).toMatchObject({ invalidProjectRecordsCount: 0, invalidProjectRecordDiagnosticsTruncated: false, heartbeatCacheEntriesCount: 0, heartbeatScanTruncated: true, registryOperationalState: "over_capacity" });
    expect(stats.invalidProjectRecordDiagnostics).toEqual([]);
    expect(serialized).not.toMatch(/registry-super-secret|private-|token|project_root|instance_id/);
  });

  it("returns zero unstable state when the registry generation changes on both attempts", () => {
    const root = makeTempDir();
    const instancesDir = path.join(root, "instances");
    let alternateDir = path.join(root, "alternate");
    fs.mkdirSync(instancesDir, { mode: 0o700 });
    fs.mkdirSync(alternateDir, { mode: 0o700 });
    writeHeartbeat(instancesDir, heartbeat({ project: "trusted" }));
    writeHeartbeat(alternateDir, heartbeat({ project: "attacker", token: "attacker-private-token" }));
    let swaps = 0;
    const fileSystem = wrappingFileSystem({
      realpathSync(file) {
        const result = fs.realpathSync.native(file);
        const holding = path.join(root, `holding-${swaps}`);
        fs.renameSync(instancesDir, holding);
        fs.renameSync(alternateDir, instancesDir);
        alternateDir = holding;
        swaps += 1;
        return result;
      },
    });

    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, fileSystem, now: () => now });
    const snapshot = registry.captureSnapshot();
    const stats = snapshot.getStats();
    expect(swaps).toBeGreaterThanOrEqual(2);
    expect(stats).toMatchObject({ projectRecordsCount: 0, heartbeatCacheEntriesCount: 0, heartbeatCacheHitCount: 0, heartbeatCacheMissCount: 0, invalidProjectRecordDiagnostics: [], registryOperationalState: "unstable" });
    expect(snapshot.resolveProject("trusted")).toEqual({ status: "error", code: "unsafe_binding_refused", matches: [], registryOperationalState: "unstable" });
    expect(registry.resolveProject("trusted")).toEqual({ status: "error", code: "unsafe_binding_refused", matches: [], registryOperationalState: "unstable" });
    expect(JSON.stringify(stats)).not.toMatch(/attacker|private-token|project_root|instance_id/);
  });

  it("makes zero filesystem mutations for stale, malformed, and successor heartbeat files", () => {
    const instancesDir = makeTempDir();
    const stale = path.join(instancesDir, "old-stale.json");
    const malformed = path.join(instancesDir, "malformed.json");
    const successor = path.join(instancesDir, "successor.json");
    fs.writeFileSync(stale, JSON.stringify(heartbeat({ instance_id: "old-stale", last_heartbeat: "2026-05-12T09:40:00.000Z" })));
    fs.writeFileSync(malformed, "{\"token\":\"private-malformed");
    fs.utimesSync(malformed, new Date("2026-05-12T09:40:00.000Z"), new Date("2026-05-12T09:40:00.000Z"));
    fs.writeFileSync(successor, JSON.stringify(heartbeat({ instance_id: "successor", project: "successor" })));
    const before = new Map(fs.readdirSync(instancesDir).map((name) => [name, fs.readFileSync(path.join(instancesDir, name))]));
    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, cleanupAfterMs: 600_000, now: () => now });

    expect(registry.getStats({ cleanup: true })).toMatchObject({ removedProjectRecordsCount: 0, invalidProjectRecordsCount: 2, projectRecordsCount: 1 });
    expect(fs.readdirSync(instancesDir).sort(compareNames)).toEqual([...before.keys()].sort(compareNames));
    for (const [name, bytes] of before) expect(fs.readFileSync(path.join(instancesDir, name))).toEqual(bytes);
    expect(JSON.parse(fs.readFileSync(successor, "utf8"))).toMatchObject({ instance_id: "successor", project: "successor" });
  });

  it("returns zero complete results and clears cache when relevant files exceed capacity", () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "valid", project: "valid" }));
    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, cacheMaxEntries: 2, scanMaxFiles: 2, now: () => now });
    expect(registry.listProjects()).toMatchObject([{ project: "valid" }]);
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "second", project: "second" }));
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "third", project: "third" }));

    const stats = registry.getStats();
    expect(stats).toMatchObject({
      projectRecordsCount: 0,
      heartbeatCacheEntriesCount: 0,
      heartbeatScanFilesCount: 0,
      heartbeatScanOverflowCount: 1,
      heartbeatScanTruncated: true,
      invalidProjectRecordsCount: 0,
      invalidProjectRecordDiagnostics: [],
      registryOperationalState: "over_capacity",
    });
    expect(publicRegistryOperationalStatus(stats)).toMatchObject({ status: "over_capacity", invalid_records: 0, invalid_diagnostic_codes: [], scan_truncated: true });
    expect(registry.resolveProject("valid")).toEqual({ status: "error", code: "unsafe_binding_refused", matches: [], registryOperationalState: "over_capacity" });
  });

  it("returns a complete stable 1024-record snapshot across list, stats, and resolve", () => {
    const instancesDir = makeTempDir();
    for (let index = 0; index < 1024; index += 1) writeHeartbeat(instancesDir, heartbeat({ instance_id: `instance-${index}`, project: `project-${index}`, unity_pid: 20_000 + index }));
    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, cacheMaxEntries: 1024, scanMaxFiles: 1024, now: () => now });
    expect(registry.listProjects()).toHaveLength(1024);
    expect(registry.getStats()).toMatchObject({ projectRecordsCount: 1024, heartbeatCacheHitCount: 1024, heartbeatCacheEntriesCount: 1024, heartbeatScanFilesCount: 1024, heartbeatScanTruncated: false, registryOperationalState: "ok" });
    expect(registry.resolveProject("project-777")).toMatchObject({ status: "ok", record: { instance_id: "instance-777" } });
  });

  it("keeps one immutable capture coherent after the backing registry changes", () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "captured", project: "captured" }));
    let directoryReads = 0;
    const fileSystem = wrappingFileSystem({ opendirSync(directory) { directoryReads += 1; return fs.opendirSync(directory); } });
    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, fileSystem, now: () => now });

    const snapshot = registry.captureSnapshot();
    const readsAfterCapture = directoryReads;
    fs.rmSync(path.join(instancesDir, "captured.json"));
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "replacement", project: "replacement", unity_pid: 22222, bridge_port: 18143 }));

    expect(snapshot.resolveProject("captured")).toMatchObject({ status: "ok", record: { instance_id: "captured" } });
    expect(snapshot.listProjectRecords()).toMatchObject([{ instance_id: "captured" }]);
    expect(snapshot.getStats()).toMatchObject({ projectRecordsCount: 1, liveProjectRecordsCount: 1 });
    expect(snapshot.captured_at).toBe(snapshot.capture_started_at);
    expect(snapshot.capture_ended_at).toBe(snapshot.capture_started_at);
    expect(directoryReads).toBe(readsAfterCapture);
    expect(Object.isFrozen(snapshot.records)).toBe(true);
    expect(Object.isFrozen(snapshot.records[0])).toBe(true);
  });

  it("retains the immutable snapshot used for a top-level selector decision", () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "captured", project: "captured", last_heartbeat: "2026-05-12T09:00:00.000Z" }));
    let directoryReads = 0;
    const fileSystem = wrappingFileSystem({ opendirSync(directory) { directoryReads += 1; return fs.opendirSync(directory); } });
    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, fileSystem, now: () => now });

    const resolution = registry.resolveProject("captured");
    const snapshot = projectRegistrySnapshotForResolution(resolution);
    const readsAfterResolution = directoryReads;
    fs.rmSync(path.join(instancesDir, "captured.json"));
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "replacement", project: "captured", unity_pid: 22222, bridge_port: 18143 }));

    expect(resolution).toMatchObject({ status: "error", code: "project_stale", matches: [{ instance_id: "captured" }] });
    expect(snapshot?.listProjects()).toMatchObject([{ instance_id: "captured", stale: true }]);
    expect(snapshot?.resolveProject("captured")).toMatchObject({ status: "error", code: "project_stale", matches: [{ instance_id: "captured" }] });
    expect(directoryReads).toBe(readsAfterResolution);
  });

  it("keeps every snapshot API token-free and deeply frozen", () => {
    const instancesDir = makeTempDir();
    const old = heartbeat({ instance_id: "snapshot-old", display_name: "Snapshot Display", token: "snapshot-old-secret" });
    writeHeartbeat(instancesDir, old);
    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, now: () => now });
    registry.captureSnapshot();

    fs.rmSync(path.join(instancesDir, "snapshot-old.json"));
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "snapshot-new", display_name: "Snapshot Display", token: "snapshot-new-secret" }));
    const snapshot = registry.captureSnapshot();
    const topLevelResolution = registry.resolveProject("Snapshot Display");
    const listedProjects = snapshot.listProjects();
    const listedRecords = snapshot.listProjectRecords();
    const stats = snapshot.getStats();
    const resolution = snapshot.resolveProject("Snapshot Display");
    const recentReplacement = snapshot.findRecentReplacement("Snapshot Display");
    for (const selector of ["game", "Snapshot Display", "snapshot-old", "C:/u/game", "c:/u/game", "/mnt/c/u/game"]) {
      expect(snapshot.findRecentReplacement(selector)).toMatchObject({ replacement_record: { instance_id: "snapshot-new" } });
    }
    const apiValues: unknown[] = [
      snapshot.records,
      listedProjects,
      listedRecords,
      stats,
      resolution,
      snapshot.resolveProject("missing"),
      recentReplacement,
      snapshot.hasBridgeToken("snapshot-new"),
    ];

    expect(snapshot.hasBridgeToken("snapshot-new")).toBe(true);
    expect(topLevelResolution).toMatchObject({ status: "ok", record: { token: "snapshot-new-secret" } });
    expect(resolution).toMatchObject({ status: "ok", record: { instance_id: "snapshot-new" } });
    expect(recentReplacement).toMatchObject({ failed_record: { instance_id: "snapshot-old" }, replacement_record: { instance_id: "snapshot-new" } });
    for (const value of apiValues) {
      expect(JSON.stringify(value)).not.toContain("snapshot-old-secret");
      expect(JSON.stringify(value)).not.toContain("snapshot-new-secret");
    }
    expect(JSON.stringify(snapshot)).not.toContain("snapshot-old-secret");
    expect(JSON.stringify(snapshot)).not.toContain("snapshot-new-secret");
    expect(Object.isFrozen(snapshot.records)).toBe(true);
    expect(Object.isFrozen(snapshot.records[0])).toBe(true);
    expect(Object.isFrozen(listedProjects)).toBe(true);
    expect(Object.isFrozen(listedProjects[0])).toBe(true);
    expect(Object.isFrozen(listedRecords)).toBe(true);
    expect(Object.isFrozen(listedRecords[0])).toBe(true);
    expect(Object.isFrozen(stats)).toBe(true);
    if (resolution.status === "ok") expect(Object.isFrozen(resolution.record)).toBe(true);
    if (recentReplacement) {
      expect(Object.isFrozen(recentReplacement.failed_record)).toBe(true);
      expect(Object.isFrozen(recentReplacement.replacement_record)).toBe(true);
    }
  });

  it("bounds unrelated directory flood work and always closes the handle", () => {
    const instancesDir = makeTempDir();
    let reads = 0;
    let closes = 0;
    const fileSystem = wrappingFileSystem({
      opendirSync() { return generatedDirectory((index) => `noise-${index}.txt`, () => { reads += 1; }, () => { closes += 1; }); },
    });
    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, cacheMaxEntries: 2, scanMaxFiles: 2, fileSystem, now: () => now });
    expect(registry.getStats()).toMatchObject({ projectRecordsCount: 0, heartbeatScanTruncated: true, heartbeatScanOverflowCount: 1, invalidProjectRecordsCount: 0, invalidProjectRecordDiagnostics: [], registryOperationalState: "over_capacity" });
    expect(reads).toBeLessThanOrEqual(2 + 32 + 1);
    expect(closes).toBe(1);
  });

  it("uses one max-plus-one buffer and rejects a heartbeat that grows during descriptor read", () => {
    const instancesDir = makeTempDir();
    const file = path.join(instancesDir, "growing.json");
    fs.writeFileSync(file, JSON.stringify(heartbeat()));
    let maximumBuffer = 0;
    let grown = false;
    const fileSystem = wrappingFileSystem({
      readSync(handle, buffer, offset, length, position) {
        maximumBuffer = Math.max(maximumBuffer, buffer.byteLength);
        const count = fs.readSync(handle, buffer, offset, length, position);
        if (!grown) { grown = true; fs.appendFileSync(file, Buffer.alloc(BRIDGE_PROTOCOL_BOUNDS.heartbeatBytes + 1)); }
        return count;
      },
    });
    const stats = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, fileSystem, now: () => now }).getStats();
    expect(stats).toMatchObject({ projectRecordsCount: 0, invalidProjectRecordDiagnostics: [{ code: "heartbeat_too_large" }] });
    expect(maximumBuffer).toBe(BRIDGE_PROTOCOL_BOUNDS.heartbeatBytes + 1);
  });

  it("projects bounded aggregate registry state for authenticated health without identities", () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat({ token: "health-private-token", project_root: "C:/private/root" }));
    fs.writeFileSync(path.join(instancesDir, "secret-name.json"), "{\"token\":\"health-invalid-secret\"");
    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, invalidDiagnosticsMax: 1, now: () => now });
    registry.listProjects();
    const status = publicRegistryOperationalStatus(registry.getStats());

    expect(status).toEqual({
      cache_hits: 1,
      cache_misses: 1,
      cache_entries: 1,
      invalid_records: 1,
      invalid_diagnostic_codes: ["heartbeat_invalid_json"],
      invalid_diagnostics_truncated: false,
      scan_files: 2,
      scan_overflow: 0,
      scan_truncated: false,
      status: "ok",
    });
    expect(JSON.stringify(status)).not.toMatch(/health-private|invalid-secret|secret-name|project_root|instance_id|C:\/private/);
  });
});

function makeTempDir(): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-unity-registry-"));
  tempDirs.push(dir);
  return dir;
}

function writeHeartbeat(instancesDir: string, record: HeartbeatRecord | Omit<HeartbeatRecord, "canonical_project_root" | "aliases">): void {
  fs.writeFileSync(path.join(instancesDir, `${record.instance_id}.json`), JSON.stringify(record, null, 2));
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

type RegistryFileSystem = NonNullable<ProjectRegistryOptions["fileSystem"]>;

function wrappingFileSystem(overrides: Partial<RegistryFileSystem>): RegistryFileSystem {
  return {
    platform: process.platform,
    lstatSync: (file, options) => fs.lstatSync(file, options),
    opendirSync: (directory) => fs.opendirSync(directory),
    openSync: (file, flags) => fs.openSync(file, flags),
    fstatSync: (handle, options) => fs.fstatSync(handle, options),
    readSync: (handle, buffer, offset, length, position) => fs.readSync(handle, buffer, offset, length, position),
    closeSync: (handle) => fs.closeSync(handle),
    realpathSync: (file) => fs.realpathSync.native(file),
    ...overrides,
  };
}

function identityStats(stat: BigIntStats, overrides: Partial<Pick<BigIntStats, "dev" | "ino" | "size" | "mtimeNs" | "ctimeNs">>): BigIntStats {
  return new Proxy(stat, { get(target, property, receiver) { return property in overrides ? overrides[property as keyof typeof overrides] : Reflect.get(target, property, receiver); } });
}

function compareNames(left: string, right: string): number { return left < right ? -1 : left > right ? 1 : 0; }

function generatedDirectory(nameAt: (index: number) => string | null, onRead: () => void, onClose: () => void): ReturnType<RegistryFileSystem["opendirSync"]> {
  let index = 0;
  let closed = false;
  return {
    readSync() { onRead(); const name = nameAt(index++); return name === null ? null : fakeDirent(name); },
    closeSync() { if (!closed) { closed = true; onClose(); } },
  };
}

function fakeDirent(name: string): fs.Dirent {
  return {
    name,
    parentPath: "",
    isFile: () => name.endsWith(".json"),
    isDirectory: () => false,
    isBlockDevice: () => false,
    isCharacterDevice: () => false,
    isSymbolicLink: () => false,
    isFIFO: () => false,
    isSocket: () => false,
  };
}
