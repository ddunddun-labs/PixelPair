#!/usr/bin/env node
/** Deterministic final refinement request used by the README demo. */
import { spawn } from "node:child_process";
import { createInterface } from "node:readline";

const child = spawn("node", ["mcp/pixelpair-mcp.mjs"], {
  cwd: process.cwd(),
  stdio: ["pipe", "pipe", "pipe"],
});
const lines = createInterface({ input: child.stdout });
const timeout = setTimeout(() => finish(new Error("MCP final refinement timed out.")), 12_000);
let done = false;

function finish(error, response) {
  if (done) return;
  done = true;
  clearTimeout(timeout);
  lines.close();
  child.kill();
  if (error || response?.result?.isError) {
    console.error(`MCP final refinement error: ${error?.message || "unknown error"}`);
    process.exitCode = 1;
    return;
  }
  console.log("MCP → recolor mouth");
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
    arguments: { operations: [{ op: "recolor", from: "#F43F5E", to: "#F97316", tolerance: 0 }] },
  },
})}\n`);
child.stdin.end();
