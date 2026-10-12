import fs from "node:fs";
import path from "node:path";

import { acquireStorageLock, currentProcessOwner, durableRenameFile, durableUnlinkFile, ensureSecureDirectory, ownerIdentityTag, writeNewFileDurable } from "../../src/security/safe-storage.js";

const [mode, root, value] = process.argv.slice(2);
if (!mode || !root) throw new Error("mode and root are required");

if (mode === "lock" || mode === "lock-release-marker-crash" || mode === "lock-reclaim-marker-crash"
  || mode === "lock-release-gate-crash" || mode === "lock-release-cleanup-crash" || mode === "lock-release-cleanup-hold"
  || mode === "lock-dead-gate-retirement-crash" || mode === "lock-dead-gate-retirement-hold" || mode === "lock-block-signal" || mode === "lock-signal") {
  const maxAttempts = 5;
  let lock: ReturnType<typeof acquireStorageLock> | undefined;
  let gateCount = 0;
  for (let attempt = 0; attempt < maxAttempts && !lock; attempt += 1) {
    try {
      lock = acquireStorageLock(root, ".test.lock", {
        timeoutMs: 20_000,
        staleAfterMs: Number(process.env.SGG_TEST_STALE_AFTER_MS ?? 10),
        ...(mode === "lock-block-signal" ? { afterBlockedTransition: () => {
          if (!value) throw new Error("blocked-transition signal path is required");
          fs.writeFileSync(value, "blocked");
        } } : {}),
        ...(mode === "lock-release-gate-crash" ? { afterTransitionGate: () => { if (++gateCount === 2) process.exit(0); } } : {}),
        ...(mode === "lock-release-marker-crash" || mode === "lock-reclaim-marker-crash" ? { afterTransitionMarker: () => process.exit(0) } : {}),
        ...(mode === "lock-release-cleanup-crash" ? { afterTransitionCleanup: () => process.exit(0) } : {}),
        ...(mode === "lock-dead-gate-retirement-crash" ? { afterDeadTransitionGateRetirement: () => process.exit(0) } : {}),
        ...(mode === "lock-dead-gate-retirement-hold" ? { afterDeadTransitionGateRetirement: () => {
          if (!value) throw new Error("dead-gate retirement signal path is required");
          fs.writeFileSync(value, "retired");
          while (!fs.existsSync(`${value}.continue`)) Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 5);
        } } : {}),
        ...(mode === "lock-release-cleanup-hold" ? { afterTransitionCleanup: () => {
          if (!value) throw new Error("release signal path is required");
          while (!fs.existsSync(value)) Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 5);
        } } : {}),
      });
    }
    catch (error) {
      if (attempt === maxAttempts - 1 || !(error instanceof Error) || !error.message.startsWith("Storage lock unavailable.")) throw error;
    }
  }
  if (!lock) throw new Error("Storage lock unavailable after bounded test retry");
  if (mode === "lock-reclaim-marker-crash") throw new Error("Expected reclaim marker crash before acquisition");
  process.stdout.write("acquired\n");
  if (mode.startsWith("lock-release-")) lock.release();
  if (value === "crash") process.exit(0);
  if (mode === "lock-signal") {
    if (!value) throw new Error("lock release signal path is required");
    const timer = setInterval(() => {
      if (!fs.existsSync(value)) return;
      clearInterval(timer);
      lock!.release();
      process.exit(0);
    }, 5);
  } else {
  setTimeout(() => { lock.release(); process.exit(0); }, mode === "lock-block-signal" || mode === "lock-dead-gate-retirement-hold" ? 50 : Number(value ?? 100));
  }
} else if (mode === "transition-old-resumer-hold") {
  process.stdout.write(`${JSON.stringify(currentProcessOwner())}\n`);
  let renamed = false;
  const timer = setInterval(() => {
    if (!renamed && fs.existsSync(path.join(root, "old-resumer.rename"))) {
      const active = path.join(root, ".test.lock");
      const markerName = fs.readdirSync(active).find((name) => name.startsWith("transition-") && name.endsWith(".json"));
      if (!markerName) throw new Error("transition marker is required");
      const transition = JSON.parse(fs.readFileSync(path.join(active, markerName), "utf8"));
      fs.renameSync(active, `${active}.tomb-${transition.transition_nonce}`);
      renamed = true;
    }
    if (!renamed || !fs.existsSync(path.join(root, "old-resumer.exit"))) return;
    clearInterval(timer);
    process.exit(0);
  }, 5);
} else if (mode === "transition-owner-hold") {
  if (!value) throw new Error("transition-owner signal path is required");
  process.stdout.write(`${JSON.stringify(currentProcessOwner())}\n`);
  const timer = setInterval(() => {
    if (!fs.existsSync(value)) return;
    clearInterval(timer);
    process.exit(0);
  }, 5);
} else if (mode === "claim") {
  if (!value) throw new Error("claim source is required");
  ensureSecureDirectory(root, "claims");
  const owner = currentProcessOwner();
  const destination = path.join(root, "claims", `active-c1-${owner.pid}-${ownerIdentityTag(owner)}-${Date.parse(owner.acquired_at)}-${owner.nonce}.json`);
  durableRenameFile(root, value, destination);
  process.stdout.write(`${destination}\n`);
  const releaseFile = process.env.SGG_TEST_RELEASE_FILE;
  const holdMs = Number(process.env.SGG_TEST_HOLD_MS ?? 0);
  if (releaseFile) {
    const timer = setInterval(() => {
      if (!fs.existsSync(releaseFile)) return;
      clearInterval(timer);
      process.exit(0);
    }, 5);
  } else if (holdMs > 0) setTimeout(() => process.exit(0), holdMs);
  else process.exit(0);
} else if (mode === "delete-crash") {
  if (!value) throw new Error("delete source is required");
  durableUnlinkFile(root, value, { afterRename: () => process.exit(0) });
} else if (mode === "delete-metadata-temp-crash" || mode === "delete-metadata-link-crash") {
  if (!value) throw new Error("delete source is required");
  durableUnlinkFile(root, value, { metadataPublish: mode.endsWith("temp-crash") ? { afterTempFsync: () => process.exit(0) } : { afterPublish: () => process.exit(0) } });
} else if (mode === "publish-temp-crash" || mode === "publish-link-crash") {
  if (!value) throw new Error("publication target is required");
  writeNewFileDurable(root, value, "publication", mode.endsWith("temp-crash") ? { afterTempFsync: () => process.exit(0) } : { afterPublish: () => process.exit(0) });
} else if (mode === "lock-owner-temp-crash" || mode === "lock-owner-link-crash") {
  if (!value) throw new Error("lock owner target is required");
  const owner = currentProcessOwner();
  writeNewFileDurable(root, value, `${JSON.stringify(owner)}\n`, mode.endsWith("temp-crash") ? { afterTempFsync: () => process.exit(0) } : { afterPublish: () => process.exit(0) });
} else if (mode === "lock-owner-write") {
  if (!value) throw new Error("lock owner target is required");
  writeNewFileDurable(root, value, `${JSON.stringify(currentProcessOwner())}\n`);
} else {
  throw new Error("unknown mode");
}
