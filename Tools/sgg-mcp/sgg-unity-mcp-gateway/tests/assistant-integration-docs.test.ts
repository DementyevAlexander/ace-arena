import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

describe("assistant integration docs", () => {
  it("documents Unity Assistant UserSettings config and exact SGGMcp tool ids", () => {
    const docs = fs.readFileSync(path.join(process.cwd(), "docs", "en", "connect", "unity-ai-assistant.md"), "utf8");
    const ruDocs = fs.readFileSync(path.join(process.cwd(), "docs", "ru", "connect", "unity-ai-assistant.md"), "utf8");

    for (const body of [docs, ruDocs]) {
      expect(body).toContain("UserSettings/mcp.json");
      expect(body).toContain('"enabled": true');
      expect(body).toContain('"type": "stdio"');
      expect(body).toContain("%LOCALAPPDATA%/SGG/UnityMcpGateway/config/gateway.json");
      expect(body).toContain("sgg_unity_projects");
      expect(body).toContain("sgg_unity_describe");
      expect(body).toContain("sgg_unity_call");
      expect(body).toContain("sgg_unity_batch");
      expect(body).toContain("sgg_unity_job");
      expect(body).toContain("sgg_unity_logs");
      expect(body).toMatch(/Do not create PascalCase aliases|Не создавайте PascalCase aliases/);
      expect(body).toMatch(/command catalog ids passed to `sgg_unity_call`|command ids внутри `sgg_unity_call`/);
      expect(body).toContain("official.<toolName>");
      expect(body).toMatch(/remain behind `sgg_unity_call`|вызываются через `sgg_unity_call`/);
    }
  });
});
