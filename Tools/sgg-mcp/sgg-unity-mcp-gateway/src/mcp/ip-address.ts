import { isIP } from "node:net";

export function canonicalizeIpAddress(value: string): string | undefined {
  const family = isIP(value);
  if (family === 4) return value;
  if (family !== 6 || value.includes("%")) return undefined;

  let canonical: string;
  try {
    canonical = new URL(`http://[${value}]/`).hostname.slice(1, -1).toLowerCase();
  } catch {
    return undefined;
  }

  const mapped = /^::ffff:([0-9a-f]{1,4}):([0-9a-f]{1,4})$/.exec(canonical);
  if (!mapped) return canonical;
  const high = Number.parseInt(mapped[1]!, 16);
  const low = Number.parseInt(mapped[2]!, 16);
  return `${high >>> 8}.${high & 0xff}.${low >>> 8}.${low & 0xff}`;
}
