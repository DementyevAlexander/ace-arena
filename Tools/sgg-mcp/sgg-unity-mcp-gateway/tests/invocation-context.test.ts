import { inspect } from "node:util";

import { describe, expect, it } from "vitest";

import type { CommandDescriptor } from "../src/bridge/bridge-client.js";
import type { HeartbeatRecord } from "../src/registry/project-registry.js";
import { InvocationContextSnapshot, InvocationIdentity } from "../src/mcp/invocation-context.js";

describe("invocation identity context", () => {
  it("requires non-empty critical identity and the resolved descriptor command", () => {
    const descriptor = command();

    expect(() => InvocationIdentity.create({ projectSelector: "", project: "game", command: descriptor.id, descriptor })).toThrow(TypeError);
    expect(() => InvocationIdentity.create({ projectSelector: "game", project: " ", command: descriptor.id, descriptor })).toThrow(TypeError);
    expect(() => InvocationIdentity.create({ projectSelector: "game", project: "game", command: "other.command", descriptor })).toThrow(TypeError);
    expect(() => InvocationContextSnapshot.create({ projectSelector: "game", project: "game", command: descriptor.id, descriptor, commandsHash: "" })).toThrow(TypeError);
    expect(() => InvocationContextSnapshot.create({ projectSelector: "game", project: "game", command: descriptor.id, descriptor, commandsHash: null, record: heartbeat() })).toThrow(TypeError);
  });

  it("binds descriptor and heartbeat project identity in a frozen snapshot", () => {
    const descriptor = command();
    const snapshot = InvocationContextSnapshot.create({
      projectSelector: "game-alias",
      project: "game",
      command: descriptor.id,
      descriptor,
      commandsHash: "catalog-revision",
      record: heartbeat({ catalog_generation: 7 }),
    });

    expect(Object.isFrozen(snapshot)).toBe(true);
    expect(Object.isFrozen(snapshot.identity)).toBe(true);
    expect(snapshot.identity).toMatchObject({ projectSelector: "game-alias", project: "game", command: descriptor.id });
    expect(snapshot.descriptor).not.toBe(descriptor);
    expect(snapshot.record?.project).toBe("game");
    expect(snapshot.catalogGeneration).toBe(7);

    expect(() => InvocationContextSnapshot.create({
      projectSelector: "game",
      project: "other-project",
      command: descriptor.id,
      descriptor,
      commandsHash: null,
      record: heartbeat(),
    })).toThrow(TypeError);
  });

  it("detaches and recursively freezes descriptor and heartbeat values", () => {
    const descriptor = command();
    const record = heartbeat();
    const snapshot = InvocationContextSnapshot.create({
      projectSelector: "game",
      project: "game",
      command: descriptor.id,
      descriptor,
      commandsHash: "catalog-revision",
      record,
    });

    const snapshotDescriptor = snapshot.descriptor;
    const snapshotRecord = snapshot.record;
    descriptor.timeout_ms += 1;
    descriptor.schema.properties = {};
    record.project = "other-project";
    record.project_root = "C:/changed";
    record.bridge_host = "changed-host";
    record.bridge_port = 1;
    record.token = "changed-token";
    record.commands_hash = "changed-hash";
    record.catalog_generation = 8;
    record.aliases?.push({ environment: "unity", host_path: "C:/changed", canonical_path: "c:/changed" });

    expect(snapshotDescriptor.timeout_ms).toBe(1_000);
    expect(snapshotDescriptor.schema).toMatchObject({ properties: { nested: { type: "object" } } });
    expect(snapshotRecord).toMatchObject({
      project: "game",
      project_root: "C:/u/game",
      bridge_host: "127.0.0.1",
      bridge_port: 18142,
      token: "local-token",
      commands_hash: "catalog-revision",
      catalog_generation: 7,
      aliases: [],
    });
    expect(Object.isFrozen(snapshotDescriptor)).toBe(true);
    expect(Object.isFrozen(snapshotDescriptor.schema)).toBe(true);
    expect(Object.isFrozen(snapshotDescriptor.schema.properties)).toBe(true);
    expect(Object.isFrozen(snapshotRecord)).toBe(true);
    expect(Object.isFrozen(snapshotRecord?.aliases)).toBe(true);
    expect(() => { snapshotDescriptor.schema.properties = {}; }).toThrow(TypeError);
    expect(() => { snapshotRecord!.aliases!.push({ environment: "unity", host_path: "x", canonical_path: "x" }); }).toThrow(TypeError);
  });

  it("does not serialize or inspect heartbeat secrets or path-capable selectors", () => {
    const descriptor = command();
    const projectSelector = "C:/Users/operator/Secret Project";
    const snapshot = InvocationContextSnapshot.create({
      projectSelector,
      project: "game",
      command: descriptor.id,
      descriptor,
      commandsHash: "catalog-revision",
      record: heartbeat({ token: "raw-secret-token" }),
    });

    const serialized = JSON.stringify(snapshot);
    const inspected = inspect(snapshot);
    const identitySerialized = JSON.stringify(snapshot.identity);
    const identityInspected = inspect(snapshot.identity);
    expect(serialized).not.toContain("raw-secret-token");
    expect(inspected).not.toContain("raw-secret-token");
    expect(serialized).not.toContain("project_root");
    expect(inspected).not.toContain("project_root");
    expect(serialized).not.toContain("catalog-revision");
    expect(inspected).not.toContain("catalog-revision");
    for (const output of [serialized, inspected, identitySerialized, identityInspected]) {
      expect(output).not.toContain(projectSelector);
      expect(output).not.toContain("Users");
      expect(output).not.toContain("Secret Project");
    }
  });
});

function command(): CommandDescriptor {
  return {
    id: "scene.inspect",
    title: "Inspect scene",
    description: "Inspect scene",
    package: "com.example.test",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 1_000,
    schema: { type: "object", properties: { nested: { type: "object" } } },
  };
}

function heartbeat(overrides: Partial<HeartbeatRecord> = {}): HeartbeatRecord {
  return {
    schema_version: 1,
    instance_id: "game-a",
    project: "game",
    project_root: "C:/u/game",
    canonical_project_root: "c:/u/game",
    aliases: [],
    unity_version: "6000.0.0f1",
    unity_pid: 12345,
    bridge_host: "127.0.0.1",
    bridge_port: 18142,
    token: "local-token",
    status: "ready",
    commands_hash: "catalog-revision",
    catalog_status: "ready",
    catalog_generation: 7,
    last_heartbeat: "2026-05-12T09:59:30.000Z",
    ...overrides,
  };
}
