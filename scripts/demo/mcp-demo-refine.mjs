#!/usr/bin/env node
/** Deterministic refinement request used after the initial README demo draft. */
import { spawn } from "node:child_process";
import { createInterface } from "node:readline";

const child = spawn("node", ["mcp/pixelpair-mcp.mjs"], {
  cwd: process.cwd(),
  stdio: ["pipe", "pipe", "pipe"],
});
const lines = createInterface({ input: child.stdout });
const timeout = setTimeout(() => finish(new Error("MCP refinement timed out.")), 12_000);
let done = false;

function finish(error, response) {
  if (done) return;
  done = true;
  clearTimeout(timeout);
  lines.close();
  child.kill();
  if (error || response?.result?.isError) {
    console.error(`MCP refinement error: ${error?.message || "unknown error"}`);
    process.exitCode = 1;
    return;
  }
  console.log("MCP → recolor eyes");
}

child.once("error", (error) => finish(error));
child.stderr.on("data", () => {});
lines.on("line", (line) => {
  try {
    const message = JSON.parse(line);
    if (message.id === 1) finish(null, message);
  } catch {
    // Ignore non-JSON process output.
  }
});

child.stdin.write(`${JSON.stringify({
  jsonrpc: "2.0",
  id: 1,
  method: "tools/call",
  params: {
    name: "apply_operations",
    arguments: { operations: [{ op: "recolor", from: "#60A5FA", to: "#22C55E", tolerance: 0 }] },
  },
})}\n`);
child.stdin.end();
