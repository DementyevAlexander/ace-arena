import { tsImport } from "tsx/esm/api";
const { run } = await tsImport("./controlled-worker.ts", { parentURL: import.meta.url, tsconfig: false });
run();
