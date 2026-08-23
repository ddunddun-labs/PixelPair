#!/usr/bin/env node
/**
 * Deterministic MCP request used by the README demo.
 * It never includes a token, local path, or account data in terminal output.
 */
import { spawn } from "node:child_process";
import { createInterface } from "node:readline";

const operations = [
  { op: "fill_rect", x: 18, y: 16, w: 28, h: 32, color: "#1E293B" },
  { op: "round_corners", x: 18, y: 16, w: 28, h: 32, radius: 5, fill_color: "#0F172A" },
  { op: "fill_rect", x: 21, y: 20, w: 22, h: 18, color: "#334155" },
  { op: "fill_rect", x: 23, y: 23, w: 7, h: 7, color: "#60A5FA" },
  { op: "fill_rect", x: 34, y: 23, w: 7, h: 7, color: "#60A5FA" },
  { op: "fill_rect", x: 25, y: 25, w: 3, h: 3, color: "#E0F2FE" },
  { op: "fill_rect", x: 36, y: 25, w: 3, h: 3, color: "#E0F2FE" },
  { op: "fill_rect", x: 24, y: 34, w: 16, h: 4, color: "#A855F7" },
  { op: "fill_rect", x: 28, y: 40, w: 8, h: 5, color: "#F43F5E" },
  { op: "draw_line", x0: 18, y0: 34, x1: 12, y1: 42, color: "#64748B", pixel_perfect: true },
  { op: "draw_line", x0: 45, y0: 34, x1: 51, y1: 42, color: "#64748B", pixel_perfect: true },
  { op: "draw_circle", x: 31, y: 12, radius: 3, color: "#FBBF24", fill: true },
];

const child = spawn("node", ["mcp/pixelpair-mcp.mjs"], {
  cwd: process.cwd(),
  stdio: ["pipe", "pipe", "pipe"],
});

const lines = createInterface({ input: child.stdout });
const timeout = setTimeout(() => finish(new Error("MCP 응답 시간이 초과됐다.")), 12_000);
let done = false;

function finish(error, response) {
  if (done) return;
  done = true;
  clearTimeout(timeout);
  lines.close();
  child.kill();
  if (error) {
    console.error(`MCP error: ${error.message}`);
    process.exitCode = 1;
    return;
  }
  const text = response?.result?.content?.find((item) => item.type === "text")?.text;
  if (response?.result?.isError) {
    console.error(`MCP error: ${text || "unknown error"}`);
    process.exitCode = 1;
    return;
  }
  console.log("MCP → apply_operations");
  console.log(text || "아이콘 완성");
}

child.once("error", (error) => finish(error));
child.stderr.on("data", () => {}); // MCP progress notifications are intentionally omitted from the demo terminal.
lines.on("line", (line) => {
  try {
    const message = JSON.parse(line);
    if (message.id === 1) finish(null, message);
  } catch {
    // Ignore non-JSON process output; MCP JSON-RPC is the source of truth.
  }
});

child.stdin.write(`${JSON.stringify({
  jsonrpc: "2.0",
  id: 1,
  method: "tools/call",
  params: { name: "apply_operations", arguments: { operations } },
})}\n`);
child.stdin.end();
