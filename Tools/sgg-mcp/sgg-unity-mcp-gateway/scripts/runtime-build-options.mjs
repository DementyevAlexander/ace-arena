import path from "node:path";

export const GATEWAY_RUNTIME_BANNER = "import { createRequire as __sggCreateRequire } from 'node:module';const require=__sggCreateRequire(import.meta.url);";

export function gatewayRuntimeBuildOptions(root, outfile) {
  return {
    entryPoints: [path.join(root, "src", "cli.ts")],
    outfile,
    bundle: true,
    platform: "node",
    format: "esm",
    target: "node22",
    minify: true,
    legalComments: "external",
    banner: { js: GATEWAY_RUNTIME_BANNER },
    metafile: true,
  };
}
