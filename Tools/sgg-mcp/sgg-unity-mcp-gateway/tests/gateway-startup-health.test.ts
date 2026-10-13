import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const root = process.cwd();

describe("Gateway startup health", () => {
  it("uses cached store observations instead of lock-taking maintenance", () => {
    const cli = fs.readFileSync(path.join(root, "src", "cli.ts"), "utf8");
    const health = cli.slice(cli.indexOf("health: () => {"), cli.indexOf("logger: operationalLogger", cli.indexOf("health: () => {")));

    expect(health).toContain("context.audit.snapshotStatus()");
    expect(health).toContain("context.approvalStore.snapshotStatus()");
    expect(health).not.toContain("context.audit.status()");
    expect(health).not.toContain("context.approvalStore.status(");
  });
});
