import { Plugin } from "@opencode/plugin";
import type { Info } from "@opencode/plugin/promise/tool";

import { createPresentationSnapshot, presentationToolAllowlist, type PresentationSnapshot } from "./format.js";

function isMetadataRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}

function presentationMetadata(snapshot: PresentationSnapshot, outcome: "running" | "completed" | "error") {
  return Object.freeze({
    schemaVersion: snapshot.schemaVersion,
    label: snapshot.label,
    outcome,
  });
}

function addPresentationMetadata(target: object, snapshot: PresentationSnapshot, outcome: "completed" | "error"): void {
  try {
    // Only metadata is inspected. Never copy a result or read its content/output/error message.
    const descriptor = Object.getOwnPropertyDescriptor(target, "metadata");
    if (descriptor && !Object.hasOwn(descriptor, "value")) return;
    const metadata: unknown = descriptor?.value;
    const presentation = presentationMetadata(snapshot, outcome);
    if (metadata === undefined) {
      Object.defineProperty(target, "metadata", {
        value: { sggPresentation: presentation }, enumerable: true, configurable: true, writable: true,
      });
    } else if (isMetadataRecord(metadata) && !Object.hasOwn(metadata, "sggPresentation")) {
      Object.defineProperty(metadata, "sggPresentation", {
        value: presentation, enumerable: true, configurable: true, writable: true,
      });
    }
  } catch {
    // Immutable or hostile metadata must not change tool completion or failure.
  }
}

function wrapExecutor(original: Info["execute"], tool: string, allowlist: ReadonlyMap<string, string>): Info["execute"] {
  return async function (this: unknown, input, context) {
    try {
      const snapshot = createPresentationSnapshot(tool, input, allowlist);
      if (snapshot) await context.progress({ sggPresentation: presentationMetadata(snapshot, "running") });
    } catch {
      // Presentation/progress failure never prevents the original executor from running.
    }
    return Reflect.apply(original, this, [input, context]);
  };
}

export default Plugin.define({
  id: "sgg-unity.presentation",
  async setup(ctx) {
    const allowlist = presentationToolAllowlist(ctx.options.serverPrefixes);
    await ctx.tool.hook("execute.after", (event) => {
      try {
        const snapshot = createPresentationSnapshot(event.tool, event.input, allowlist);
        if (!snapshot) return;
        if (event.status === "completed") addPresentationMetadata(event.result, snapshot, "completed");
        if (event.status === "error") addPresentationMetadata(event.error, snapshot, "error");
      } catch {
        // Presentation is fail-open; no canonical payload fields or permissions are touched.
      }
    });
    await ctx.tool.transform((editor) => {
      for (const tool of editor.list()) {
        if (!allowlist.has(tool.id)) continue;
        editor.update(tool.id, (draft) => {
          draft.execute = wrapExecutor(draft.execute, tool.id, allowlist);
        });
      }
    });
  },
});
