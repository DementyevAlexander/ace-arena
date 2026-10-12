import { describe, expect, it } from "vitest";

import { ClientRootsRegistry, clientRootHash, clientRootHashes } from "../src/mcp/client-roots.js";

describe("MCP client roots evidence", () => {
  it("normalizes Windows and WSL file roots to the same bounded hash", () => {
    expect(clientRootHashes([
      { uri: "file:///C:/Work/game" },
      { uri: "file:///mnt/c/Work/game" },
      { uri: "https://example.invalid/root" },
    ])).toEqual([clientRootHash("c:/Work/game")]);
  });

  it("never exposes raw roots and keeps per-session observations distinct", () => {
    let now = Date.parse("2026-07-26T00:00:00.000Z");
    const registry = new ClientRootsRegistry({ now: () => now });
    registry.record({ observation_id: "session-a", status: "observed", root_hashes: [clientRootHash("c:/Work/a")] });
    registry.record({ observation_id: "session-b", status: "observed", root_hashes: [clientRootHash("c:/Work/b")] });

    const health = registry.health();
    expect(health.schema_version).toBe(1);
    expect(health.observations).toHaveLength(2);
    expect(JSON.stringify(health)).not.toContain("c:/Work/");
    expect(health.observations.map((entry) => entry.observation_id)).toEqual(["session-a", "session-b"]);

    now += 30 * 60_000;
    expect(registry.health().observations).toEqual([]);
  });

  it("renews active observations and removes closed ones", () => {
    let now = Date.parse("2026-07-26T00:00:00.000Z");
    const registry = new ClientRootsRegistry({ now: () => now });
    registry.record({ observation_id: "session-a", status: "observed", root_hashes: [] });
    now += 29 * 60_000;
    expect(registry.touch("session-a")).toBe(true);
    now += 29 * 60_000;
    expect(registry.health().observations).toHaveLength(1);
    registry.remove("session-a");
    expect(registry.health().observations).toEqual([]);
  });

  it("bounds root count and rejects UNC, non-file and malformed URIs", () => {
    const roots = Array.from({ length: 20 }, (_, index) => ({ uri: `file:///C:/Work/${index}` }));
    roots.unshift({ uri: "file://server/share" }, { uri: "not a URI" }, { uri: "https://example.invalid" });
    expect(clientRootHashes(roots)).toHaveLength(5);
  });
});
